namespace Dnex.Services;

/// <summary>
/// Защита аккаунтов ИИ-сервисов. Обход региона нарушает условия сервисов, и чаще всего аккаунт банят не за сам вход,
/// а за противоречивые сигналы: сегодня вход из Нидерландов, завтра из Германии, послезавтра из США. Класс помнит,
/// из какой страны каждый сервис обычно видит пользователя (по дням за последние 30 дней), и подсказывает:
/// - сейчас сервис видит вас не из привычной страны;
/// - сервер, к которому вы собираетесь подключиться, выведет сервис в другую страну;
/// - страна менялась слишком часто.
/// Хранится только страна и день, без адресов: файл egress-history.json в папке данных.
/// </summary>
public sealed class AccountSafety
{
    public const string FileName = "egress-history.json";
    /// <summary>Сколько дней помнить.</summary>
    public const int KeepDays = 30;
    /// <summary>Больше стольких разных стран за KeepDays — частая смена.</summary>
    public const int ManyCountries = 2;

    /// <summary>Дни, когда сервис видел пользователя из страны: сервис → страна → дни (yyyy-MM-dd).</summary>
    public Dictionary<string, Dictionary<string, SortedSet<string>>> Days { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Страна, которую выбрал пользователь: тогда она «привычная» для всех сервисов, даже без истории, и программа
    /// подключает только серверы с выходом в ней. Задается из настроек (AppSettings.EgressCountry).
    /// </summary>
    public static string? Pinned { get; set; }

    /// <summary>Сервер выводит все сервисы в выбранную страну. Без выбора подходит любой.</summary>
    public static bool Fits(Models.DnsServer s) =>
        Pinned == null || (s.EgressBy.Count > 0 && s.EgressBy.Values.All(e => string.Equals(e.Country, Pinned, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Тесты подменяют «сегодня».</summary>
    internal static Func<DateTime> Today = () => DateTime.Today;

    public static AccountSafety Load()
    {
        try { return Storage.LoadOrCreate(FileName, () => new AccountSafety()).Normalized(); }
        catch (Exception) { return new AccountSafety(); }
    }

    public void Save()
    {
        try { Storage.Save(FileName, this); } catch (Exception) { }
    }

    private AccountSafety Normalized()
    {
        // После чтения из файла словари без нужного сравнения: пересобираем, заодно выбрасываем старые дни
        var cut = Today().AddDays(-KeepDays).ToString("yyyy-MM-dd");
        Days = Days.ToDictionary(
            s => s.Key,
            s => s.Value.ToDictionary(c => c.Key.ToUpperInvariant(), c => new SortedSet<string>(c.Value.Where(d => string.CompareOrdinal(d, cut) >= 0)), StringComparer.OrdinalIgnoreCase)
                        .Where(c => c.Value.Count > 0).ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        return this;
    }

    /// <summary>Сервис сейчас видит пользователя из страны country. Возвращает true, если запись новая (стоит сохранить).</summary>
    public bool Record(string service, string? country)
    {
        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(country) || country.Length != 2) return false;
        if (!Days.TryGetValue(service, out var byCountry)) Days[service] = byCountry = new(StringComparer.OrdinalIgnoreCase);
        var c = country.ToUpperInvariant();
        if (!byCountry.TryGetValue(c, out var days)) byCountry[c] = days = new SortedSet<string>();
        return days.Add(Today().ToString("yyyy-MM-dd"));
    }

    /// <summary>Привычная страна сервиса: где он видел пользователя больше всего дней (при равенстве — последней). null — истории нет.</summary>
    public string? Home(string service) =>
        Pinned ?? (Days.TryGetValue(service, out var byCountry) && byCountry.Count > 0
            ? byCountry.OrderByDescending(c => c.Value.Count).ThenByDescending(c => c.Value.Max).First().Key
            : null);

    /// <summary>Сколько дней считать «недавно»: страна уже менялась за это время — новая смена считается частой.</summary>
    public const int RecentDays = 14;

    /// <summary>Страны, из которых сервис видел пользователя за последние days дней, в порядке первого дня.</summary>
    public List<string> Recent(string service, int days = RecentDays)
    {
        if (!Days.TryGetValue(service, out var byCountry)) return new();
        var cut = Today().AddDays(-days).ToString("yyyy-MM-dd");
        return byCountry.Select(c => (Country: c.Key, First: c.Value.Where(d => string.CompareOrdinal(d, cut) >= 0).DefaultIfEmpty("").Min()!))
                        .Where(c => c.First.Length > 0).OrderBy(c => c.First, StringComparer.Ordinal).Select(c => c.Country).ToList();
    }

    /// <summary>Страна уже менялась недавно хотя бы у одного из сервисов: еще одна смена — частая.</summary>
    public bool Frequent(IEnumerable<string> services, int days = RecentDays) => services.Any(s => Recent(s, days).Count >= 2);

    /// <summary>Сколько разных стран видел сервис за последние KeepDays дней.</summary>
    public int CountryCount(string service) => Days.TryGetValue(service, out var byCountry) ? byCountry.Count : 0;

    /// <summary>Расхождение с привычной страной: сервис, привычная страна, новая страна.</summary>
    public sealed record Mismatch(string Service, string Home, string Now);

    /// <summary>
    /// Какие сервисы окажутся не в привычной стране, если выход будет таким, как в egress (сервис → страна).
    /// Сервисы без истории или без данных о выходе пропускаются: с чем сравнивать, неизвестно.
    /// </summary>
    public List<Mismatch> Compare(IEnumerable<KeyValuePair<string, string?>> egress) =>
        egress.Where(e => !string.IsNullOrEmpty(e.Value))
              .Select(e => (e.Key, Country: e.Value!.ToUpperInvariant(), Home: Home(e.Key)))
              .Where(e => e.Home != null && e.Home != e.Country)
              .Select(e => new Mismatch(e.Key, e.Home!, e.Country))
              .ToList();

    /// <summary>Куда сервер выведет сервисы (по последней проверке сервера): сервис → страна.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> EgressOf(Models.DnsServer s) =>
        s.EgressBy.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.Country));

    /// <summary>
    /// Насколько сервер подходит по стране: сколько сервисов он выведет в привычную страну минус сколько в чужую.
    /// Для выбора замены и запасного сервера: при прочих равных лучше тот, что не меняет страну.
    /// </summary>
    public int Fit(Models.DnsServer s)
    {
        int fit = 0;
        foreach (var (service, country) in EgressOf(s))
        {
            var home = Home(service);
            if (home == null || string.IsNullOrEmpty(country)) continue;
            fit += string.Equals(home, country, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        }
        return fit;
    }

    /// <summary>Текст предупреждения: «ChatGPT: Нидерланды → Германия, Claude: Нидерланды → США».</summary>
    public static string Describe(IEnumerable<Mismatch> list) =>
        string.Join(", ", list.Select(m => $"{m.Service}: {GeoLocation.CountryName(m.Home)} → {GeoLocation.CountryName(m.Now)}"));
}
