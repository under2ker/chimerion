using System.Net;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Скорость DNS: медиана, медленные ответы, поток запросов, оценка словами.</summary>
public class SpeedTestTests
{
    private static Task<(List<IPAddress> Ips, long Ms)> Answer(long ms) =>
        Task.FromResult((new List<IPAddress> { IPAddress.Parse("203.0.113.5") }, ms));

    [Fact]
    public async Task Fast_server_is_graded_fast_with_median()
    {
        var r = await SpeedTest.MeasureAsync((_, _) => Answer(25), CancellationToken.None);
        Assert.Equal(SpeedTest.Hosts.Length, r.Ok);
        Assert.Equal(25, r.MedianMs);
        Assert.Equal("быстрый", r.Grade);
        Assert.True(r.QueriesPerSecond > 0);
    }

    [Theory]
    [InlineData(40, "быстрый")]
    [InlineData(120, "обычный")]
    [InlineData(450, "медленный")]
    public async Task Grade_follows_median(long ms, string grade)
    {
        var r = await SpeedTest.MeasureAsync((_, _) => Answer(ms), CancellationToken.None);
        Assert.Equal(grade, r.Grade);
    }

    [Fact]
    public async Task Unanswered_queries_make_the_server_unstable()
    {
        int n = 0;
        var r = await SpeedTest.MeasureAsync((_, _) =>
        {
            if (Interlocked.Increment(ref n) % 2 == 0) throw new TimeoutException();
            return Answer(30);
        }, CancellationToken.None);
        Assert.Equal(SpeedTest.Hosts.Length / 2, r.Ok);
        Assert.Equal("нестабильный", r.Grade);
        Assert.Contains($"ответов {r.Ok} из {r.Total}", r.Summary);
    }

    [Fact]
    public async Task Dead_server_has_no_answers_and_says_so()
    {
        var r = await SpeedTest.MeasureAsync((_, _) => throw new TimeoutException(), CancellationToken.None);
        Assert.Equal(0, r.Ok);
        Assert.Equal("нет ответов", r.Grade);
        Assert.Contains("ни один запрос", r.Summary);
    }

    [Fact]
    public async Task Slow_tail_is_reported_separately_from_median()
    {
        int n = 0;
        var r = await SpeedTest.MeasureAsync((_, _) => Answer(Interlocked.Increment(ref n) <= 2 ? 900 : 30), CancellationToken.None);
        Assert.Equal(30, r.MedianMs);
        Assert.Equal(900, r.SlowMs);
    }

    [Fact]
    public async Task Queries_run_in_limited_parallel_batches()
    {
        int running = 0, peak = 0;
        await SpeedTest.MeasureAsync(async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this) peak = Math.Max(peak, now);
            await Task.Delay(15);
            Interlocked.Decrement(ref running);
            return (new List<IPAddress> { IPAddress.Loopback }, 15L);
        }, CancellationToken.None);
        Assert.InRange(peak, 2, SpeedTest.Concurrency);
    }

    [Fact]
    public async Task Cancellation_stops_the_measurement()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpeedTest.MeasureAsync((_, _) => Answer(10), cts.Token));
    }

    [Fact]
    public void Speed_does_not_use_ai_service_names()
        => Assert.DoesNotContain(SpeedTest.Hosts, h => h.Contains("openai") || h.Contains("anthropic") || h.Contains("claude") || h.Contains("gemini"));
}
