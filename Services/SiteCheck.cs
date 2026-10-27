using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Dnex.Services;

public enum SiteVerdict { Ok, NoDns, Blocked, Throttled, Refused, Protected, Error }

/// <summary>Итог проверки сайта: вердикт, строки подробностей и что делать.</summary>
public sealed record SiteReport(string Host, SiteVerdict Verdict, IReadOnlyList<string> Lines, string Advice);

/// <summary>
/// «Почему не открывается сайт»: адрес через DNS системы, владелец адреса, загрузка страницы и ее большого файла
/// (стили или скрипт) с отслеживанием обрыва. Замедление Cloudflare у российских провайдеров выглядит так:
/// первые 15–30 КБ приходят сразу, дальше соединение замирает. DNS это не лечит, нужен обход DPI (zapret).
/// </summary>
public static class SiteCheck
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(6);

    /// <summary>Домен из того, что ввел пользователь (ссылка или имя).</summary>
    public static string HostOf(string input)
    {
        var s = input.Trim();
        if (!s.Contains("://")) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && Uri.CheckHostName(u.Host) == UriHostNameType.Dns ? u.Host.ToLowerInvariant() : "";
    }

    /// <summary>Загрузка с отслеживанием обрыва: сколько байт пришло, дочиталось ли до конца, сколько заняло.</summary>
    public sealed record Fetch(int Status, long Bytes, bool Complete, bool Stalled, long Ms, string Body, string? Error);

    public static async Task<Fetch> FetchAsync(HttpClient http, string url, long limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, startCts.Token);
            var total = resp.Content.Headers.ContentLength;
            await using var st = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[16384];
            var ms = new MemoryStream();
            while (ms.Length < limit)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(Stall);
                int n;
                try { n = await st.ReadAsync(buf, readCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new Fetch((int)resp.StatusCode, ms.Length, false, true, sw.ElapsedMilliseconds, Text(ms), null);
                }
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
            bool complete = total == null || ms.Length >= total || ms.Length < limit;
            return new Fetch((int)resp.StatusCode, ms.Length, complete, false, sw.ElapsedMilliseconds, Text(ms), null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new Fetch(0, 0, false, false, sw.ElapsedMilliseconds, "", ConnectionProbe.Friendly(e));
        }

        static string Text(MemoryStream m) => System.Text.Encoding.UTF8.GetString(m.GetBuffer(), 0, (int)Math.Min(m.Length, 200_000));
    }

    public static async Task<SiteReport> CheckAsync(string input, CancellationToken ct = default)
    {
        var host = HostOf(input);
        var lines = new List<string>();
        if (host.Length == 0) return new SiteReport(input, SiteVerdict.Error, lines, "Не похоже на адрес сайта");
        // Служебные домены хранилищ: на самом домене сайта нет, он не отвечает даже без блокировок. Проверять надо адрес с поддоменом
        if (ServiceRoots.TryGetValue(host, out var example))
            return new SiteReport(host, SiteVerdict.Error, lines,
                $"{host} сам по себе не сайт, а домен хранилища: он не отвечает даже без всяких блокировок. Проверьте полный адрес, например {example}");

        // 1. Адрес через DNS системы (как у браузера)
        IPAddress[] ips;
        try { ips = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { ips = Array.Empty<IPAddress>(); }
        if (ips.Length == 0)
            return new SiteReport(host, SiteVerdict.NoDns, lines, "DNS не нашел адрес сайта. Проверьте адрес или смените общий DNS");
        var org = await GeoLocation.OrgAsync(ips[0].ToString(), ct) ?? "";
        bool cloudflare = org.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase);
        lines.Add($"DNS: {string.Join(", ", ips.Take(3))}{(org.Length > 0 ? $" ({org})" : "")}");

        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = TimeSpan.FromSeconds(8), AutomaticDecompression = DecompressionMethods.None });
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");

        // 2. Страница
        var page = await FetchAsync(http, $"https://{host}/", 2_000_000, ct);
        if (page.Error != null)
        {
            lines.Add($"Соединение: {page.Error}");
            // DNS системы может подменять адрес сайта своим прокси (так делают Smart DNS вроде Xbox DNS и для YouTube).
            // Тогда сайт идет через чужой прокси: когда он не отвечает, zapret бессилен, помогает только смена DNS
            if (await SubstitutedAsync(host, ips, org, ct) is { } realOrg)
                return new SiteReport(host, SiteVerdict.Blocked, lines,
                    $"DNS системы подменяет адрес сайта на свой прокси ({(org.Length > 0 ? org : ips[0].ToString())} вместо {realOrg}), и прокси не отвечает. " +
                    "zapret тут не поможет: верните автоматический DNS или подключитесь через CHIMERION");
            return new SiteReport(host, SiteVerdict.Blocked, lines, ZapretRunning?.Invoke() == true
                ? "Похоже, провайдер блокирует адрес сайта, а zapret его не обходит. Нажмите «Подобрать стратегию» в «Telegram и YouTube»"
                : "Похоже, провайдер блокирует адрес сайта. DNS не поможет, поможет zapret в «Telegram и YouTube»");
        }
        lines.Add($"Страница: код {page.Status}, {Kb(page.Bytes)} за {page.Ms / 1000.0:0.0} с{(page.Stalled ? ", загрузка оборвалась" : "")}");
        if (page.Stalled) return Throttled(host, lines, page.Bytes, cloudflare);
        // Страница защиты Cloudflare от ботов: файлы на ней чужие (cf.errors.css), по ним о самом сайте судить нельзя.
        // Проверяем то, что можно: большой файл с того же адреса через служебную страницу Cloudflare не получить,
        // поэтому честно говорим, что программа сайт как браузер загрузить не может
        if (page.Status is 403 or 503 && Regex.IsMatch(page.Body, @"cf-error|cf-wrapper|Attention Required|Just a moment|challenge-platform|cf_chl", RegexOptions.IgnoreCase))
            return new SiteReport(host, SiteVerdict.Protected, lines,
                "Соединение есть, но сайт закрыт проверкой «вы не робот», дальше программа не видит. Если в браузере он не догружается, поможет zapret");
        if (page.Status is 403 or 451 && Regex.IsMatch(page.Body, "country|region|страна|регион|not available|unavailable", RegexOptions.IgnoreCase))
            return new SiteReport(host, SiteVerdict.Refused, lines, "Сайт не пускает из вашей страны. Поможет только VPN или прокси в другой стране");

        // 3. Большой файл страницы (стили или скрипт с того же сайта): обрыв часто начинается после 15–30 КБ
        var assets = Regex.Matches(page.Body, @"(?:src|href)=""(/[^""/][^""]*?\.(?:css|js))""").Select(m => m.Groups[1].Value).Distinct().Take(4).ToList();
        foreach (var a in assets)
        {
            var f = await FetchAsync(http, $"https://{host}{a}", 3_000_000, ct);
            lines.Add($"Файл {a.Split('/').Last()}: {(f.Error ?? $"код {f.Status}, {Kb(f.Bytes)} за {f.Ms / 1000.0:0.0} с")}{(f.Stalled ? ", загрузка оборвалась" : "")}");
            if (f.Stalled) return Throttled(host, lines, f.Bytes, cloudflare);
        }
        return new SiteReport(host, SiteVerdict.Ok, lines,
            page.Status < 400 ? "Сайт загружается нормально. Если в браузере не открывается, обновите страницу без кэша (Ctrl+F5) или проверьте расширения"
                              : $"Сайт ответил кодом {page.Status}. Сеть в порядке, ограничение на стороне сайта");
    }

    private static SiteReport Throttled(string host, List<string> lines, long bytes, bool cloudflare) =>
        new(host, SiteVerdict.Throttled, lines,
            $"Загрузка обрывается на {Kb(bytes)}: " + (cloudflare ? "провайдер замедляет сайты на Cloudflare. " : "провайдер замедляет сайт. ") +
            (ZapretRunning?.Invoke() == true
                ? "zapret включен, но не помогает: добавьте сайт в «Свои сайты» или нажмите «Подобрать стратегию»"
                : "DNS не поможет, поможет zapret в «Telegram и YouTube»"));

    /// <summary>
    /// Подменяет ли DNS системы адрес сайта: ни один адрес не совпал с эталонным DoH, и сеть-владелец другая.
    /// Возвращает владельца настоящих адресов или null, если подмены нет или эталон не ответил.
    /// </summary>
    private static async Task<string?> SubstitutedAsync(string host, IPAddress[] ips, string org, CancellationToken ct)
    {
        try
        {
            var (real, _) = await DnsWire.ResolveDohAsync(new Models.AppSettings().ReferenceDoh, host, TimeSpan.FromSeconds(6), ct);
            if (real.Count == 0 || real.Intersect(ips).Any()) return null;
            var realOrg = await GeoLocation.OrgAsync(real[0].ToString(), ct) ?? "";
            return realOrg.Length > 0 && !string.Equals(realOrg, org, StringComparison.OrdinalIgnoreCase) ? realOrg : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Работает ли сейчас zapret: тогда совет другой (подобрать стратегию, а не включить). Задает главное окно.</summary>
    public static Func<bool>? ZapretRunning;

    /// <summary>Домены хранилищ без сайта на самом домене и пример настоящего адреса для проверки.</summary>
    private static readonly Dictionary<string, string> ServiceRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["r2.dev"] = "pub-6327ca42c2344c2ab42e1c16868d6c5a.r2.dev/latest.json (канал обновлений CHIMERION)",
        ["workers.dev"] = "chimerion-crowd.chimerion.workers.dev",
        ["pages.dev"] = "имя-проекта.pages.dev",
        ["githubusercontent.com"] = "raw.githubusercontent.com",
    };

    private static string Kb(long bytes) => bytes < 1024 ? $"{bytes} Б" : $"{bytes / 1024} КБ";
}
