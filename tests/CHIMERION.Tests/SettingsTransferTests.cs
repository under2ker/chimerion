using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Перенос настроек и избранного: что попадает в файл, что из него принимается и от чего защищает импорт.</summary>
public class SettingsTransferTests
{
    private static DnsServer Fav(string name, string ip, bool fav = true) => new() { Name = name, Primary = ip, IsFavorite = fav };

    [Fact]
    public void Export_contains_favorites_and_safe_settings_but_no_secrets()
    {
        var s = new AppSettings { Theme = "light", Language = "en", WatchdogSeconds = 90 };
        s.HostCookies["4pda.to"] = "member_id=1; pass_hash=SECRET";
        s.CrowdConsent = true;
        s.SplitDns.Enabled = true;
        var json = SettingsTransfer.Export(s, new[] { Fav("Амстердам", "203.0.113.11"), Fav("Не избранный", "203.0.113.12", fav: false) });
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("4pda", json);
        Assert.DoesNotContain("crowd", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("splitDns", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("203.0.113.11", json);
        Assert.DoesNotContain("203.0.113.12", json);
        Assert.Contains("\"theme\": \"light\"", json);
    }

    [Fact]
    public void Roundtrip_restores_settings_and_favorites()
    {
        var src = new AppSettings { Theme = "light", Language = "en", TimeoutSeconds = 9, Parallelism = 12, StartCompact = true, EgressCountry = "NL" };
        src.Colors["Accent"] = "#FF8800";
        var (plan, error) = SettingsTransfer.Parse(SettingsTransfer.Export(src, new[] { Fav("Амстердам", "203.0.113.11") }));
        Assert.Null(error);
        var target = new AppSettings();
        Assert.True(SettingsTransfer.Apply(plan!.Settings, target) >= 6);
        Assert.Equal("light", target.Theme); Assert.Equal("en", target.Language); Assert.Equal(9, target.TimeoutSeconds);
        Assert.Equal(12, target.Parallelism); Assert.True(target.StartCompact); Assert.Equal("NL", target.EgressCountry);
        Assert.Equal("#FF8800", target.Colors["Accent"]);
        Assert.Equal("203.0.113.11", Assert.Single(plan.Favorites).Primary);
    }

    [Fact]
    public void Import_never_touches_cookies_sources_targets_split_dns_or_consent()
    {
        var file = """
        { "format": 1, "app": "CHIMERION", "settings": { "theme": "dark",
          "hostCookies": { "x.com": "pass=1" }, "crowdConsent": true, "sources": [ { "name": "evil", "url": "https://evil.example/" } ],
          "targets": [ { "name": "x", "host": "192.168.0.1" } ], "splitDns": { "enabled": true }, "excludeIps": [ "1.2.3.4" ], "skippedUpdate": "9.9.9" } }
        """;
        var (plan, _) = SettingsTransfer.Parse(file);
        var target = new AppSettings();
        var sourcesBefore = target.Sources.Count; var targetsBefore = target.Targets.Count; var excludeBefore = target.ExcludeIps.Count;
        SettingsTransfer.Apply(plan!.Settings, target);
        Assert.Empty(target.HostCookies);
        Assert.Null(target.CrowdConsent);
        Assert.False(target.SplitDns.Enabled);
        Assert.Equal(sourcesBefore, target.Sources.Count); Assert.Equal(targetsBefore, target.Targets.Count);
        Assert.Equal(excludeBefore, target.ExcludeIps.Count);
        Assert.Null(target.SkippedUpdate);
    }

    [Fact]
    public void Numbers_are_clamped_to_safe_ranges()
    {
        var (plan, _) = SettingsTransfer.Parse("""{ "format": 1, "app": "CHIMERION", "settings": { "timeoutSeconds": 100000, "parallelism": 0, "watchdogSeconds": 1, "trayCheckMinutes": -5 } }""");
        var s = plan!.Settings;
        Assert.Equal(30, s.TimeoutSeconds); Assert.Equal(1, s.Parallelism); Assert.Equal(15, s.WatchdogSeconds); Assert.Equal(1, s.TrayCheckMinutes);
    }

    [Fact]
    public void Wrong_theme_language_color_and_country_are_dropped()
    {
        var (plan, _) = SettingsTransfer.Parse("""{ "format": 1, "app": "CHIMERION", "settings": { "theme": "<script>", "language": "xx", "egressCountry": "NLD", "colors": { "Accent": "red", "bad key!": "#112233", "Ok": "#00FF00" } } }""");
        var s = plan!.Settings;
        Assert.Null(s.Theme); Assert.Null(s.Language); Assert.Null(s.EgressCountry);
        Assert.Equal(new[] { "Ok" }, s.Colors!.Keys);
        Assert.NotEmpty(plan.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("{}")]
    [InlineData("""{ "app": "Other", "format": 1 }""")]
    [InlineData("""{ "app": "CHIMERION", "format": 99 }""")]
    [InlineData("""{ "app": "CHIMERION", "format": 0 }""")]
    public void Foreign_or_broken_files_are_refused_with_a_reason(string json)
    {
        var (plan, error) = SettingsTransfer.Parse(json);
        Assert.Null(plan);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Huge_file_is_refused()
        => Assert.NotNull(SettingsTransfer.Parse(new string(' ', (int)SettingsTransfer.MaxBytes + 1)).Error);

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("не адрес")]
    public void Favorite_with_local_or_broken_address_is_skipped(string ip)
    {
        var json = $$"""{ "format": 1, "app": "CHIMERION", "favorites": [ { "name": "X", "primary": "{{ip}}" } ] }""";
        var (plan, _) = SettingsTransfer.Parse(json);
        Assert.Empty(plan!.Favorites);
        Assert.Contains(plan.Skipped, x => x.Contains("адрес не подошел"));
    }

    [Theory]
    [InlineData("https://dns.example.com/dns-query", true)]
    [InlineData("https://1.1.1.1/dns-query", true)]
    [InlineData("http://dns.example.com/dns-query", false)]
    [InlineData("https://localhost/dns-query", false)]
    [InlineData("https://192.168.1.1/dns-query", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Doh_must_be_https_and_not_local(string url, bool accepted)
    {
        var s = SettingsTransfer.CleanServer(new PortableServer { Name = "D", DohUrl = url });
        Assert.Equal(accepted, s != null);
    }

    [Fact]
    public void Duplicates_are_merged_names_cleaned_and_count_limited()
    {
        var favs = string.Join(",", Enumerable.Range(0, 400).Select(i => $$"""{ "name": "S{{i}}", "primary": "203.0.{{i / 250}}.{{i % 250 + 1}}" }"""));
        var (plan, _) = SettingsTransfer.Parse($$"""{ "format": 1, "app": "CHIMERION", "favorites": [ { "name": "Дубль", "primary": "203.0.113.5" }, { "name": "Дубль 2", "primary": "203.0.113.5" }, {{favs}} ] }""");
        Assert.True(plan!.Favorites.Count <= SettingsTransfer.MaxFavorites);
        Assert.Single(plan.Favorites, f => f.Primary == "203.0.113.5");
        var clean = SettingsTransfer.CleanServer(new PortableServer { Name = "  Сер\u0001вер\n" + new string('я', 300), Primary = "203.0.113.9" })!;
        Assert.DoesNotContain(clean.Name, c => char.IsControl(c));
        Assert.True(clean.Name.Length <= 100);
    }
}
