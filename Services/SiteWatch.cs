using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>
/// Наблюдение за сайтами, которыми пользуется человек. Программа смотрит в кэш DNS Windows (какие имена недавно
/// запрашивались), собирает из них сайты и в фоне проверяет, открываются ли они. Если сайт перестал открываться,
/// разбирается почему: лежит у всех (проверка с узла Cloudflare), закрыт для России, режет провайдер (нужен zapret),
/// DNS не находит адрес или Smart DNS перестал его подменять.
///
/// Все происходит на компьютере. Наружу имя сайта уходит только когда он не открылся: на сервер общей карты, чтобы
/// проверить его снаружи. Список сайтов хранится в sites.json (только имена и итоги проверок) и не отправляется.
/// </summary>
public sealed class SiteWatch
{
    public const string FileName = "sites.json";
    /// <summary>Сколько сайтов проверять за раз: остальные дождутся следующего круга.</summary>
    public const int PerRound = 16;
    /// <summary>Сайт попадает под наблюдение, когда встретился в кэше хотя бы в стольких кругах.</summary>
    public const int SeenToWatch = 2;
    /// <summary>Сколько неудач подряд считать падением (разовая неудача бывает у любого сайта).</summary>
    public const int FailsToAlert = 2;
    /// <summary>Забывать сайт, если его не открывали столько дней.</summary>
    public const int ForgetDays = 14;
    /// <summary>Сколько сайтов держать в списке.</summary>
    public const int MaxSites = 300;

    public enum Verdict { Unknown, Ok, NoDns, DownForAll, Blocked, Refused, NotProxied }

    public sealed class Site
    {
        public string Host { get; set; } = "";
        public int Seen { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime? LastChecked { get; set; }
        public Verdict State { get; set; }
        public int Fails { get; set; }
        /// <summary>Хоть раз открывался: о падении сообщаем только для таких (трекеры и мертвые адреса не шумят).</summary>
        public bool EverOk { get; set; }
        public bool Down { get; set; }
        public string? Detail { get; set; }
    }

    public Dictionary<string, Site> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Сайты, за которыми пользователь попросил не следить.</summary>
    public HashSet<string> Ignored { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SiteWatch Load()
    {
        try
        {
            var w = Storage.LoadOrCreate(FileName, () => new SiteWatch());
            w.Sites = new(w.Sites, StringComparer.OrdinalIgnoreCase);
            w.Ignored = new(w.Ignored, StringComparer.OrdinalIgnoreCase);
            return w;
        }
        catch (Exception) { return new SiteWatch(); }
    }

    public void Save() { try { Storage.Save(FileName, this); } catch (Exception) { } }

    // ---------- Какие сайты открывались ----------

    /// <summary>Служебные имена и сети, которые сайтами не являются и проверять их незачем.</summary>
    private static readonly string[] SkipSuffixes =
    {
        ".local", ".lan", ".home", ".arpa", ".internal", ".localdomain", ".corp", ".workgroup",
        // Адреса проверки интернета (Steam, Android, Apple, Firefox, GNOME): сайтами не являются
        "steamconnecttest.com", "connectivitycheck.gstatic.com", "connectivitycheck.android.com", "captive.apple.com",
        "detectportal.firefox.com", "nmcheck.gnome.org", "network-test.debian.org", "clients3.google.com",
        "msftncsi.com", "msftconnecttest.com", "windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com",
        "events.data.microsoft.com", "telemetry.microsoft.com", "wns.windows.com", "chimerion.workers.dev",
        // Служебные сети CDN и облаков: в кэш попадают как цели CNAME, сайтом не являются и по https:// не открываются
        "akamaiedge.net", "akamai.net", "akamaized.net", "edgekey.net", "edgesuite.net", "cloudfront.net", "fastly.net",
        "fastlylb.net", "cdn.cloudflare.net", "trafficmanager.net", "azureedge.net", "azurefd.net", "msedge.net", "1e100.net",
        "googlevideo.com", "gvt1.com", "gvt2.com", "apple-dns.net", "aaplimg.com", "akadns.net", "llnwd.net", "cdn77.org",
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "app-measurement.com", "scorecardresearch.com",
    };
    /// <summary>Адрес ни разу не открылся за столько проверок: это не сайт (служебный адрес), больше не проверяем.</summary>
    public const int GiveUpFails = 3;

    /// <summary>Вторые уровни, под которыми регистрируют домены (co.uk, com.ru): сайт там на уровень глубже.</summary>
    private static readonly HashSet<string> SecondLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "com.au", "net.au", "co.jp", "com.br", "com.cn", "com.tr", "co.kr", "co.in", "com.ua",
        "com.ru", "net.ru", "org.ru", "pp.ru", "msk.ru", "spb.ru", "co.il", "com.mx", "com.ar", "co.za", "com.sg", "com.hk",
        "github.io", "vercel.app", "netlify.app", "pages.dev", "herokuapp.com", "blogspot.com",
    };

    /// <summary>
    /// Сайт из имени в кэше: «static.cdn.example.co.uk» → «example.co.uk». Null — служебное имя, адрес, не сайт.
    /// Проверяется сам сайт, а не каждый его поддомен: так в списке одна строка на сайт.
    /// </summary>
    public static string? SiteOf(string name)
    {
        var h = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length < 4 || h.Length > 253 || Uri.CheckHostName(h) != UriHostNameType.Dns || !h.Contains('.')) return null;
        if (IPAddress.TryParse(h, out _)) return null;
        if (SkipSuffixes.Any(s => h == s.TrimStart('.') || h.EndsWith(s.StartsWith('.') ? s : "." + s) || h == s)) return null;
        var parts = h.Split('.');
        if (parts.Length < 2 || parts[^1].Length < 2 || parts[^1].All(char.IsDigit)) return null;
        int take = parts.Length >= 3 && SecondLevels.Contains($"{parts[^2]}.{parts[^1]}") ? 3 : 2;
        return string.Join('.', parts[^Math.Min(take, parts.Length)..]);
    }

    /// <summary>Отметить имена из кэша DNS: сайты встречены в этом круге. Старые и лишние сайты забываются.</summary>
    public void Observe(IEnumerable<string> names, DateTime now)
    {
        foreach (var site in names.Select(SiteOf).Where(s => s != null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Ignored.Contains(site!)) continue;
            if (!Sites.TryGetValue(site!, out var s)) Sites[site!] = s = new Site { Host = site! };
            s.Seen++;
            s.LastSeen = now;
        }
        foreach (var old in Sites.Values.Where(s => now - s.LastSeen > TimeSpan.FromDays(ForgetDays)).Select(s => s.Host).ToList()) Sites.Remove(old);
        if (Sites.Count > MaxSites)
            foreach (var extra in Sites.Values.OrderBy(s => s.LastSeen).ThenBy(s => s.Seen).Take(Sites.Count - MaxSites).Select(s => s.Host).ToList())
                Sites.Remove(extra);
    }

    /// <summary>Что проверить в этом круге: сайты под наблюдением, давно не проверенные первыми; упавшие в каждом круге.</summary>
    public List<Site> Due(DateTime now, TimeSpan every) =>
        Sites.Values.Where(s => s.Seen >= SeenToWatch && !Ignored.Contains(s.Host) && !IsNotSite(s))
             .Where(s => s.Fails > 0 || s.LastChecked == null || now - s.LastChecked >= every)
             .OrderByDescending(s => s.Fails > 0).ThenBy(s => s.LastChecked ?? DateTime.MinValue)
             .Take(PerRound).ToList();

    /// <summary>Ни разу не открылся за GiveUpFails проверок: служебный адрес, а не сайт. Не проверяется и не показывается.</summary>
    public static bool IsNotSite(Site s) => !s.EverOk && s.Fails >= GiveUpFails;

    public sealed record Change(Site Site, bool Down);

    /// <summary>Итог проверки сайта. Возвращает изменение для уведомления (упал или снова открывается) или null.</summary>
    public Change? Apply(Site s, Verdict v, string? detail, DateTime now)
    {
        s.LastChecked = now;
        s.State = v;
        s.Detail = detail;
        if (v == Verdict.Ok)
        {
            bool was = s.Down;
            s.Fails = 0; s.Down = false; s.EverOk = true;
            return was ? new Change(s, false) : null;
        }
        s.Fails++;
        if (!s.Down && s.EverOk && s.Fails >= FailsToAlert)
        {
            s.Down = true;
            return new Change(s, true);
        }
        return null;
    }

    public static string Describe(Verdict v) => v switch
    {
        Verdict.Ok => "открывается",
        Verdict.NoDns => "DNS не находит адрес",
        Verdict.DownForAll => "лежит у всех",
        Verdict.Blocked => "блокирует провайдер",
        Verdict.Refused => "закрыт для России",
        Verdict.NotProxied => "Smart DNS перестал подменять адрес",
        _ => "еще не проверен",
    };

    /// <summary>Что делать: короткий совет к вердикту.</summary>
    public static string Advice(Verdict v) => v switch
    {
        Verdict.NoDns => "Проверьте адрес или смените общий DNS",
        Verdict.DownForAll => "Снаружи России тоже не открывается. Остается подождать",
        Verdict.Blocked => "Снаружи России открывается. Поможет zapret",
        Verdict.Refused => "Поможет Smart DNS, который поддерживает этот сайт",
        Verdict.NotProxied => "Смените сервер",
        _ => "",
    };

    // ---------- Проверка ----------

    /// <summary>Проверка сайта снаружи (сервер общей карты на Cloudflare): true — открывается, false — нет, null — не узнали.</summary>
    internal static Func<string, CancellationToken, Task<bool?>> Outside = OutsideAsync;
    /// <summary>Загрузка сайта через DNS и сеть этого компьютера: код ответа или null (соединение не удалось).</summary>
    internal static Func<string, CancellationToken, Task<(bool Dns, int? Status, string? Body)>> Local = LocalAsync;

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static async Task<bool?> OutsideAsync(string host, CancellationToken ct)
    {
        try
        {
            using var resp = await Probe.GetAsync($"{Crowd.BaseUrl()}/v1/probe?host={Uri.EscapeDataString(host)}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("up", out var up) && up.ValueKind is JsonValueKind.True or JsonValueKind.False ? up.GetBoolean() : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<(bool Dns, int? Status, string? Body)> LocalAsync(string host, CancellationToken ct)
    {
        IPAddress[] ips;
        try { ips = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { ips = Array.Empty<IPAddress>(); }
        if (ips.Length == 0) return (false, null, null);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(7), UseProxy = true })
                         { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        try
        {
            using var resp = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string? body = null;
            if ((int)resp.StatusCode is 403 or 451)
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                body = text.Length > 20000 ? text[..20000] : text;
            }
            return (true, (int)resp.StatusCode, body);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (true, null, null); }
    }

    /// <summary>
    /// Проверить сайт и разобрать причину. smartDns — сайт должен идти через Smart DNS (домены ИИ и наборы «Двух DNS»):
    /// тогда отказ по региону значит, что сервер перестал его подменять.
    /// </summary>
    public static async Task<(Verdict, string?)> CheckAsync(string host, bool smartDns, CancellationToken ct)
    {
        var (dns, status, body) = await Local(host, ct).ConfigureAwait(false);
        if (!dns)
        {
            var outside = await Outside(host, ct).ConfigureAwait(false);
            return outside == false ? (Verdict.DownForAll, "сайт не отвечает и снаружи России") : (Verdict.NoDns, null);
        }
        if (status is { } code)
        {
            bool region = code is 403 or 451 && body != null &&
                          System.Text.RegularExpressions.Regex.IsMatch(body, @"unsupported_country|not (available|supported) in your (country|region)|unavailable in your (country|region)|Request not allowed|недоступ\w* в ваше\w* (стран|регион)|в вашей стране недоступ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (region) return (smartDns ? Verdict.NotProxied : Verdict.Refused, $"ответ {code}");
            if (code is >= 500 and not 503)
            {
                var outside = await Outside(host, ct).ConfigureAwait(false);
                return outside == false ? (Verdict.DownForAll, $"ответ {code}, снаружи тоже не отвечает") : (Verdict.Ok, $"ответ {code}");
            }
            return (Verdict.Ok, null);
        }
        // Соединиться не удалось: снаружи открывается — значит, режут по пути к вам, иначе сайт лежит
        var up = await Outside(host, ct).ConfigureAwait(false);
        return up switch
        {
            true => (Verdict.Blocked, "снаружи России открывается"),
            false => (Verdict.DownForAll, "снаружи России тоже не открывается"),
            _ => (Verdict.Blocked, "не удалось соединиться, снаружи проверить не получилось"),
        };
    }

    // ---------- Кэш DNS Windows ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsCacheEntry
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")]
    private static extern bool DnsGetCacheDataTable(out IntPtr table);
    [DllImport("dnsapi.dll", EntryPoint = "DnsFree")]
    private static extern void DnsFree(IntPtr data, int freeType);

    /// <summary>Имена из кэша DNS Windows (без прав администратора). Пусто, если функции нет или кэш недоступен.</summary>
    public static List<string> CachedNames()
    {
        var names = new List<string>();
        try
        {
            if (!DnsGetCacheDataTable(out var table) || table == IntPtr.Zero) return names;
            var cur = table;
            while (cur != IntPtr.Zero && names.Count < 5000)
            {
                var e = Marshal.PtrToStructure<DnsCacheEntry>(cur);
                if (e.Name != IntPtr.Zero && Marshal.PtrToStringUni(e.Name) is { Length: > 0 } n) names.Add(n);
                var next = e.Next;
                DnsFree(e.Name, 0);
                DnsFree(cur, 0);
                cur = next;
            }
        }
        catch (Exception) { }
        return names;
    }
}
