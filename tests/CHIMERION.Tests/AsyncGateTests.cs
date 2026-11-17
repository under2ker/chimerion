using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Запуск winws.exe по одному: два быстрых нажатия «В zapret» не должны запускать две копии.</summary>
public class AsyncGateTests
{
    [Fact]
    public async Task OperationsNeverOverlap()
    {
        var gate = new AsyncGate();
        int running = 0, maxRunning = 0;
        async Task Op()
        {
            int now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gate.RunAsync(Op)));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task NestedCallFromSameFlowDoesNotDeadlock()
    {
        var gate = new AsyncGate();
        var order = new List<string>();
        var task = gate.RunAsync(async () =>
        {
            order.Add("stop");
            await gate.RunAsync(async () => { await Task.Delay(5); order.Add("inner"); });
            order.Add("start");
        });
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))));
        await task;
        Assert.Equal(new[] { "stop", "inner", "start" }, order);
    }

    [Fact]
    public async Task ReturnsValueAndReleasesAfterFailure()
    {
        var gate = new AsyncGate();
        Assert.Equal(42, await gate.RunAsync(async () => { await Task.Yield(); return 42; }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(() => Task.FromException(new InvalidOperationException())));
        // после ошибки блокировка свободна
        Assert.Equal(7, await gate.RunAsync(() => Task.FromResult(7)));
    }
}
