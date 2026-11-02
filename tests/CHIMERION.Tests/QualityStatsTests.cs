using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("language")] // тесты переключают язык программы: не параллельно
public class QualityStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0);

    private static CheckPoint P(int daysAgo, int hour, bool ok, long? ms = 40) =>
        new(Now.Date.AddDays(-daysAgo).AddHours(hour), ok, ok ? ms : null);

    [Fact]
    public void Empty_history_gives_a_neutral_message()
    {
        var s = QualityStats.Summarize(new List<CheckPoint>(), Now);
        Assert.Equal(0, s.Count);
        Assert.Null(s.Last);
    }

    [Fact]
    public void Counts_percent_median_and_ignores_points_older_than_the_period()
    {
        var points = new List<CheckPoint> { P(20, 10, false), P(3, 9, true, 30), P(2, 9, true, 50), P(1, 9, true, 70), P(1, 10, false) };
        var s = QualityStats.Summarize(points, Now);
        Assert.Equal(4, s.Count);
        Assert.Equal(3, s.OkCount);
        Assert.Equal(75, s.OkRate);
        Assert.Equal(50, s.MedianMs);
    }

    [Fact]
    public void Longest_and_current_failure_runs_are_separate()
    {
        var points = new List<CheckPoint> { P(5, 9, true), P(4, 9, false), P(4, 10, false), P(4, 11, false), P(3, 9, true), P(2, 9, false), P(1, 9, true) };
        Assert.Equal(3, QualityStats.Summarize(points, Now).MaxRun);
        Assert.Equal(0, QualityStats.Summarize(points, Now).CurrentRun);
    }

    [Fact]
    public void Evening_failures_are_named_when_clearly_worse_than_the_rest()
    {
        var points = new List<CheckPoint>();
        for (int d = 1; d <= 6; d++) { points.Add(P(d, 9, true)); points.Add(P(d, 14, true)); points.Add(P(d, 20, d % 2 == 0)); }
        var s = QualityStats.Summarize(points, Now);
        Assert.Equal(QualityStats.Parts[3], s.WorstPart);
        Assert.Equal(50, s.WorstPercent);
    }

    [Fact]
    public void Evenly_spread_failures_name_no_time_of_day()
    {
        var points = new List<CheckPoint>();
        for (int d = 1; d <= 6; d++) foreach (var h in new[] { 3, 9, 15, 21 }) points.Add(P(d, h, d != 3));
        Assert.Null(QualityStats.Summarize(points, Now).WorstPart);
    }

    [Fact]
    public void Buckets_align_to_the_clock_and_count_failures_per_interval()
    {
        var points = new List<CheckPoint>
        {
            new(new DateTime(2026, 10, 29, 19, 10, 0), true, 80),
            new(new DateTime(2026, 10, 29, 19, 40, 0), false, null),
            new(new DateTime(2026, 10, 29, 19, 50, 0), true, 60),
            new(new DateTime(2026, 10, 30, 7, 0, 0), true, 40),
        };
        var buckets = QualityStats.Buckets(points, Now, 6);
        Assert.Equal(new DateTime(2026, 10, 29, 18, 0, 0), buckets[0].Start);
        Assert.Equal(3, buckets[0].Count);
        Assert.Equal(1, buckets[0].Failed);
        Assert.Equal(33, buckets[0].FailPercent);
        Assert.Equal(60, buckets[0].MedianMs); // медиана по ближайшему рангу
        Assert.Equal(60, buckets[0].MinMs);
        Assert.Equal(new DateTime(2026, 10, 30, 0, 0, 0), buckets[1].Start);
        Assert.Equal(0, buckets[1].Count);
        Assert.Equal(new DateTime(2026, 10, 30, 6, 0, 0), buckets[^1].Start);
        Assert.Equal(1, buckets[^1].Count);
    }

    [Fact]
    public void Bucket_size_follows_the_period()
    {
        Assert.Equal(1, QualityStats.BucketHours(1));
        Assert.Equal(6, QualityStats.BucketHours(7));
        Assert.Equal(12, QualityStats.BucketHours(14));
    }

    private static List<CheckPoint> Series(int ok, int failed) =>
        Enumerable.Range(0, ok + failed).Select(i => new CheckPoint(Now.AddHours(-i - 1), i >= failed, i >= failed ? 40 : null)).ToList();

    [Theory]
    [InlineData(43, 2, 95.6)]
    [InlineData(42, 2, 95.5)]
    [InlineData(45, 0, 100)]
    [InlineData(4999, 1, 99.9)] // 99,98% не показывается как идеал
    public void Availability_is_rounded_by_one_rule(int ok, int failed, double expected)
    {
        var points = Enumerable.Range(0, ok + failed).Select(i => new CheckPoint(Now.AddSeconds(-i - 1), i >= failed, 40L)).ToList();
        Assert.Equal(expected, QualityStats.RoundRate(QualityStats.Summarize(points, Now)));
    }

    [Theory]
    [InlineData(100, "Ok")]
    [InlineData(99, "Ok")]
    [InlineData(95.6, "Warn")]
    [InlineData(94.9, "Bad")]
    public void Grade_depends_on_thresholds(double rate, string grade) => Assert.Equal(grade, QualityStats.Grade(rate));

    [Fact]
    public void Current_run_counts_failures_at_the_end()
    {
        var s = QualityStats.Summarize(Series(5, 2), Now);
        Assert.Equal(2, s.CurrentRun);
        Assert.Equal(2, s.MaxRun);
        Assert.Equal(2, s.Failed);
    }

    [Fact]
    public void Small_difference_from_median_is_not_called_a_spike()
    {
        var calm = Enumerable.Range(0, 20).Select(i => new CheckPoint(Now.AddHours(-i - 1), true, 70 + i % 4)).ToList();
        Assert.False(QualityStats.Summarize(calm, Now).HasSpikes);
        var spiky = Enumerable.Range(0, 20).Select(i => new CheckPoint(Now.AddHours(-i - 1), true, i < 3 ? 900L : 70L)).ToList();
        var s = QualityStats.Summarize(spiky, Now);
        Assert.True(s.HasSpikes);
        Assert.Equal(70, s.MedianMs);
        Assert.Equal(900, s.P95Ms);
    }

    [Fact]
    public void Line_breaks_where_a_bucket_has_no_measurements()
    {
        var points = new List<CheckPoint> { new(Now.AddHours(-11), true, 40), new(Now.AddHours(-10), true, 50), new(Now.AddHours(-3), false, null), new(Now.AddHours(-1), true, 60) };
        var buckets = QualityStats.Buckets(points, Now, 1, Now.AddHours(-12));
        var segments = QualityStats.Segments(buckets);
        Assert.Equal(2, segments.Count); // пустые часы и час только со сбоем не соединяются
        Assert.Equal(2, segments[0].Count);
        Assert.Single(segments[1]);
        Assert.Contains(buckets, b => b.Failed == 1 && b.MedianMs == null);
    }

    [Fact]
    public void Axis_starts_at_period_start_and_empty_buckets_are_kept()
    {
        var buckets = QualityStats.Buckets(new List<CheckPoint> { new(Now.AddHours(-2), true, 40) }, Now, 6, Now.AddDays(-7));
        Assert.Equal(Now.AddDays(-7).Date.AddHours(12), buckets[0].Start);
        Assert.True(buckets.Count(b => b.Count == 0) > 20);
    }

    [Theory]
    [InlineData(73, 100)]
    [InlineData(100, 100)]
    [InlineData(101, 200)]
    [InlineData(380, 500)]
    [InlineData(1500, 2000)]
    public void Scale_top_is_a_round_number(long value, long top) => Assert.Equal(top, QualityStats.NiceTop(value));

    [Fact]
    public void Report_text_includes_recent_errors_section()
    {
        var report = new DiagReport(new List<DiagItem>(), null, DateTime.Now);
        var text = Diagnostics.ToText(report, new[] { "строка журнала" }, new[] { "2026-10-30 ошибка проверки" });
        Assert.Contains("Ошибки программы", text);
        Assert.Contains("ошибка проверки", text);
        Assert.DoesNotContain("Ошибки программы", Diagnostics.ToText(report, new[] { "строка журнала" }));
    }
}

public class HistoryThreadSafetyTests
{
    [Fact]
    public async Task Background_check_and_ui_reading_do_not_collide()
    {
        var s = new DnsServer { Name = "Тест", Primary = "203.0.113.10" };
        var stop = DateTime.Now.AddMilliseconds(600);
        var writer = Task.Run(() => { int i = 0; while (DateTime.Now < stop) s.AddHistory(i++ % 3 != 0, 40); });
        var reader = Task.Run(() =>
        {
            while (DateTime.Now < stop)
            {
                System.Text.Json.JsonSerializer.Serialize(new List<DnsServer> { s }, Dnex.Services.Storage.Options);
                QualityStats.Summarize(s.History, DateTime.Now);
            }
        });
        await Task.WhenAll(writer, reader); // раньше: InvalidOperationException «Collection was modified»
        Assert.InRange(s.HistoryCount, 1, DnsServer.MaxHistory);
    }

    [Fact]
    public void Trim_keeps_the_first_points_and_copy_is_detached()
    {
        var s = new DnsServer();
        for (int i = 0; i < 5; i++) s.AddHistory(true, i, DateTime.Now.AddMinutes(i));
        var copy = s.History;
        copy.Clear();
        Assert.Equal(5, s.HistoryCount);
        s.TrimHistory(3);
        Assert.Equal(new long?[] { 0, 1, 2 }, s.History.Select(p => p.Ms));
    }
}
