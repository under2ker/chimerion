using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Защита списка серверов от общих каталогов DNS. Сайты вроде public-dns.info публикуют все публичные резолверы мира
/// (десятки тысяч адресов): ни один из них не открывает нейросети, а поиск один раз набрал так 100 тысяч серверов,
/// после чего проверка и диагностика шли минутами. Подборка Smart DNS на странице всегда короткая.
/// </summary>
public static class ServerListGuard
{
    /// <summary>Больше адресов на одной странице: это общий каталог DNS, а не подборка Smart DNS. Страница пропускается.</summary>
    public const int MaxPerPage = 150;

    /// <summary>Сколько новых серверов принимает один поиск. Остальное почти всегда мусор, а проверка каждого стоит запросов.</summary>
    public const int MaxNewPerSearch = 600;

    /// <summary>Выше этого числа список считается раздутым: при запуске остаются только полезные серверы.</summary>
    public const int MaxServers = 2000;

    /// <summary>Сайты-каталоги всех публичных DNS и справочники резолверов: с них берутся только обычные DNS.</summary>
    public static readonly string[] MassHosts =
    {
        "public-dns.info", "publicdnsserver.com", "dnsperf.com", "dnscrypt.info", "rethinkdns.com", "cloudflare-dns.com",
        "controld.com", "opennic.org", "dns.sb", "dnslist.net", "dnsserverlist.org", "dnscheck.tools",
    };

    /// <summary>
    /// Разных серверов на странице больше предела. Считаются серверы, а не строки: файл hosts GeoHideDNS дает тысячи
    /// строк «домен → адрес», но адресов в нем около двадцати, и это нормальная подборка Smart DNS.
    /// </summary>
    public static int Distinct(IEnumerable<DnsServer> items) => items.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public static bool IsMassHost(string? host) =>
        !string.IsNullOrEmpty(host) && MassHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Сервер, который нельзя убирать: работает, в избранном или к нему подключено.</summary>
    private static bool Keep(DnsServer s, Guid? connected) =>
        s.IsFavorite || s.Id == connected || s.Status is ServerStatus.Ok or ServerStatus.Partial;

    /// <summary>
    /// Убирает серверы, набранные с общих каталогов, а если список все равно раздут, лишние нерабочие (новые первыми).
    /// Рабочие, избранные и подключенный остаются всегда. Возвращает, сколько убрано.
    /// </summary>
    public static int Purge(IList<DnsServer> servers, Guid? connected)
    {
        int before = servers.Count;
        var keep = servers.Where(s => Keep(s, connected) || !IsMassHost(s.SourceHost)).ToList();
        if (keep.Count > MaxServers)
        {
            var useful = keep.Where(s => Keep(s, connected)).ToList();
            var rest = keep.Where(s => !Keep(s, connected)).OrderBy(s => s.AddedAt).Take(Math.Max(0, MaxServers - useful.Count));
            keep = useful.Concat(rest).ToList();
        }
        if (keep.Count == before) return 0;
        var set = keep.ToHashSet();
        for (int i = servers.Count - 1; i >= 0; i--)
            if (!set.Contains(servers[i])) servers.RemoveAt(i);
        return before - servers.Count;
    }
}
