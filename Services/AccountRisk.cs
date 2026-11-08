namespace Dnex.Services;

/// <summary>
/// «Как вас видят сервисы»: сводка по тому, что программа уже знает о подключении. Страна, из которой сервис видит
/// вас сейчас, привычная страна, сколько стран было за две недели и репутация адреса выхода. Сама в сеть не ходит:
/// фоновых запросов к сервисам и так достаточно, а лишние с адреса, где человек сидит в аккаунте, ни к чему.
/// Оценка только подсказывает: что именно сервисы считают подозрительным, никто снаружи не знает.
/// </summary>
public static class AccountRisk
{
    public enum Level { Calm, Attention, Risky }

    /// <param name="Country">Страна, из которой сервис видит вас по последней проверке. Null — не узнали.</param>
    /// <param name="Home">Привычная страна (AccountSafety.Home).</param>
    /// <param name="Recent">Страны за последние две недели, в порядке появления.</param>
    /// <param name="Rep">Репутация адреса выхода, если она уже известна.</param>
    public sealed record ServiceView(string Service, string? Country, string? Home, IReadOnlyList<string> Recent, IpRep? Rep);

    public sealed record Factor(Level Level, string Text, string Advice);

    /// <param name="Known">Есть хоть один сервис со страной выхода: без них оценивать нечего.</param>
    public sealed record Result(Level Level, bool Known, IReadOnlyList<ServiceView> Services, IReadOnlyList<Factor> Factors);

    /// <summary>Сколько стран за две недели уже повод насторожиться и сколько считается частой сменой.</summary>
    public const int AttentionCountries = 2, RiskyCountries = 3;

    public static Result Assess(IReadOnlyList<ServiceView> services, bool guardOn)
    {
        var factors = new List<Factor>();
        if (!guardOn)
            factors.Add(new Factor(Level.Attention, "Защита аккаунтов выключена",
                "Включите ее в настройках: тогда программа не сменит страну без вашего согласия"));
        foreach (var s in services)
        {
            if (s.Country != null && s.Home != null && !string.Equals(s.Country, s.Home, StringComparison.OrdinalIgnoreCase))
                factors.Add(new Factor(Level.Risky, $"{s.Service} видит вас из страны {GeoLocation.CountryName(s.Country)}, а обычно из {GeoLocation.CountryName(s.Home)}",
                    $"Подключитесь к серверу с выходом в {GeoLocation.CountryName(s.Home)}: колонка «Где» в списке серверов"));
            if (s.Recent.Count >= AttentionCountries)
                factors.Add(new Factor(s.Recent.Count >= RiskyCountries ? Level.Risky : Level.Attention,
                    $"{s.Service}: за {AccountSafety.RecentDays} дней страны входа {string.Join(", ", s.Recent.Select(GeoLocation.CountryName))}",
                    "Закрепите одну страну в настройках и держитесь ее"));
            if (s.Rep is { Bad: true } rep)
                factors.Add(new Factor(Level.Risky, $"Адрес, через который вас видит {s.Service}, на плохом счету: {IpReputation.Describe(rep)}",
                    "Смените сервер: с таких адресов входы блокируют чаще"));
        }
        bool known = services.Any(s => s.Country != null);
        var level = factors.Count == 0 ? Level.Calm : factors.Max(f => f.Level);
        return new Result(level, known, services, factors);
    }

    /// <summary>
    /// Страна, которую стоит закрепить: привычная у всех сервисов с историей. Разные привычные страны у разных
    /// сервисов: выбрать за человека нельзя, null.
    /// </summary>
    public static string? CommonHome(IEnumerable<ServiceView> services)
    {
        var homes = services.Select(s => s.Home).OfType<string>().Select(h => h.ToUpperInvariant()).Distinct().ToList();
        return homes.Count == 1 ? homes[0] : null;
    }

    public static string Title(Level level) => level switch
    {
        Level.Calm => "Спокойно", Level.Attention => "Внимание", _ => "Рискованно",
    };

    public static string Summary(Result r) => !r.Known
        ? "Пока не с чем сравнивать: подключитесь и дождитесь проверки сервисов"
        : r.Level switch
        {
            Level.Calm => "Сервисы видят вас из одной и той же страны, адреса выхода без замечаний",
            Level.Attention => "Есть то, что стоит поправить, пока это не стало проблемой",
            _ => "Сейчас сервисы видят то, за что чаще всего блокируют аккаунты",
        };
}
