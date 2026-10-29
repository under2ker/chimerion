using Dnex.Models;
using Dnex.Services;
namespace Dnex.Tests;
[Collection("scenarios")]
public class Ipv6CrashAuditTests
{
    [Fact]
    public async Task Restore_after_crash_between_disable_and_marker()
    {
        var a = new NetSim.Adapter { dns6 = new() { "fd00::1" } };
        using var sim = new NetSim(new NetSim.State { adapters = { a } });
        var applied = new TaskCompletionSource();
        var hang = new TaskCompletionSource();
        sim.Before = async script =>
        {
            if (!script.Contains("$ifs =")) return;
            var modified = script.Replace("Disable-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop", "Disable-NetAdapterBinding -Name $i.Name -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop\nthrow 'simulated termination before marker'");
            await sim.RunReal(modified);
            applied.SetResult();
            await hang.Task;
        };
        _ = DnsSwitcher.ApplyAsync(new DnsServer { Name = "Demo", Primary = "203.0.113.10" }, new AppSettings(), _ => { });
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(sim["Sim-Ethernet"].v6);
        sim.Before = null;
        await DnsSwitcher.RestoreAsync(_ => { });
        Assert.True(sim["Sim-Ethernet"].v6);
    }
}