using System.Globalization;
using System.Reflection;
using System.Text.Json;
using NodaTime;
using NodaTime.TimeZones;

namespace Dnex.Services;

internal static class TimeZonePlaces
{
    private sealed record Country(string Ru, string En, string Aliases);
    private sealed record Place(string Zone, string Country, string Ru, string En);
    private sealed record Catalog(Dictionary<string, Country> Countries, Dictionary<string, Place> Places);

    internal sealed record Choice(string Id, string Name, string Country, string CountryCode, string SearchNames, string? IanaId, TimeZoneInfo? Windows)
    {
        internal string Label => Country.Length == 0 ? Name : $"{Name}, {Country}";

        internal DateTimeOffset At(DateTimeOffset now, bool windows)
        {
            if (IanaId is { } iana && (!windows || Windows == null) && DateTimeZoneProviders.Tzdb.GetZoneOrNull(iana) is { } zone)
                return Instant.FromDateTimeOffset(now).InZone(zone).ToDateTimeOffset();
            return Windows == null ? now : TimeZoneInfo.ConvertTime(now, Windows);
        }

        internal bool Matches(string[] words, string offset)
        {
            var shortOffset = offset[4] == '0' ? offset.Remove(4, 1) : offset;
            var names = $"{SearchNames} {offset} {shortOffset}";
            return words.All(word => CultureInfo.InvariantCulture.CompareInfo.IndexOf(names, word.Replace('−', '-'),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        }
    }

    private static readonly Lazy<Catalog> Data = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("time-zone-places.json")!;
        return JsonSerializer.Deserialize<Catalog>(stream)!;
    });

    internal static IReadOnlyList<Choice> List(bool english)
    {
        var source = TzdbDateTimeZoneSource.Default;
        var systems = TimeZoneInfo.GetSystemTimeZones().ToDictionary(zone => zone.Id, StringComparer.OrdinalIgnoreCase);
        var windowsIds = source.WindowsMapping.MapZones.SelectMany(zone => zone.TzdbIds.Select(id => (id, zone.WindowsId)))
            .ToLookup(entry => entry.id, entry => entry.WindowsId, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new List<Choice>();
        foreach (var (id, place) in Data.Value.Places)
        {
            if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(place.Zone) == null) continue;
            var country = Data.Value.Countries[place.Country];
            var name = english ? place.En : place.Ru;
            var region = english ? country.En : country.Ru;
            var windows = WindowsFor(place.Zone);
            if (windows != null) used.Add(windows.Id);
            var search = $"{place.Ru} {place.En} {country.Aliases} {place.Zone} {place.Zone.Replace('_', ' ')}";
            locations.Add(new Choice(id, name, region, place.Country, search, place.Zone, windows));
        }

        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "ru-RU");
        var order = StringComparer.Create(culture, ignoreCase: true);
        var fallback = systems.Values.Where(zone => !used.Contains(zone.Id)).Select(zone =>
        {
            source.WindowsMapping.PrimaryMapping.TryGetValue(zone.Id, out var iana);
            iana ??= TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var mapped) ? mapped : null;
            var name = zone.Id == "UTC" ? (english ? "Coordinated Universal Time" : "Всемирное время")
                : english ? zone.Id.Replace(" Standard Time", "", StringComparison.Ordinal)
                : zone.DisplayName[(zone.DisplayName.IndexOf(')') + 1)..].Trim();
            return new Choice(zone.Id, name, "", "", $"{name} {zone.Id} {iana}", iana, zone);
        }).ToArray();
        return fallback.Where(zone => zone.Id == "UTC")
            .Concat(locations.OrderBy(zone => zone.Name, order).ThenBy(zone => zone.Country, order))
            .Concat(fallback.Where(zone => zone.Id != "UTC"))
            .ToArray();

        TimeZoneInfo? WindowsFor(string iana)
        {
            if (windowsIds[iana].FirstOrDefault() is { } exact && systems.TryGetValue(exact, out var direct))
                return direct;
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var mapped) && systems.TryGetValue(mapped, out var platform))
                return platform;
            var canonical = source.CanonicalIdMap[iana];
            return windowsIds[canonical].FirstOrDefault() is { } alias ? systems.GetValueOrDefault(alias) : null;
        }
    }

    internal static Choice? Default(IReadOnlyList<Choice> choices, string windowsId)
    {
        TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping.TryGetValue(windowsId, out var primary);
        var matches = choices.Where(zone => zone.Windows?.Id == windowsId);
        return matches.FirstOrDefault(zone => zone.Id == windowsId || zone.Id == primary) ?? matches.FirstOrDefault();
    }

    internal static string[] SearchWords(string query) =>
        query.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
