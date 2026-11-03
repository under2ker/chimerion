using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayActivationTests
{
    private sealed class Sim
    {
        public readonly List<string> Steps = new();
        public bool Relay, Recovery, Network, Verified = true, FailApply, FailRestore, FailWrite;
        public DohRelayActivation Create() => new(
            _ => { Steps.Add("relay-start"); Relay = true; return Task.CompletedTask; },
            _ => { Steps.Add("recovery-write"); if (FailWrite) throw new IOException("write failed"); Recovery = true; return Task.CompletedTask; },
            _ => { Steps.Add("network-apply"); Network = true; if (FailApply) throw new InvalidOperationException(); return Task.CompletedTask; },
            _ => { Steps.Add("verify"); return Task.FromResult(Verified); },
            () => { Steps.Add("restore"); if (FailRestore) throw new InvalidOperationException(); Network = false; return Task.CompletedTask; },
            () => { Steps.Add("relay-stop"); Relay = false; return Task.CompletedTask; },
            () => { Steps.Add("recovery-clear"); Recovery = false; return Task.CompletedTask; });
    }

    [Fact]
    public async Task Starts_relay_and_saves_recovery_before_network_change_then_restores_before_stopping()
    {
        var sim = new Sim(); var activation = sim.Create();
        await activation.StartAsync(CancellationToken.None);
        Assert.True(sim.Relay && sim.Network && sim.Recovery);
        await activation.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { "relay-start", "recovery-write", "network-apply", "verify", "restore", "relay-stop", "recovery-clear" }, sim.Steps);
        Assert.False(sim.Relay || sim.Network || sim.Recovery);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_apply_or_verification_rolls_back_before_stopping(bool failApply)
    {
        var sim = new Sim { FailApply = failApply, Verified = false }; var activation = sim.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.StartAsync(CancellationToken.None));
        Assert.False(sim.Relay || sim.Network || sim.Recovery);
        Assert.True(sim.Steps.IndexOf("restore") < sim.Steps.IndexOf("relay-stop"));
    }

    [Fact]
    public async Task Failed_recovery_write_does_not_touch_network_or_clear_old_recovery()
    {
        var sim = new Sim { FailWrite = true, Recovery = true }; var activation = sim.Create();
        await Assert.ThrowsAsync<IOException>(() => activation.StartAsync(CancellationToken.None));
        Assert.False(sim.Network || sim.Relay);
        Assert.True(sim.Recovery);
        Assert.DoesNotContain("network-apply", sim.Steps);
        Assert.DoesNotContain("recovery-clear", sim.Steps);
    }

    [Fact]
    public async Task Failed_stop_restore_keeps_relay_and_recovery()
    {
        var sim = new Sim(); var activation = sim.Create();
        await activation.StartAsync(CancellationToken.None);
        sim.FailRestore = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => activation.StopAsync(CancellationToken.None));
        Assert.True(sim.Relay && sim.Network && sim.Recovery);
        Assert.DoesNotContain("relay-stop", sim.Steps);
    }

    [Fact]
    public async Task Failed_restore_keeps_relay_and_recovery_data()
    {
        var sim = new Sim { Verified = false, FailRestore = true }; var activation = sim.Create();
        await Assert.ThrowsAsync<AggregateException>(() => activation.StartAsync(CancellationToken.None));
        Assert.True(sim.Relay && sim.Network && sim.Recovery);
        Assert.DoesNotContain("relay-stop", sim.Steps);
        Assert.DoesNotContain("recovery-clear", sim.Steps);
    }
}
