using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>
/// Срок действия измерения страны: старая или перенесенная из прошлой проверки страна не подтверждает подключение,
/// а неизвестная страна ChatGPT и Claude останавливает автоматический выбор.
/// </summary>
[Collection("scenarios")]
public class EgressFreshnessTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);

    public EgressFreshnessTests() => AccountSafety.Pinned = null;
    public void Dispose() => AccountSafety.Pinned = null;

    private static DnsServer Srv(string name, string ip, string summary, params (string Service, string Country, DateTime? At)[] egress)
    {
        var s = new DnsServer { Name = name, Primary = ip, Status = ServerStatus.Ok, LatencyMs = 30, Summary = summary, LastChecked = Now };
        s.ChecksOk = 90; s.ChecksTotal = 100;
        foreach (var (svc, c, at) in egress) s.EgressBy[svc] = new Egress("192.0.2.1", c, at);
        return s;
    }

    [Fact]
    public void Measurement_expires_and_unknown_time_is_not_fresh()
    {
        Assert.False(new Egress("192.0.2.1", "NL").FreshAt(Now));
        Assert.True(new Egress("192.0.2.1", "NL", Now.AddHours(-11)).FreshAt(Now));
        Assert.False(new Egress("192.0.2.1", "NL", Now.AddHours(-13)).FreshAt(Now));
    }

    [Fact]
    public void Unknown_lists_opened_strict_services_without_fresh_country()
    {
        var s = Srv("A", "203.0.113.1", "ChatGPT ✓ · Claude ✓ · Gemini ✓", ("ChatGPT", "NL", Now));
        Assert.Equal(new[] { "Claude" }, AccountSafety.Unknown(s, Now));

        s.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now.AddDays(-2)); // устарела: все равно неизвестна
        Assert.Equal(new[] { "Claude" }, AccountSafety.Unknown(s, Now));

        s.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now);
        Assert.Empty(AccountSafety.Unknown(s, Now)); // Gemini не в Strict: его страна часто не определяется

        var closed = Srv("B", "203.0.113.2", "ChatGPT ✓ · Claude ✗ регион", ("ChatGPT", "NL", Now));
        Assert.Empty(AccountSafety.Unknown(closed, Now)); // Claude через сервер не открывается: и страна его не нужна
    }

    [Fact]
    public void Backup_needs_fresh_country_for_claude_on_both_servers()
    {
        var main = Srv("Основной", "203.0.113.10", "ChatGPT ✓ · Claude ✓", ("ChatGPT", "NL", Now));
        var spare = Srv("Запасной", "198.51.100.20", "ChatGPT ✓ · Claude ✓", ("ChatGPT", "NL", Now), ("Claude", "KZ", Now));
        // Раньше сравнивались только известные у основного сервисы: Claude без данных пропускался, и запасной вел его в KZ
        Assert.Null(ConnectionMonitor.Backup(new[] { main, spare }, main, now: Now));

        main.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now);
        Assert.Null(ConnectionMonitor.Backup(new[] { main, spare }, main, now: Now));

        spare.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now.AddHours(-20)); // страна та же, но измерена давно
        Assert.Null(ConnectionMonitor.Backup(new[] { main, spare }, main, now: Now));

        spare.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now.AddHours(-1));
        Assert.Equal("Запасной", ConnectionMonitor.Backup(new[] { main, spare }, main, now: Now)!.Name);
    }

    [Fact]
    public void Pinned_country_does_not_pass_server_with_unknown_claude()
    {
        AccountSafety.Pinned = "NL";
        var s = Srv("A", "203.0.113.1", "ChatGPT ✓ · Claude ✓", ("ChatGPT", "NL", Now));
        Assert.False(AccountSafety.Fits(s, Now));
        s.EgressBy["Claude"] = new Egress("192.0.2.1", "NL", Now);
        Assert.True(AccountSafety.Fits(s, Now));
        Assert.False(AccountSafety.Fits(s, Now.AddDays(1))); // через сутки без проверки — уже не подтверждено
    }

    [Fact]
    public void Stale_country_is_not_reported_as_current()
    {
        var s = Srv("A", "203.0.113.1", "ChatGPT ✓", ("ChatGPT", "DE", Now.AddDays(-3)), ("Claude", "NL", Now));
        Assert.Equal(new[] { "Claude" }, AccountSafety.EgressOf(s, Now).Select(kv => kv.Key));
    }

    [Fact]
    public void Legacy_list_gets_measurement_time_from_last_check()
    {
        var s = Srv("A", "203.0.113.1", "ChatGPT ✓", ("ChatGPT", "NL", null), ("Claude", "NL", Now.AddHours(-1)));
        s.LastChecked = Now.AddHours(-2);
        s.DateLegacyEgress();
        Assert.Equal(Now.AddHours(-2), s.EgressBy["ChatGPT"].At);
        Assert.Equal(Now.AddHours(-1), s.EgressBy["Claude"].At); // уже с временем: не трогаем
    }

    [Fact]
    public void Carried_country_is_not_measured_by_new_snapshot()
    {
        var snap = new NetSnapshot { Taken = Now };
        var carried = new ServiceState("Claude", "claude.ai", "203.0.113.5", true, "✓ доступен", Egress: new Egress("192.0.2.1", "NL", Now.AddMinutes(-40)));
        var measured = carried with { Egress = new Egress("192.0.2.1", "NL", Now.AddSeconds(3)) };
        Assert.False(snap.Measured(carried));
        Assert.True(snap.Measured(measured));
        Assert.False(snap.Measured(carried with { Egress = null }));
    }

    [Fact]
    public void Freshness_text_says_how_old_the_country_is()
    {
        Assert.Equal("Страна проверена только что", AccountRiskWindow.FreshnessText(new(Now, false), Now.AddSeconds(20)));
        Assert.Equal("Страна проверена 12 мин назад", AccountRiskWindow.FreshnessText(new(Now, false), Now.AddMinutes(12)));
        Assert.Equal("Страна проверена 2 ч назад", AccountRiskWindow.FreshnessText(new(Now, false), Now.AddMinutes(130)));
        Assert.Equal("После смены сети страна еще не проверена", AccountRiskWindow.FreshnessText(new(null, true), Now));
        Assert.Null(AccountRiskWindow.FreshnessText(new(null, false), Now));
    }
}
