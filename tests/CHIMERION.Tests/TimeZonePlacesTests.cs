using Dnex.Services;

namespace Dnex.Tests;

public class TimeZonePlacesTests
{
    [Theory]
    [InlineData(false, "Германия", "Europe/Berlin", "Берлин, Германия")]
    [InlineData(true, "Берлин, Germany", "Europe/Berlin", "Berlin, Germany")]
    [InlineData(false, "Нью-Йорк, США", "America/New_York", "Нью-Йорк, США")]
    [InlineData(true, "United States Los Angeles", "America/Los_Angeles", "Los Angeles, United States")]
    [InlineData(false, "Санкт-Петербург Россия", "Saint_Petersburg", "Санкт-Петербург, Россия")]
    [InlineData(false, "UTC+9 Tokyo", "Asia/Tokyo", "Токио, Япония")]
    public void Finds_a_city_by_name_country_and_offset(bool english, string query, string id, string label)
    {
        var choice = Assert.Single(TimeZonePlaces.List(english), place => place.Id == id);
        Assert.Equal(label, choice.Label);
        var offset = choice.At(new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero), windows: false).Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        Assert.True(choice.Matches(TimeZonePlaces.SearchWords(query), $"UTC{sign}{offset.Duration():hh\\:mm}"));
    }

    [Fact]
    public void Keeps_separate_cities_and_resolves_both_time_modes()
    {
        var choices = TimeZonePlaces.List(english: true);
        var berlin = Assert.Single(choices, place => place.Id == "Europe/Berlin");
        var munich = Assert.Single(choices, place => place.Id == "Munich");
        Assert.Equal(berlin.IanaId, munich.IanaId);
        Assert.Equal("W. Europe Standard Time", berlin.Windows?.Id);
        var july = new DateTimeOffset(2026, 7, 10, 10, 30, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(2), berlin.At(july, windows: false).Offset);
        Assert.Equal(TimeSpan.FromHours(2), berlin.At(july, windows: true).Offset);
        Assert.Equal("Europe/Berlin", TimeZonePlaces.Default(choices, "W. Europe Standard Time")?.Id);
    }

    [Fact]
    public void Uses_the_selected_city_in_a_country_with_several_time_zones()
    {
        var usa = TimeZonePlaces.List(english: true)
            .Where(place => place.Matches(TimeZonePlaces.SearchWords("United States"), "UTC+00:00"))
            .ToDictionary(place => place.Id);
        var instant = new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(-5), usa["America/New_York"].At(instant, windows: false).Offset);
        Assert.Equal(TimeSpan.FromHours(-8), usa["America/Los_Angeles"].At(instant, windows: false).Offset);
    }

    [Theory]
    [InlineData("Нидерланды")]
    [InlineData("Нидерландов")]
    [InlineData("Голландия")]
    [InlineData("Holland")]
    [InlineData("Netherlands")]
    public void Finds_the_Dutch_cities_by_country_name(string query)
    {
        var cities = TimeZonePlaces.List(english: false).Where(place => place.CountryCode == "NL").ToArray();
        Assert.Equal(new[] { "Амстердам", "Гаага", "Роттердам", "Утрехт", "Эйндховен" },
            cities.Select(city => city.Name));
        Assert.All(cities, city =>
        {
            Assert.True(city.Matches(TimeZonePlaces.SearchWords(query), "UTC+01:00"));
            Assert.Equal("Europe/Amsterdam", city.IanaId);
            Assert.Equal("W. Europe Standard Time", city.Windows?.Id);
        });
    }
    [Fact]
    public void Contains_geographic_choices_and_all_native_Windows_zones()
    {
        var choices = TimeZonePlaces.List(english: true);
        Assert.True(choices.Count(place => place.Country.Length > 0) >= 400);
        Assert.Equal(247, choices.Where(place => place.CountryCode.Length > 0).Select(place => place.CountryCode).Distinct().Count());
        Assert.Equal(choices.Count, choices.Select(place => place.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var nativeIds = choices.Select(place => place.Windows?.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(TimeZoneInfo.GetSystemTimeZones(), zone => Assert.Contains(zone.Id, nativeIds));
        Assert.Equal("UTC", TimeZonePlaces.Default(choices, "UTC")?.Id);
        Assert.Contains("Antarctica/Troll", TimeZoneSettings.BrowserCommand("Antarctica/Troll"));
    }
}
