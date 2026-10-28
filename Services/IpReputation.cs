using System.Net;
using System.Net.Http;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Репутация адреса: риск по proxycheck.io (0–100), его тип и в каких черных списках он есть.</summary>
public sealed record IpRep(string Ip, int? Risk, string? Type, string? Provider, string[] Lists, DateTime Checked)
{
    /// <summary>Адрес в черном списке или с высоким риском: сервисы чаще блокируют входы с таких адресов.</summary>
    public bool Bad => Lists.Length > 0 || Risk >= IpReputation.BadRisk;
}

/// <summary>
/// Репутация IP выхода: адреса прокси Smart DNS, через который нейросети видят пользователя. Свой настоящий IP
/// пользователя сюда не попадает: проверяются только адреса выхода серверов. Источники без ключа и регистрации:
/// - proxycheck.io: оценка риска 0–100 и тип (VPN, хостинг, домашний). У любого Smart DNS выход на сервере,
///   поэтому «VPN» и риск около 66 для них обычное дело, плохим считается только высокий риск;
/// - черные списки DroneBL (открытые прокси, ботнеты), UCEPROTECT и SpamCop через DNS-over-HTTPS. Spamhaus не
///   отвечает публичным DNS, поэтому его нет.
/// Никакой публичный список не знает, кого блокируют сами OpenAI или Anthropic: это признак, а не приговор.
/// Итоги хранятся три дня в ip-reputation.json, чтобы не упираться в лимит proxycheck (около 100 запросов в день).
/// </summary>
public static class IpReputation
{
    public const int BadRisk = 75;
    private const string FileName = "ip-reputation.json";
    private static readonly TimeSpan Keep = TimeSpan.FromDays(3);
    private static readonly (string Name, string Zone)[] Blocklists =
    {
        ("DroneBL", "dnsbl.dronebl.org"), ("UCEPROTECT", "dnsbl-1.uceprotect.net"), ("SpamCop", "bl.spamcop.net"),
    };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly object Gate = new();
    private static Dictionary<string, IpRep>? _cache;

    /// <summary>Тесты подменяют источники.</summary>
    internal static Func<string, CancellationToken, Task<(int? Risk, string? Type, string? Provider)>> RiskOf = ProxyCheckAsync;
    internal static Func<string, string, CancellationToken, Task<bool>> ListedIn = ListedAsync;
    internal static Func<DateTime> Now = () => DateTime.Now;

    private static Dictionary<string, IpRep> Cache()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            try { _cache = Storage.LoadOrCreate(FileName, () => new Dictionary<string, IpRep>()); }
            catch (Exception) { _cache = new(); }
            _cache = _cache.Where(kv => Now() - kv.Value.Checked < Keep).ToDictionary(kv => kv.Key, kv => kv.Value);
            return _cache;
        }
    }

    /// <summary>Уже известная репутация адреса или null.</summary>
    public static IpRep? Cached(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        var c = Cache();
        lock (Gate) return c.TryGetValue(ip, out var r) && Now() - r.Checked < Keep ? r : null;
    }

    /// <summary>Хоть один адрес выхода сервера в черном списке или с высоким риском (по уже известным данным).</summary>
    public static bool IsBad(DnsServer s) => s.EgressBy.Values.Any(e => Cached(e.Ip)?.Bad == true);

    /// <summary>Адреса выхода сервера, которые стоит проверить: публичные и еще не проверенные.</summary>
    public static IEnumerable<string> Unchecked(DnsServer s) =>
        s.EgressBy.Values.Select(e => e.Ip).Where(ip => GeoLocation.IsPublicIp(ip) && Cached(ip) == null).Distinct();

    /// <summary>Проверить адрес (или взять из памяти). Null — адрес не публичный или источники не ответили.</summary>
    public static async Task<IpRep?> CheckAsync(string ip, string referenceDoh, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var parsed) || !GeoLocation.IsPublicIp(ip)) return null;
        if (Cached(ip) is { } known) return known;
        var riskTask = RiskOf(ip, ct);
        var lists = new List<string>();
        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var reversed = string.Join('.', ip.Split('.').Reverse());
            var hits = await Task.WhenAll(Blocklists.Select(async b => (b.Name, Hit: await ListedIn($"{reversed}.{b.Zone}", referenceDoh, ct))));
            lists.AddRange(hits.Where(h => h.Hit).Select(h => h.Name));
        }
        var (risk, type, provider) = await riskTask;
        if (risk == null && lists.Count == 0 && type == null) return null; // ничего не узнали: не запоминаем, проверим позже
        var rep = new IpRep(ip, risk, type, provider, lists.ToArray(), Now());
        var c = Cache();
        lock (Gate)
        {
            c[ip] = rep;
            try { Storage.Save(FileName, c); } catch (Exception) { }
        }
        return rep;
    }

    /// <summary>«риск 66 из 100, распознается как VPN; в черном списке DroneBL».</summary>
    public static string Describe(IpRep r)
    {
        var parts = new List<string>();
        if (r.Risk is { } risk) parts.Add($"риск {risk} из 100" + (string.IsNullOrEmpty(r.Type) ? "" : $", распознается как {r.Type}"));
        parts.Add(r.Lists.Length == 0 ? "в черных списках нет" : $"в черном списке {string.Join(", ", r.Lists)}");
        return string.Join("; ", parts);
    }

    private static async Task<(int?, string?, string?)> ProxyCheckAsync(string ip, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://proxycheck.io/v2/{ip}?vpn=1&risk=1", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(ip, out var e)) return (null, null, null);
            int? risk = e.TryGetProperty("risk", out var r) && r.TryGetInt32(out var v) ? v : null;
            string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? provider = e.TryGetProperty("provider", out var p) ? p.GetString() : null;
            return (risk, type, provider);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (null, null, null); }
    }

    /// <summary>Ответ из 127.0.0.0/8 значит «в списке»; 127.255.255.x — это коды ошибок самого списка, не попадание.</summary>
    private static async Task<bool> ListedAsync(string name, string doh, CancellationToken ct)
    {
        try
        {
            var (ips, _) = await DnsWire.ResolveDohAsync(doh, name, TimeSpan.FromSeconds(6), ct);
            return ips.Any(a => a.ToString() is var s && s.StartsWith("127.") && !s.StartsWith("127.255.255."));
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Сбросить память (тесты).</summary>
    internal static void Reset() { lock (Gate) _cache = new(); }
}
