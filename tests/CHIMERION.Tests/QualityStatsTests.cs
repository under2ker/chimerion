using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

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
        Assert.Contains("мало проверок", s.Text);
    }

    [Fact]
    public void Counts_percent_median_and_ignores_points_older_than_the_period()
    {
        var points = new List<CheckPoint> { P(20, 10, false), P(3, 9, true, 30), P(2, 9, true, 50), P(1, 9, true, 70), P(1, 10, false) };
        var s = QualityStats.Summarize(points, Now);
        Assert.Equal(4, s.Count);
        Assert.Equal(3, s.OkCount);
        Assert.Equal(75, s.OkPercent);
        Assert.Equal(50, s.MedianMs);
    }

    [Fact]
    public void Two_failures_in_a_row_are_one_outage()
    {
        var points = new List<CheckPoint> { P(5, 9, true), P(4, 9, false), P(4, 10, false), P(4, 11, false), P(3, 9, true), P(2, 9, false), P(1, 9, true) };
        Assert.Equal(1, QualityStats.Summarize(points, Now).Outages);
    }

    [Fact]
    public void Evening_failures_are_named_when_clearly_worse_than_the_rest()
    {
        var points = new List<CheckPoint>();
        for (int d = 1; d <= 6; d++) { points.Add(P(d, 9, true)); points.Add(P(d, 14, true)); points.Add(P(d, 20, d % 2 == 0)); }
        var s = QualityStats.Summarize(points, Now);
        Assert.Equal(QualityStats.Parts[3], s.WorstPart);
        Assert.Equal(50, s.WorstPercent);
        Assert.Contains("вечером", s.Text);
    }

    [Fact]
    public void Evenly_spread_failures_name_no_time_of_day()
    {
        var points = new List<CheckPoint>();
        for (int d = 1; d <= 6; d++) foreach (var h in new[] { 3, 9, 15, 21 }) points.Add(P(d, h, d != 3));
        Assert.Null(QualityStats.Summarize(points, Now).WorstPart);
    }

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
