using Dnex.Models;
using Xunit;

namespace Dnex.Tests;

/// <summary>Стабильность стареет (аудит CR-09): свежие проверки весят больше, чем давние.</summary>
public class StabilityAgingTests
{
    private static void Check(DnsServer s, bool ok)
    {
        s.ChecksTotal++;
        if (ok) s.ChecksOk++;
        s.ServiceChecks++;
        if (ok) { s.ServiceOk["ChatGPT"] = s.ServiceOk.GetValueOrDefault("ChatGPT") + 1; s.AllOk++; }
        s.AgeCounters();
    }

    [Fact]
    public void Broken_veteran_loses_stability_quickly()
    {
        var s = new DnsServer { Name = "ветеран", Primary = "203.0.113.5" };
        for (int i = 0; i < 200; i++) Check(s, true);
        Assert.InRange(s.ChecksTotal, DnsServer.StabilityWindow, 2 * DnsServer.StabilityWindow - 1);
        Assert.Equal(100, s.ServiceStability("ChatGPT"));
        for (int i = 0; i < 20; i++) Check(s, false);
        // За всё время было бы 200/220 = 90%. С окном — около 70%
        Assert.InRange(s.ChecksOk * 100 / s.ChecksTotal, 60, 75);
        Assert.InRange(s.ServiceStability("ChatGPT")!.Value, 60, 75);
        Assert.InRange(s.AllStability!.Value, 60, 75);
    }

    [Fact]
    public void Few_checks_are_not_touched()
    {
        var s = new DnsServer { Name = "новый", Primary = "203.0.113.6" };
        for (int i = 0; i < 30; i++) Check(s, i % 3 != 0);
        Assert.Equal(30, s.ChecksTotal);
        Assert.Equal(20, s.ChecksOk);
        Assert.Equal(30, s.ServiceChecks);
    }

    [Fact]
    public void Steady_share_stays_steady()
    {
        var s = new DnsServer { Name = "ровный", Primary = "203.0.113.7" };
        for (int i = 0; i < 1000; i++) Check(s, i % 4 != 0); // 75%
        Assert.InRange(s.ChecksOk * 100 / s.ChecksTotal, 70, 80);
    }
}
