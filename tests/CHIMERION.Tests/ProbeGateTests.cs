using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Общий ограничитель проверок: одна проверка на всех, повтор готового результата, сброс при смене сети.</summary>
public class ProbeGateTests
{
    private sealed class Box { public int N; }

    private DateTime _now = new(2026, 11, 10, 12, 0, 0);
    private ProbeGate<Box> Gate() => new(TimeSpan.FromSeconds(20), () => _now);

    [Fact]
    public async Task Concurrent_callers_share_one_probe()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        int runs = 0;
        Func<Task<Box>> run = async () => { Interlocked.Increment(ref runs); await release.Task; return new Box { N = runs }; };

        var a = gate.RunAsync(ProbeLevel.Full, run);
        var b = gate.RunAsync(ProbeLevel.Full, run);
        var c = gate.RunAsync(ProbeLevel.Light, run); // облегченной хватает и полной
        release.SetResult();
        var results = await Task.WhenAll(a, b, c);

        Assert.Equal(1, runs);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], results[2]);
        Assert.Equal((1, 2), (gate.Started, gate.Joined));
    }

    [Fact]
    public async Task Fresh_result_is_reused_then_expires()
    {
        var gate = Gate();
        int runs = 0;
        Func<Task<Box>> run = () => Task.FromResult(new Box { N = ++runs });

        var first = await gate.RunAsync(ProbeLevel.Full, run);
        _now = _now.AddSeconds(10);
        Assert.Same(first, await gate.RunAsync(ProbeLevel.Light, run));
        Assert.Equal(1, gate.Reused);

        _now = _now.AddSeconds(15); // 25 с после проверки: готовый результат устарел
        Assert.NotSame(first, await gate.RunAsync(ProbeLevel.Full, run));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Lighter_result_does_not_satisfy_fuller_request()
    {
        var gate = Gate();
        int runs = 0;
        Func<Task<Box>> run = () => Task.FromResult(new Box { N = ++runs });

        await gate.RunAsync(ProbeLevel.Light, run);
        await gate.RunAsync(ProbeLevel.Full, run);   // облегченная страну не мерила: нужна новая
        await gate.RunAsync(ProbeLevel.Leak, run);   // и тест утечки только в своей проверке
        Assert.Equal(3, runs);
        await gate.RunAsync(ProbeLevel.Full, run);   // а с тестом утечки годится и для полной
        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task Invalidate_drops_ready_and_running_results()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        int runs = 0;
        Func<Task<Box>> slow = async () => { Interlocked.Increment(ref runs); await release.Task; return new Box(); };

        var before = gate.RunAsync(ProbeLevel.Full, slow);
        gate.Invalidate(); // сменилась сеть, пока шла проверка
        var after = gate.RunAsync(ProbeLevel.Full, slow);
        release.SetResult();
        Assert.NotSame(await before, await after);
        Assert.Equal(2, runs);

        gate.Invalidate();
        await gate.RunAsync(ProbeLevel.Full, () => Task.FromResult(new Box()));
        Assert.Equal(3, gate.Started);
    }

    [Fact]
    public async Task Failure_is_shared_and_not_cached()
    {
        var gate = Gate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(ProbeLevel.Full, () => throw new InvalidOperationException()));
        var ok = await gate.RunAsync(ProbeLevel.Full, () => Task.FromResult(new Box { N = 7 }));
        Assert.Equal(7, ok.N);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_shared_probe()
    {
        var gate = Gate();
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var waiting = gate.RunAsync(ProbeLevel.Full, async () => { await release.Task; return new Box { N = 5 }; }, cts.Token);
        var other = gate.RunAsync(ProbeLevel.Full, () => Task.FromResult(new Box()));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        Assert.Equal(5, (await other).N);
    }

    [Theory]
    [InlineData(15, 0, 15)]
    [InlineData(15, 1, 15)]
    [InlineData(15, 2, 30)]
    [InlineData(15, 5, 30)]
    [InlineData(5, 2, 10)]
    [InlineData(5, 3, 30)]
    [InlineData(60, 4, 60)]
    public void Backoff_grows_after_repeated_failures(int normal, int streak, int expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), ProbeGate<Box>.Backoff(TimeSpan.FromMinutes(normal), streak));
}
