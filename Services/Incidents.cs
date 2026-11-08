namespace Dnex.Services;

/// <summary>
/// Новые блокировки у провайдера. Программы с согласия пользователя сообщают в общую карту, открываются ли
/// популярные сервисы из короткого списка, и включен ли при этом обход. Сервер копит это по сети провайдера (AS)
/// и замечает, когда сервис, который неделю открывался, у многих перестал (backend/crowd/worker.js, incidents).
/// В ответ программа узнает о блокировке раньше, чем человек сам поймет, в чем дело.
///
/// Уходят только сервисы из списка Sites и только те, которые программа и так проверяет в «Наблюдении за сайтами».
/// Свои сайты пользователя не уходят никогда.
/// </summary>
public static class Incidents
{
    /// <summary>Сервисы, о которых сообщают программы. Тот же список на сервере (SITES в worker.js).</summary>
    public static readonly string[] Sites =
    {
        "youtube.com", "discord.com", "t.me", "telegram.org", "whatsapp.com",
        "instagram.com", "x.com", "facebook.com", "twitch.tv", "linkedin.com",
    };

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube.com"] = "YouTube", ["discord.com"] = "Discord", ["t.me"] = "Telegram", ["telegram.org"] = "Telegram",
        ["whatsapp.com"] = "WhatsApp", ["instagram.com"] = "Instagram", ["x.com"] = "X", ["facebook.com"] = "Facebook",
        ["twitch.tv"] = "Twitch", ["linkedin.com"] = "LinkedIn",
    };

    /// <summary>Сервис из списка, к которому относится имя сайта (www.youtube.com → youtube.com). Null — не из списка.</summary>
    public static string? SiteOf(string host)
    {
        var h = host.Trim().TrimEnd('.');
        return Sites.FirstOrDefault(s => h.Equals(s, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
    }

    public static string NameOf(string site) => Names.TryGetValue(site, out var n) ? n : site;

    /// <summary>Итог проверки сервиса для общей карты. Bypass — в это время работал обход (zapret).</summary>
    public sealed record Report(string Site, bool Ok, bool Bypass);

    /// <summary>
    /// Что отправить по итогам круга проверки сайтов. Засчитываются только ясные исходы: открылся или его режет
    /// провайдер. «Лежит у всех», «нет адреса» и прочее не про провайдера. На сервис одна запись: при нескольких
    /// адресах одного сервиса открылся хоть один, значит открывается.
    /// </summary>
    public static List<Report> Collect(IEnumerable<SiteWatch.Site> checkedSites, bool bypass)
    {
        var by = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in checkedSites)
        {
            if (SiteOf(s.Host) is not { } site) continue;
            bool? ok = s.State switch
            {
                SiteWatch.Verdict.Ok => true,
                SiteWatch.Verdict.Blocked or SiteWatch.Verdict.Refused => false,
                _ => null,
            };
            if (ok is not { } v) continue;
            by[site] = by.TryGetValue(site, out var was) ? was || v : v;
        }
        return by.Select(kv => new Report(kv.Key, kv.Value, bypass)).OrderBy(r => r.Site, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Блокировка в сети провайдера по данным сервера. Fail — доля неудачных проверок за последние часы (0–100),
    /// Checks — сколько их было, Since — час начала (часы от начала эпохи, UTC).
    /// </summary>
    public sealed record Incident(string Site, bool Bypass, int Fail, int Checks, long Since);

    /// <summary>
    /// Ключ уведомления: о каждой блокировке не чаще раза в сутки. Время начала в ключ не входит: окно на сервере
    /// сдвигается каждый час, и оно бы сдвигалось вместе с ним.
    /// </summary>
    public static string Key(int asn, Incident i, DateTime today) => $"{asn}|{NameOf(i.Site)}|{(i.Bypass ? 1 : 0)}|{today:yyyy-MM-dd}";

    /// <summary>
    /// О каких блокировках сказать человеку: сервисом он пользуется (есть в наблюдении), блокировка касается его
    /// (без обхода, когда обход выключен, и наоборот), и сегодня о ней еще не говорили.
    /// </summary>
    public static List<Incident> ToNotify(IEnumerable<Incident> incidents, int asn, IEnumerable<string> usedHosts, bool bypassOn,
        ICollection<string> notified, DateTime today)
    {
        var used = usedHosts.Select(SiteOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return incidents.Where(i => used.Contains(i.Site) && i.Bypass == bypassOn && !notified.Contains(Key(asn, i, today)))
                        .GroupBy(i => NameOf(i.Site)).Select(g => g.First()) // t.me и telegram.org: одно уведомление
                        .ToList();
    }

    /// <summary>Текст уведомления: «У пользователей Rostelecom (AS12389) примерно с 14:00 не открывается Discord…».</summary>
    public static string Describe(Incident i, string net)
    {
        var since = DateTimeOffset.FromUnixTimeSeconds(i.Since * 3600).ToLocalTime();
        var when = since.Date == DateTime.Today ? $"примерно с {since:HH:mm}" : $"с {since.Day} {RuMonth(since.Month)}";
        return i.Bypass
            ? $"У пользователей {net} {when} обход перестал открывать {NameOf(i.Site)}. Попробуйте другую стратегию в «Обход и Telegram»"
            : $"У пользователей {net} {when} не открывается {NameOf(i.Site)}. Включить обход можно в «Обход и Telegram»";
    }

    private static string RuMonth(int m) => new[] { "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря" }[m - 1];
}
