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
            return new SiteReport(host, SiteVerdict.Blocked, lines,
                "Сайт не отвечает по адресу, который дал DNS: скорее всего, провайдер блокирует его адрес. DNS это не исправит. Попробуйте zapret («Свои сайты» и обход по адресам) или VPN");
        }
        lines.Add($"Страница: код {page.Status}, {Kb(page.Bytes)} за {page.Ms / 1000.0:0.0} с{(page.Stalled ? ", загрузка оборвалась" : "")}");
        if (page.Stalled) return Throttled(host, lines, page.Bytes, cloudflare);
        // Страница защиты Cloudflare от ботов: файлы на ней чужие (cf.errors.css), по ним о самом сайте судить нельзя.
        // Проверяем то, что можно: большой файл с того же адреса через служебную страницу Cloudflare не получить,
        // поэтому честно говорим, что программа сайт как браузер загрузить не может
        if (page.Status is 403 or 503 && Regex.IsMatch(page.Body, @"cf-error|cf-wrapper|Attention Required|Just a moment|challenge-platform|cf_chl", RegexOptions.IgnoreCase))
            return new SiteReport(host, SiteVerdict.Protected, lines,
                "Сайт закрыт проверкой Cloudflare «вы не робот»: программа не может загрузить его так, как браузер, и оценить загрузку. " +
                "Соединение с сайтом есть. Если в браузере сайт не догружается, это замедление: помогает zapret («Сайты для обхода» или обход по адресам)");
        if (page.Status is 403 or 451 && Regex.IsMatch(page.Body, "country|region|страна|регион|not available|unavailable", RegexOptions.IgnoreCase))
            return new SiteReport(host, SiteVerdict.Refused, lines, "Сайт сам не пускает из вашей страны. DNS и zapret тут не помогут, нужен VPN или прокси в другой стране");

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
            $"Загрузка обрывается на {Kb(bytes)}: " + (cloudflare ? "так провайдеры замедляют сайты на Cloudflare. " : "похоже на замедление провайдером. ") +
            "DNS это не исправит, все DNS ведут на те же адреса. Помогает zapret: включите его в «Telegram и YouTube», добавьте сайт в «Свои сайты» " +
            "или включите «Обход по адресам (IPSet)»");

    private static string Kb(long bytes) => bytes < 1024 ? $"{bytes} Б" : $"{bytes / 1024} КБ";
}
