using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("scenarios")]
public class DohWindowsScenarioTests
{
    [Fact]
    public async Task Doh_timeout_does_not_disclose_the_endpoint()
    {
        using var server = new TinyHttp(_ => { Thread.Sleep(1000); return (200, "application/dns-message", Array.Empty<byte>()); });
        var error = await Assert.ThrowsAsync<TimeoutException>(() => DnsWire.ResolveDohAsync(
            server.Url + "/secret-path?token=secret-query", "example.test", TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain(server.Url, error.Message);
    }

    [Fact]
    public async Task Report_state_does_not_request_doh_templates()
    {
        var old = DnsSwitcher.Runner;
        try
        {
            string? script = null;
            DnsSwitcher.Runner = (value, _) => { script = value; return Task.FromResult((0, "DoH 203.0.113.5")); };
            await DnsSwitcher.ReportStateAsync(_ => { });
            Assert.NotNull(script);
            Assert.DoesNotContain(".DohTemplate", script);
        }
        finally { DnsSwitcher.Runner = old; }
    }

    [Fact]
    public void Old_relay_state_is_cleaned_without_losing_recovery_data()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } });
        var channel = Guid.NewGuid().ToString("N");
        Storage.SaveRequired("active.json", new DnsSwitcher.ActiveConnection
        {
            Name = "DoH", Addresses = new() { "127.0.0.2" }, RelayChannel = channel,
            RelayListen = "127.0.0.2", DohTemplate = "https://203.0.113.5/dns-query/secret"
        });
        var active = DnsSwitcher.GetActive();
        Assert.Equal(channel, active?.RelayChannel);
        Assert.Null(active?.DohTemplate);
        var saved = File.ReadAllText(Path.Combine(TestSetup.DataDir, "active.json"));
        Assert.DoesNotContain("secret", saved);
        Assert.Contains(channel, saved);
    }

    [Fact]
    public async Task Proxy_failure_with_ip_does_not_change_network()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } }) { NoDoh = true, ProxyFails = true };
        var server = new DnsServer { Name = "DoH", Primary = "203.0.113.7", DohUrl = "https://203.0.113.5/dns-query" };
        await Assert.ThrowsAsync<DohRelayFailedException>(() => DnsSwitcher.ApplyAsync(server, new AppSettings(), _ => { }));
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
        Assert.Null(DnsSwitcher.GetActive());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_without_native_doh_uses_proxy_preserves_query_and_hides_token(bool withIp)
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } }) { NoDoh = true };
        Uri? endpoint = null;
        DnsSwitcher.DohProxyStart = (uri, _, _, _) =>
        {
            endpoint = uri;
            var proxy = new DohProxySession("127.0.0.2", 53);
            sim.ProxiesStarted.Add(proxy);
            return Task.FromResult(proxy);
        };
        var server = new DnsServer { Name = "DoH", Primary = withIp ? "203.0.113.7" : "", DohUrl = "https://203.0.113.5/dns-query/secret-path?token=secret-query" };
        await DnsSwitcher.ApplyAsync(server, new AppSettings(), _ => { });
        Assert.Equal("?token=secret-query", endpoint?.Query);
        Assert.Single(sim.ProxiesStarted);
        Assert.Equal(new[] { "127.0.0.2" }, sim["Sim-Ethernet"].dns4);
        var saved = File.ReadAllText(Path.Combine(TestSetup.DataDir, "active.json"));
        Assert.DoesNotContain("secret-path", saved);
        Assert.DoesNotContain("secret-query", saved);
        await DnsSwitcher.RestoreAsync(_ => { });
        Assert.Equal(new[] { "192.168.1.1" }, sim["Sim-Ethernet"].dns4);
    }

    [Fact]
    public void Old_native_doh_state_is_protected_and_remains_readable()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } });
        const string endpoint = "https://203.0.113.5/dns-query/secret?token=example";
        Storage.SaveRequired("active.json", new DnsSwitcher.ActiveConnection
        {
            Name = "DoH", Addresses = new() { "203.0.113.7" }, DohTemplate = endpoint
        });
        var active = DnsSwitcher.GetActive();
        Assert.Equal(endpoint, active?.ReadDohTemplate());
        Assert.Null(active?.DohTemplate);
        var saved = File.ReadAllText(Path.Combine(TestSetup.DataDir, "active.json"));
        Assert.DoesNotContain("secret", saved);
        Assert.DoesNotContain("example", saved);
        Assert.Contains("dpapi:", saved);
    }

    [Fact]
    public async Task Native_doh_with_literal_ip_needs_no_dns_bootstrap()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } });
        var server = new DnsServer { Name = "DoH", DohUrl = "https://203.0.113.5/dns-query?token=example" };
        await DnsSwitcher.ApplyAsync(server, new AppSettings(), _ => { });
        Assert.Equal(new[] { "203.0.113.5" }, sim["Sim-Ethernet"].dns4);
        Assert.Contains(sim.Load().doh, d => d.addr == "203.0.113.5" && d.template.EndsWith("?token=example"));
        await DnsSwitcher.RestoreAsync(_ => { });
    }

    [Fact]
    public async Task Native_doh_keeps_query_parameters()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } });
        var server = new DnsServer { Name = "DoH", Primary = "203.0.113.7", Ipv6 = new() { "2001:db8::1" }, DohUrl = "https://dns.example.test/dns-query?token=example" };
        await DnsSwitcher.ApplyAsync(server, new AppSettings(), _ => { });
        Assert.Contains(sim.Load().doh, d => d.template.EndsWith("?token=example"));
        var active = DnsSwitcher.GetActive();
        Assert.Equal(server.DohUrl, active?.ReadDohTemplate());
        Assert.DoesNotContain("token=example", File.ReadAllText(Path.Combine(TestSetup.DataDir, "active.json")));
        await DnsSwitcher.RestoreAsync(_ => { });
    }
}
