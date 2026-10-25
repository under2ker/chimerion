using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dnex.Models;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>Настройки режима «Два DNS» (эксперимент).</summary>
public sealed class SplitDnsSettings
{
    public bool Enabled { get; set; }
    /// <summary>Общий DNS для всего интернета. Пустой список — DNS адаптера не меняется.</summary>
    public string GeneralName { get; set; } = "Не менять DNS системы";
    public List<string> GeneralAddresses { get; set; } = new();
    public string? GeneralDoh { get; set; }
    /// <summary>Свои домены, которые тоже отправлять в Smart DNS (по одному, без «https://»).</summary>
    public List<string> ExtraAiDomains { get; set; } = new();
    /// <summary>
    /// Готовые наборы (BypassCatalog), которые тоже отправлять в Smart DNS: для сервисов, которые сами закрылись
    /// для России (Figma, Adobe, Docker, Notion...). Работает, если Smart DNS эти сервисы поддерживает.
    /// </summary>
    public List<string> Groups { get; set; } = new();
}

/// <summary>Готовый общий DNS: название, адреса, DoH и чем он хорош.</summary>
public sealed record GeneralDnsPreset(string Name, string[] Addresses, string? Doh, string Note);

/// <summary>Итог проверки кандидата на роль общего DNS.</summary>
public sealed record GeneralDnsCheck(string Name, int ForeignOk, int ForeignTotal, int RussianOk, int RussianTotal, string Detail);

/// <summary>
/// Режим «Два DNS»: на адаптере общий DNS для всего интернета, а домены ИИ через правила NRPT (таблица политик
/// разрешения имен Windows) уходят только в Smart DNS. Проверено на Windows 11: правилам подчиняются сама система,
/// .NET, Edge и Яндекс Браузер. Правила CHIMERION помечены комментарием «CHIMERION» и снимаются при отключении.
/// </summary>
public static class SplitDns
{
    public const string RuleComment = "DNEX";

    /// <summary>
    /// Домены ИИ. Для своих доменов сервисов правило ставится на весь домен с поддоменами. У Google только точные
    /// адреса: google.com и googleapis.com общие с YouTube, почтой и поиском, целиком их в Smart DNS не отправляем.
    /// </summary>
    public static readonly string[] SuffixDomains =
    {
        // ChatGPT и OpenAI
        "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "chat.com", "sora.com",
        // Claude и Anthropic
        "claude.ai", "claude.com", "anthropic.com", "claudeusercontent.com",
    };

    public static readonly string[] ExactDomains =
    {
        "gemini.google.com", "aistudio.google.com", "makersuite.google.com", "notebooklm.google.com", "bard.google.com",
        "generativelanguage.googleapis.com", "alkalimakersuite-pa.clients6.google.com", "ai.google.dev",
    };

    /// <summary>
    /// Общие адреса Google, которые встречаются в настройках сервисов, но нужны и всему остальному (вход в аккаунт,
    /// поиск, шрифты). В правила для ИИ не попадают, чтобы два DNS не пересекались. Свой домен пользователь может
    /// добавить вручную.
    /// </summary>
    public static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.google.com", "google.com", "www.google.com", "apis.google.com", "www.gstatic.com", "fonts.googleapis.com", "fonts.gstatic.com",
    };

    public static readonly GeneralDnsPreset[] Presets =
    {
        new("Не менять DNS системы", Array.Empty<string>(), null, "DNS адаптера остается как есть, в Smart DNS уходят только домены ИИ"),
        new("Zapret DNS", new[] { "83.217.211.149" }, "https://dns.zapret.moe/dns-query", "Открывает часть заблокированных сайтов через свой прокси"),
        new("Comss DNS", new[] { "83.220.169.155", "212.109.195.93" }, "https://dns.comss.one/dns-query", "Открывает часть заблокированных сайтов через свой прокси"),
        new("Xbox DNS", new[] { "111.88.96.50", "111.88.96.51" }, "https://xbox-dns.ru/dns-query", "Открывает часть заблокированных сайтов и игровые сервисы"),
        new("Cloudflare", new[] { "1.1.1.1", "1.0.0.1" }, "https://cloudflare-dns.com/dns-query", "Честные ответы без подмены. Заблокированное сам не открывает"),
        new("Google", new[] { "8.8.8.8", "8.8.4.4" }, "https://dns.google/dns-query", "Честные ответы без подмены. Заблокированное сам не открывает"),
    };

    /// <summary>
    /// Список правил NRPT: для доменов сервисов запись «домен» и «.домен» (сам домен и все поддомены), для точных
    /// адресов только «адрес». Плюс адреса из настроек сервисов и свои домены пользователя.
    /// </summary>
    public static List<string> Namespaces(AppSettings st)
    {
        var suffix = new HashSet<string>(SuffixDomains, StringComparer.OrdinalIgnoreCase);
        var exact = new HashSet<string>(ExactDomains, StringComparer.OrdinalIgnoreCase);
        foreach (var d in st.SplitDns.ExtraAiDomains.Select(Clean).Where(d => d.Length > 0)) suffix.Add(d);
        if (st.SplitDns.Groups.Count > 0)
            foreach (var d in BypassCatalog.DomainsOf(BypassCatalog.LoadCached(), st.SplitDns.Groups).Where(d => !SharedHosts.Contains(d))) suffix.Add(d);
        // Адреса сервисов из настроек: если они не под доменами выше, добавляем точным правилом
        foreach (var t in st.Targets.Where(t => t.Enabled))
            foreach (var h in new[] { t.Host, t.WebCheck?.Split('/')[0] ?? "" }.Concat(t.ExtraHosts).Select(Clean))
                if (h.Length > 0 && !SharedHosts.Contains(h) && !suffix.Any(s => h.Equals(s, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)))
                    exact.Add(h);
        return suffix.OrderBy(d => d).SelectMany(d => new[] { d, "." + d }).Concat(exact.OrderBy(d => d)).ToList();
    }

    /// <summary>Домен из того, что ввел пользователь: без схемы, пути, точек по краям и «*.».</summary>
    public static string Clean(string? s)
    {
        var d = (s ?? "").Trim().ToLowerInvariant();
        if (d.Contains("://")) d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
        d = d.Split('/')[0].TrimStart('*').Trim('.');
        return Uri.CheckHostName(d) == UriHostNameType.Dns && d.Contains('.') ? d : "";
    }

    /// <summary>Есть ли сейчас правила CHIMERION в таблице NRPT (чтение реестра, без PowerShell и прав администратора).</summary>
    public static bool RulesPresent()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig");
            if (k == null) return false;
            foreach (var name in k.GetSubKeyNames())
            {
                using var r = k.OpenSubKey(name);
                if (string.Equals(r?.GetValue("Comment") as string, RuleComment, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>PowerShell: снять все правила CHIMERION из NRPT. Вставляется в скрипты подключения и отключения.</summary>
    public const string RemoveRulesScript =
        "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -eq 'DNEX' } | ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force -ErrorAction SilentlyContinue }";

    // ---------- Проверка кандидатов на общий DNS ----------

    // Заблокированное в России зарубежное: должно открываться через общий DNS (если он умеет обходить блокировки)
    private static readonly string[] ForeignSites = { "www.instagram.com", "x.com", "www.facebook.com", "www.linkedin.com", "discord.com", "www.youtube.com" };
    // Российские сайты: адреса должны быть настоящими, иначе сайт увидит зарубежный прокси и может не пустить
    private static readonly string[] RussianSites = { "www.gosuslugi.ru", "www.sberbank.ru", "ya.ru", "vk.com" };

    /// <summary>
    /// Проверка общего DNS: заблокированные зарубежные сайты открываются через его ответы (HTTPS через полученный
    /// адрес), а российские получают настоящие адреса (совпадают с эталоном или открываются напрямую).
    /// «Не менять DNS системы» проверяется через DNS системы.
    /// </summary>
    public static async Task<GeneralDnsCheck> CheckAsync(GeneralDnsPreset p, AppSettings st, CancellationToken ct)
    {
        async Task<List<IPAddress>> Resolve(string host)
        {
            try
            {
                if (p.Addresses.Length == 0)
                    return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).ToList();
                return (await DnsWire.ResolveUdpAsync(IPAddress.Parse(p.Addresses[0]), host, TimeSpan.FromSeconds(5), ct)).Ips;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return new(); }
        }

        var foreign = await Task.WhenAll(ForeignSites.Select(async h =>
        {
            var ips = await Resolve(h);
            return ips.Count > 0 && await ReachableAsync(ips[0], h, ct);
        }));
        var russian = await Task.WhenAll(RussianSites.Select(async h =>
        {
            var ips = await Resolve(h);
            if (ips.Count == 0) return false;
            try
            {
                var (real, _) = await DnsWire.ResolveDohAsync(st.ReferenceDoh, h, TimeSpan.FromSeconds(6), ct);
                if (ips.Any(real.Contains)) return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            // Адрес не совпал с эталоном (у крупных сайтов адресов много): засчитываем, если он российский
            var (country, _) = await GeoLocation.LookupAsync(ips[0].ToString(), ct);
            return string.Equals(country, "RU", StringComparison.OrdinalIgnoreCase) && await ReachableAsync(ips[0], h, ct);
        }));
        int f = foreign.Count(x => x), r = russian.Count(x => x);
        var missF = ForeignSites.Where((_, i) => !foreign[i]).Select(h => h.Replace("www.", "")).ToList();
        var missR = RussianSites.Where((_, i) => !russian[i]).Select(h => h.Replace("www.", "")).ToList();
        var detail = (missF.Count > 0 ? "не открылись: " + string.Join(", ", missF) : "все зарубежные открылись")
                     + (missR.Count > 0 ? "; подменены или недоступны российские: " + string.Join(", ", missR) : "; российские адреса настоящие");
        return new GeneralDnsCheck(p.Name, f, ForeignSites.Length, r, RussianSites.Length, detail);
    }

    /// <summary>HTTPS-запрос к сайту через конкретный адрес (с проверкой сертификата): любой ответ HTTP до 500.</summary>
    private static async Task<bool> ReachableAsync(IPAddress ip, string host, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (_, c) =>
            {
                var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await sock.ConnectAsync(new IPEndPoint(ip, 443), c); return new NetworkStream(sock, ownsSocket: true); }
                catch { sock.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CHIMERION/0.1");
        try
        {
            using var resp = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)resp.StatusCode < 500;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}
