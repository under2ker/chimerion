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

    /// <summary>
    /// Найденные поиском серверы, которые при первой проверке не ответили ни разу (сеть при этом была в порядке: при сбое
    /// сети счетчики откатываются и сервер сюда не попадает). В основной список они не нужны: из 339 новых серверов в
    /// одном поиске ответили 6, остальные раздували список до трехсот строк и ждали второй проверки. Подключенный и
    /// добавленный вручную сервер не трогаем.
    /// </summary>
    public static List<DnsServer> DeadNewcomers(IEnumerable<DnsServer> added, Guid? activeId) =>
        added.Where(s => !s.Manual && s.Id != activeId && s.Status == ServerStatus.Fail && s.ChecksTotal >= 1 && s.ChecksOk == 0).ToList();

    public static bool IsMassHost(string? host) =>
        !string.IsNullOrEmpty(host) && MassHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>От скольких проверенных серверов «не ответил ни один» считается сбоем сети, а не мертвым списком.</summary>
    public const int NoAnswerMinBatch = 5;

    /// <summary>
    /// В пакете проверены не меньше <see cref="NoAnswerMinBatch"/> серверов, и ни один не ответил. Так выглядит сеть, где
    /// запросы к чужим DNS не проходят (фаервол, антивирус, VPN, провайдер): проверка «сеть есть» при этом успешна,
    /// а все серверы «не работают». Защита по числу ранее работавших серверов тут не помогает: их не было с самого начала.
    /// </summary>
    public static bool NoneAnswered(IEnumerable<DnsServer> batch)
    {
        int failed = 0;
        foreach (var s in batch)
        {
            if (s.Status is ServerStatus.Ok or ServerStatus.Partial) return false;
            if (s.Status == ServerStatus.Fail) failed++;
        }
        return failed >= NoAnswerMinBatch;
    }

    /// <summary>Автоочистка оставила бы список без единого настоящего сервера: так убирать нельзя.</summary>
    public static bool WouldEmptyList(IEnumerable<DnsServer> all, int junkCount) =>
        junkCount > 0 && all.Count(s => !s.IsPromo) <= junkCount;

    /// <summary>Сервер, который нельзя убирать: работает, в избранном или к нему подключено.</summary>
    private static bool Keep(DnsServer s, Guid? connected) =>
        s.IsPromo || s.IsFavorite || s.Id == connected || s.Status is ServerStatus.Ok or ServerStatus.Partial;

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
