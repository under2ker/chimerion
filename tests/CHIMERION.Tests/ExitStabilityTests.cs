using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Постоянство выхода по накопленным проверкам и его место в выборе замены и запасного сервера.</summary>
[Collection("scenarios")]
public class ExitStabilityTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);

    private static List<ExitSeen> Seen(params (int HoursAgo, string Ip, string Country)[] list) =>
        list.Select(x => new ExitSeen(Now.AddHours(-x.HoursAgo), x.Ip, x.Country)).ToList();

    [Fact]
    public void Few_checks_say_nothing()
    {
        var v = ExitStability.Assess(Seen((5, "192.0.2.1", "NL"), (1, "192.0.2.1", "NL")), Now);
        Assert.Equal(ExitStability.Kind.Unknown, v.Kind);
    }

    [Fact]
    public void Same_address_and_country_is_stable()
    {
        var v = ExitStability.Assess(Seen((30, "192.0.2.1", "NL"), (20, "192.0.2.1", "NL"), (10, "192.0.2.1", "NL"), (1, "192.0.2.1", "NL")), Now);
        Assert.Equal(ExitStability.Kind.Stable, v.Kind);
        Assert.Equal("Выход стабилен по последним 4 проверкам", v.Text);
    }

    [Fact]
    public void Rotating_address_in_one_country_is_neutral_not_danger()
    {
        var v = ExitStability.Assess(Seen((30, "192.0.2.1", "NL"), (20, "192.0.2.2", "NL"), (10, "192.0.2.3", "NL"), (1, "192.0.2.1", "NL")), Now);
        Assert.Equal(ExitStability.Kind.Rotates, v.Kind);
        Assert.Contains("страна та же", v.Text);
        var s = new DnsServer { ExitSeen = Seen((30, "192.0.2.1", "NL"), (20, "192.0.2.2", "NL"), (10, "192.0.2.3", "NL")) };
        Assert.Equal(0, ExitStability.Rank(s, Now)); // смена адреса не штрафуется
    }

    [Fact]
    public void Country_change_is_reported_and_ranked_lower()
    {
        var seen = Seen((30, "192.0.2.1", "NL"), (20, "192.0.2.1", "NL"), (10, "198.51.100.1", "DE"));
        Assert.Equal(ExitStability.Kind.CountryChanges, ExitStability.Assess(seen, Now).Kind);
        Assert.Equal(-1, ExitStability.Rank(new DnsServer { ExitSeen = seen }, Now));
    }

    [Fact]
    public void Old_observations_are_ignored()
    {
        var seen = Seen((24 * 20, "198.51.100.1", "DE"), (30, "192.0.2.1", "NL"), (20, "192.0.2.1", "NL"), (10, "192.0.2.1", "NL"));
        Assert.Equal(ExitStability.Kind.Stable, ExitStability.Assess(seen, Now).Kind);
    }

    [Fact]
    public void Note_merges_bursts_and_keeps_last_entries()
    {
        var list = new List<ExitSeen>();
        ExitStability.Note(list, new ExitSeen(Now, "192.0.2.1", "NL"));
        ExitStability.Note(list, new ExitSeen(Now.AddMinutes(3), "192.0.2.2", "NL")); // пачка проверок подряд — одна запись
        Assert.Single(list);
        Assert.Equal("192.0.2.2", list[0].Ip);
        for (int i = 1; i <= 20; i++) ExitStability.Note(list, new ExitSeen(Now.AddHours(i), "192.0.2.1", "NL"));
        Assert.Equal(ExitStability.Keep, list.Count);
        Assert.Equal(Now.AddHours(20), list[^1].At);
    }

    [Fact]
    public void Replacement_prefers_stable_exit_over_slightly_faster()
    {
        DnsServer Srv(string name, string ip, long ms, List<ExitSeen> seen)
        {
            var s = new DnsServer { Name = name, Primary = ip, Status = ServerStatus.Ok, LatencyMs = ms, ExitSeen = seen, LastChecked = Now };
            s.ChecksOk = 95; s.ChecksTotal = 100;
            return s;
        }
        var stable = Srv("Стабильный", "198.51.100.2", 60, Seen((30, "192.0.2.1", "NL"), (20, "192.0.2.1", "NL"), (10, "192.0.2.1", "NL")));
        var jumpy = Srv("Менял страну", "198.51.100.3", 20, Seen((30, "192.0.2.5", "NL"), (20, "198.51.100.9", "DE"), (10, "192.0.2.5", "NL")));
        var active = new DnsSwitcher.ActiveConnection { ServerId = Guid.NewGuid(), Name = "Текущий" };
        var order = ConnectionMonitor.Replacements(new[] { jumpy, stable }, active, now: Now).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "Стабильный", "Менял страну" }, order);
    }
}
