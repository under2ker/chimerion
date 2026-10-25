using System.Globalization;
using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Dnex.Services;

/// <summary>
/// Где физически находится DNS-сервер: страна и город по IP (ipinfo.io), флаг страны (flagcdn.com).
/// Флаги кэшируются в %AppData%\CHIMERION\flags, поэтому повторно из сети не скачиваются.
/// </summary>
public static class GeoLocation
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Не больше трех одновременных запросов: ipinfo не любит всплески.
    private static readonly SemaphoreSlim Gate = new(3, 3);

    private static readonly string FlagDir = Path.Combine(Storage.Dir, "flags");

    /// <summary>Публичный IPv4. Частные, петлевые и служебные адреса геолокации не имеют.</summary>
    public static bool IsPublicIPv4(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Публичный адрес для геолокации: IPv4 вне частных сетей или глобальный IPv6.</summary>
    public static bool IsPublicIp(string ip) => IsPublicIPv4(ip) || SourceParser.IsPublic6(ip);

    /// <summary>Код страны и город по IP. Оба null, если сервис недоступен или лимит исчерпан.</summary>
    public static async Task<(string? Country, string? City)> LookupAsync(string ip, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var json = await Http.GetStringAsync($"https://ipinfo.io/{ip}/json", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var city = root.TryGetProperty("city", out var ci) ? ci.GetString() : null;
            return (string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant(), city);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return (null, null);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Код страны по второй базе (ipwho.is). Нужен для сверки: если базы расходятся, данным не доверяем полностью.</summary>
    public static async Task<string?> LookupSecondAsync(string ip, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var json = await Http.GetStringAsync($"https://ipwho.is/{ip}", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("success", out var ok) && ok.GetBoolean())) return null;
            var code = root.TryGetProperty("country_code", out var c) ? c.GetString() : null;
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> OrgTasks = new();

    /// <summary>Владелец сети адреса по ipinfo («AS15169 Google LLC»). Кэш на время работы программы; одновременные
    /// вопросы про один адрес (десятки серверов с одним прокси) ждут один запрос. Неудачу не кэшируем.</summary>
    public static async Task<string?> OrgAsync(string ip, CancellationToken ct)
    {
        var task = OrgTasks.GetOrAdd(ip, FetchOrgAsync);
        var org = await task.WaitAsync(ct);
        if (org == null) OrgTasks.TryRemove(new KeyValuePair<string, Task<string?>>(ip, task));
        return org;
    }

    private static async Task<string?> FetchOrgAsync(string ip)
    {
        var ct = CancellationToken.None; // запрос общий для всех ждущих: отмена одного не должна обрывать остальных
        await Gate.WaitAsync(ct);
        try
        {
            var json = await Http.GetStringAsync($"https://ipinfo.io/{ip}/json", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("org", out var o) ? o.GetString() ?? "" : "";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Адрес принадлежит сети самого сервиса (по списку владельцев из настроек).</summary>
    public static async Task<bool> IsOwnedByAsync(string ip, IReadOnlyCollection<string> owners, CancellationToken ct)
    {
        if (owners.Count == 0 || !IsPublicIp(ip)) return false;
        var org = await OrgAsync(ip, ct);
        return org != null && owners.Any(o => org.Contains(o, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Код страны по третьей базе (country.is). Спрашиваем, только когда первые две расходятся или одна молчит.</summary>
    public static async Task<string?> LookupThirdAsync(string ip, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var json = await Http.GetStringAsync($"https://api.country.is/{ip}", ct);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("country", out var c) ? c.GetString() : null;
            return string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Итог сверки баз: выбранная страна, город (только если его база в большинстве), ответ второй базы,
    /// подпись для подсказки и признак, что большинства нет.</summary>
    public sealed record GeoResult(string? Country, string? City, string? Second, string Votes, bool Disputed);

    /// <summary>
    /// Страна по IP голосованием баз. ipinfo и ipwho.is спрашиваем всегда; если они расходятся или одна молчит,
    /// спрашиваем country.is. Побеждает страна, которую назвали две базы. Без большинства берем ipinfo
    /// и помечаем результат как спорный. Null, если не ответила ни одна база (сеть или лимит: повторим позже).
    /// </summary>
    public static async Task<GeoResult?> ResolveAsync(string ip, CancellationToken ct)
    {
        var (c1, city) = await LookupAsync(ip, ct);
        var c2 = await LookupSecondAsync(ip, ct);
        string? c3 = c1 != null && c1 == c2 ? null : await LookupThirdAsync(ip, ct);

        var answers = new List<(string Base, string Code)>();
        if (c1 != null) answers.Add(("ipinfo", c1));
        if (c2 != null) answers.Add(("ipwho.is", c2));
        if (c3 != null) answers.Add(("country.is", c3));
        if (answers.Count == 0) return null;

        var top = answers.GroupBy(a => a.Code)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key == c1 ? 0 : 1).First();
        bool unanimous = answers.All(a => a.Code == top.Key);
        bool majority = top.Count() >= 2;
        var list = string.Join(", ", answers.Select(a => $"{a.Base}: {CountryName(a.Code)}"));
        var votes = unanimous
            ? (answers.Count > 1 ? $"базы совпадают ({list})" : $"ответила одна база ({list})")
            : majority ? $"страна выбрана большинством баз ({list})" : $"базы расходятся ({list})";
        return new GeoResult(top.Key, top.Key == c1 ? city : null, c2, votes, !majority && answers.Count > 1);
    }

    /// <summary>Путь к сохраненному флагу страны, если он уже скачан.</summary>
    public static string? FlagPath(string country)
    {
        var path = Path.Combine(FlagDir, country.ToLowerInvariant() + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Путь к флагу страны; при первом обращении скачивает PNG и кладет в кэш.</summary>
    public static async Task<string?> FlagPathAsync(string country, CancellationToken ct)
    {
        var code = country.ToLowerInvariant();
        if (code.Length != 2 || !code.All(char.IsLetter)) return null;
        if (FlagPath(code) is { } cached) return cached;
        try
        {
            Directory.CreateDirectory(FlagDir);
            var bytes = await Http.GetByteArrayAsync($"https://flagcdn.com/w40/{code}.png", ct);
            var path = Path.Combine(FlagDir, code + ".png");
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Русские названия частых стран. Остальные показываются по-английски.</summary>
    private static readonly Dictionary<string, string> RuNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RU"] = "Россия", ["UA"] = "Украина", ["BY"] = "Беларусь", ["KZ"] = "Казахстан", ["KG"] = "Киргизия",
        ["UZ"] = "Узбекистан", ["AZ"] = "Азербайджан", ["GE"] = "Грузия", ["AM"] = "Армения", ["MD"] = "Молдова",
        ["US"] = "США", ["CA"] = "Канада", ["MX"] = "Мексика", ["BR"] = "Бразилия", ["AR"] = "Аргентина",
        ["GB"] = "Великобритания", ["IE"] = "Ирландия", ["FR"] = "Франция", ["DE"] = "Германия", ["NL"] = "Нидерланды",
        ["BE"] = "Бельгия", ["LU"] = "Люксембург", ["CH"] = "Швейцария", ["AT"] = "Австрия", ["IT"] = "Италия",
        ["ES"] = "Испания", ["PT"] = "Португалия", ["PL"] = "Польша", ["CZ"] = "Чехия", ["SK"] = "Словакия",
        ["HU"] = "Венгрия", ["RO"] = "Румыния", ["BG"] = "Болгария", ["GR"] = "Греция", ["TR"] = "Турция",
        ["SE"] = "Швеция", ["NO"] = "Норвегия", ["FI"] = "Финляндия", ["DK"] = "Дания", ["EE"] = "Эстония",
        ["LV"] = "Латвия", ["LT"] = "Литва", ["IS"] = "Исландия", ["CN"] = "Китай", ["HK"] = "Гонконг",
        ["TW"] = "Тайвань", ["JP"] = "Япония", ["KR"] = "Южная Корея", ["SG"] = "Сингапур", ["IN"] = "Индия",
        ["ID"] = "Индонезия", ["TH"] = "Таиланд", ["VN"] = "Вьетнам", ["MY"] = "Малайзия", ["PH"] = "Филиппины",
        ["AU"] = "Австралия", ["NZ"] = "Новая Зеландия", ["ZA"] = "ЮАР", ["EG"] = "Египет", ["AE"] = "ОАЭ",
        ["IL"] = "Израиль", ["SA"] = "Саудовская Аравия", ["IR"] = "Иран", ["IQ"] = "Ирак", ["RS"] = "Сербия",
        ["HR"] = "Хорватия", ["SI"] = "Словения", ["BA"] = "Босния и Герцеговина", ["MK"] = "Северная Македония",
        ["AL"] = "Албания", ["CY"] = "Кипр", ["MT"] = "Мальта", ["LI"] = "Лихтенштейн", ["MC"] = "Монако",
    };

    /// <summary>Русские названия частых городов (ipinfo отдает английские). Остальные — как есть.</summary>
    private static readonly Dictionary<string, string> RuCities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Moscow"] = "Москва", ["Saint Petersburg"] = "Санкт-Петербург", ["Novosibirsk"] = "Новосибирск",
        ["Yekaterinburg"] = "Екатеринбург", ["Kazan"] = "Казань", ["Pervouralsk"] = "Первоуральск",
        ["Kyiv"] = "Киев", ["Minsk"] = "Минск", ["Almaty"] = "Алматы", ["Amsterdam"] = "Амстердам",
        ["Frankfurt am Main"] = "Франкфурт-на-Майне", ["Berlin"] = "Берлин", ["Paris"] = "Париж",
        ["London"] = "Лондон", ["Helsinki"] = "Хельсинки", ["Stockholm"] = "Стокгольм", ["Warsaw"] = "Варшава",
        ["Vienna"] = "Вена", ["Zurich"] = "Цюрих", ["Prague"] = "Прага", ["Riga"] = "Рига", ["Tallinn"] = "Таллин",
        ["Istanbul"] = "Стамбул", ["New York"] = "Нью-Йорк", ["Los Angeles"] = "Лос-Анджелес", ["Buffalo"] = "Буффало",
        ["Tokyo"] = "Токио", ["Singapore"] = "Сингапур", ["Hong Kong"] = "Гонконг", ["Dubai"] = "Дубай",
    };

    public static string CityName(string city) => RuCities.TryGetValue(city, out var ru) ? ru : city;

    /// <summary>Русские названия стран и городов и их английские варианты: для английского интерфейса (Loc).</summary>
    public static IEnumerable<(string Ru, string En)> EnglishNames()
    {
        foreach (var (code, ru) in RuNames)
        {
            string en;
            try { en = new RegionInfo(code).EnglishName; } catch (ArgumentException) { continue; }
            yield return (ru, en);
        }
        foreach (var (en, ru) in RuCities) yield return (ru, en);
    }

    /// <summary>Название страны по коду: по-русски для частых стран, иначе по-английски.</summary>
    public static string CountryName(string code)
    {
        if (RuNames.TryGetValue(code, out var ru)) return ru;
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }
}
