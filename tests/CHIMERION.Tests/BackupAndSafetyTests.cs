using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Запасной сервер в подключении (ConnectionMonitor.Backup, DnsSwitcher) на симуляторе сети.</summary>
[Collection("scenarios")]
public class BackupServerScenarioTests
{
    private static readonly DnsServer Main = new() { Name = "Основной", Primary = "203.0.113.10", Secondary = "203.0.113.11", DohUrl = "https://main.example/dns-query" };
    private static readonly DnsServer Spare = new() { Name = "Запасной", Primary = "198.51.100.20", Status = ServerStatus.Ok };

    private static NetSim.State OneEthernet() => new() { adapters = { new NetSim.Adapter() } };

    [Fact]
    public async Task Backup_goes_after_main_addresses_without_doh_and_is_removed_on_disconnect()
    {
        using var sim = new NetSim(OneEthernet());
        var log = new List<string>();
        await DnsSwitcher.ApplyAsync(Main, new AppSettings(), l => { lock (log) log.Add(l); }, Spare);

        Assert.Equal(new[] { "203.0.113.10", "203.0.113.11", "198.51.100.20" }, sim["Sim-Ethernet"].dns4);
        var doh = sim.Load().doh.Select(d => d.addr).ToList();
        Assert.Contains("203.0.113.10", doh);
        Assert.DoesNotContain("198.51.100.20", doh); // DoH-шаблон основного запасному не подходит
        var active = DnsSwitcher.GetActive()!;
        Assert.Equal(Spare.Id, active.BackupId);
        Assert.Equal(new[] { "198.51.100.20" }, active.BackupAddresses);
        Assert.Contains(log, l => l.StartsWith("Запасной сервер: Запасной"));

        await DnsSwitcher.RestoreAsync(_ => { });
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Empty(sim.Load().doh);
    }

    [Fact]
    public async Task Backup_is_in_nrpt_rules_in_split_mode()
    {
        using var sim = new NetSim(OneEthernet());
        var st = new AppSettings { SplitDns = { Enabled = true } };
        await DnsSwitcher.ApplyAsync(Main, st, _ => { }, Spare);
        var rule = Assert.Single(sim.Load().nrpt);
        Assert.Equal(new[] { "203.0.113.10", "203.0.113.11", "198.51.100.20" }, rule.servers);
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4); // адаптер в «Двух DNS» не трогали
        await DnsSwitcher.RestoreAsync(_ => { });
        Assert.Empty(sim.Load().nrpt);
    }

    [Fact]
    public async Task Backup_with_the_same_address_is_ignored()
    {
        using var sim = new NetSim(OneEthernet());
        var twin = new DnsServer { Name = "Двойник", Primary = "203.0.113.11", Status = ServerStatus.Ok };
        await DnsSwitcher.ApplyAsync(Main, new AppSettings(), _ => { }, twin);
        Assert.Equal(new[] { "203.0.113.10", "203.0.113.11" }, sim["Sim-Ethernet"].dns4);
        Assert.Null(DnsSwitcher.GetActive()!.BackupId);
        await DnsSwitcher.RestoreAsync(_ => { });
    }
}

public class BackupChoiceTests
{
    private static DnsServer Srv(string name, string ip, int stability, string? country = null, ServerStatus status = ServerStatus.Ok)
    {
        var s = new DnsServer { Name = name, Primary = ip, Status = status, LatencyMs = 30 };
        s.ChecksOk = stability; s.ChecksTotal = 100; s.LastChecked = DateTime.Now;
        if (country != null) foreach (var n in new[] { "ChatGPT", "Claude" }) s.EgressBy[n] = new Egress("192.0.2.1", country);
        return s;
    }

    [Fact]
    public void Backup_skips_main_addresses_ipv6_only_and_broken_servers()
    {
        var main = Srv("Основной", "203.0.113.10", 90); main.Secondary = "203.0.113.11";
        var list = new[]
        {
            main,
            Srv("Тот же адрес", "203.0.113.11", 99),
            Srv("Только IPv6", "2001:db8::1", 99),
            Srv("Сломан", "198.51.100.1", 99, status: ServerStatus.Fail),
            Srv("Годный", "198.51.100.2", 70),
        };
        Assert.Equal("Годный", ConnectionMonitor.Backup(list, main)!.Name);
        Assert.Null(ConnectionMonitor.Backup(new[] { main }, main));
    }

    [Fact]
    public void Backup_prefers_the_usual_country_over_a_bit_more_stable_server()
    {
        var safety = new AccountSafety();
        safety.Record("ChatGPT", "NL"); safety.Record("Claude", "NL");
        var main = Srv("Основной", "203.0.113.10", 90, "NL");
        var de = Srv("Германия", "198.51.100.3", 99, "DE");
        var nl = Srv("Нидерланды", "198.51.100.4", 80, "NL");
        Assert.Equal("Нидерланды", ConnectionMonitor.Backup(new[] { main, de, nl }, main, safety.Fit)!.Name);
        Assert.Equal("Германия", ConnectionMonitor.Backup(new[] { main, de, nl }, main)!.Name);
    }

    [Fact]
    public void Replacement_keeps_country_only_among_similarly_stable_servers()
    {
        var safety = new AccountSafety();
        safety.Record("ChatGPT", "NL");
        var active = new DnsSwitcher.ActiveConnection { ServerId = Guid.NewGuid() };
        var de = Srv("Германия", "198.51.100.3", 95, "DE");
        var nl = Srv("Нидерланды", "198.51.100.4", 91, "NL");
        var weakNl = Srv("Слабый NL", "198.51.100.5", 40, "NL");
        var order = ConnectionMonitor.Replacements(new[] { de, weakNl, nl }, active, fit: safety.Fit).Select(s => s.Name).ToList();
        Assert.Equal(new[] { "Нидерланды", "Германия", "Слабый NL" }, order);
    }
}

public class AccountSafetyTests
{
    [Fact]
    public void Home_is_the_country_with_most_days_and_mismatch_is_reported()
    {
        var day = new DateTime(2026, 10, 1);
        var old = AccountSafety.Today;
        try
        {
            var s = new AccountSafety();
            for (int i = 0; i < 5; i++) { var d = day.AddDays(i); AccountSafety.Today = () => d; s.Record("ChatGPT", "nl"); }
            AccountSafety.Today = () => day.AddDays(6);
            Assert.True(s.Record("ChatGPT", "DE"));
            Assert.False(s.Record("ChatGPT", "DE")); // тот же день второй раз не считается
            Assert.Equal("NL", s.Home("ChatGPT"));
            Assert.Equal(2, s.CountryCount("ChatGPT"));
            var m = Assert.Single(s.Compare(new[] { new KeyValuePair<string, string?>("ChatGPT", "DE"), new("Claude", "US"), new("Gemini", null) }));
            Assert.Equal(("ChatGPT", "NL", "DE"), (m.Service, m.Home, m.Now));
            Assert.Contains("→", AccountSafety.Describe(new[] { m }));
        }
        finally { AccountSafety.Today = old; }
    }

    [Fact]
    public void Garbage_country_is_ignored_and_history_survives_save_and_drops_old_days()
    {
        var old = AccountSafety.Today;
        try
        {
            AccountSafety.Today = () => new DateTime(2026, 9, 1);
            var s = new AccountSafety();
            Assert.False(s.Record("ChatGPT", "Netherlands"));
            Assert.False(s.Record("", "NL"));
            s.Record("ChatGPT", "US");
            AccountSafety.Today = () => new DateTime(2026, 10, 20);
            s.Record("ChatGPT", "NL");
            s.Save();
            var back = AccountSafety.Load();
            Assert.Equal("NL", back.Home("chatgpt")); // сентябрьский день старше 30 дней выброшен, регистр имени не важен
            Assert.Equal(1, back.CountryCount("ChatGPT"));
        }
        finally { AccountSafety.Today = old; File.Delete(Path.Combine(TestSetup.DataDir, AccountSafety.FileName)); }
    }

    [Fact]
    public void Fit_counts_services_kept_and_moved()
    {
        var s = new AccountSafety();
        s.Record("ChatGPT", "NL"); s.Record("Claude", "NL");
        var srv = new DnsServer();
        srv.EgressBy["ChatGPT"] = new Egress("192.0.2.1", "NL");
        srv.EgressBy["Claude"] = new Egress("192.0.2.1", "DE");
        srv.EgressBy["Gemini"] = new Egress("192.0.2.1", "US"); // истории нет: не считается
        Assert.Equal(0, s.Fit(srv));
        srv.EgressBy["Claude"] = new Egress("192.0.2.1", "NL");
        Assert.Equal(2, s.Fit(srv));
    }
}
