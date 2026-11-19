using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Откат проверки, которую испортил сбой сети: серверы остаются такими, какими были до нее.</summary>
public class CheckSnapshotTests
{
    private static DnsServer WorkingServer(int historyPoints)
    {
        var s = new DnsServer { Name = "Тест", Status = ServerStatus.Ok, LatencyMs = 50, JitterMs = 7, Summary = "ChatGPT ✓ · Claude ✓", LastChecked = new DateTime(2026, 11, 17, 8, 0, 0) };
        s.ChecksTotal = 40; s.ChecksOk = 38; s.FailStreak = 0; s.ServiceChecks = 40; s.AllOk = 36;
        s.ServiceOk = new() { ["ChatGPT"] = 38, ["Claude"] = 37 };
        s.EgressBy["Claude"] = new Egress("203.0.113.5", "NL");
        var start = DateTime.Now.AddHours(-historyPoints);
        for (int i = 0; i < historyPoints; i++) s.AddHistory(true, 50 + i % 5, start.AddHours(i));
        return s;
    }

    /// <summary>То, что делает проверка с сервером, когда сеть оборвалась: все «не ответил».</summary>
    private static void SpoilByOutage(DnsServer s)
    {
        s.Status = ServerStatus.Fail; s.LatencyMs = null; s.JitterMs = null; s.Summary = "ChatGPT ✗ · Claude ✗";
        s.LastError = "нет ответа"; s.LastChecked = DateTime.Now; s.EgressBy.Remove("Claude");
        s.ChecksTotal++; s.FailStreak++; s.ServiceChecks++;
        s.AddHistory(false, null);
    }

    [Fact]
    public void Restore_brings_back_status_and_last_good_result()
    {
        var s = WorkingServer(30);
        var snap = CheckSnapshot.Take(s);
        SpoilByOutage(s);
        snap.Restore(s);

        Assert.Equal(ServerStatus.Ok, s.Status);   // иначе «Рабочие» опустеет из-за сбоя сети
        Assert.Equal(50, s.LatencyMs);
        Assert.Equal("ChatGPT ✓ · Claude ✓", s.Summary);
        Assert.Null(s.LastError);
        Assert.Equal(new DateTime(2026, 11, 17, 8, 0, 0), s.LastChecked);
        Assert.Equal((40, 38, 0, 40), (s.ChecksTotal, s.ChecksOk, s.FailStreak, s.ServiceChecks));
        Assert.Equal("NL", s.EgressBy["Claude"].Country);
        Assert.Equal(30, s.HistoryCount);
        Assert.All(s.History, p => Assert.True(p.Ok));
    }

    [Fact]
    public void Restore_with_full_history_drops_the_bad_point_and_keeps_the_oldest()
    {
        var s = WorkingServer(DnsServer.MaxHistory);
        var oldest = s.History[0];
        var snap = CheckSnapshot.Take(s);
        SpoilByOutage(s);                       // полная история: новая точка вытеснила самую старую
        Assert.NotEqual(oldest, s.History[0]);
        snap.Restore(s);

        Assert.Equal(DnsServer.MaxHistory, s.HistoryCount);
        Assert.Equal(oldest, s.History[0]);     // «обрезать до N» оставило бы ошибочную точку и потеряло эту
        Assert.All(s.History, p => Assert.True(p.Ok));
    }

    [Fact]
    public void Snapshot_is_independent_of_later_changes()
    {
        var s = WorkingServer(5);
        var snap = CheckSnapshot.Take(s);
        s.ServiceOk["ChatGPT"] = 0; s.EgressBy.Clear(); s.AddHistory(false, null);
        Assert.Equal(38, snap.ServiceOk["ChatGPT"]);
        Assert.Single(snap.EgressBy);
        Assert.Equal(5, snap.History.Count);
    }
}
