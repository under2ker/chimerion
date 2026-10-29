using System.Reflection;
using Xunit;

namespace Dnex.Tests;

/// <summary>Подписи главной: время работы коротко и без лишних нулей.</summary>
public class DashboardTextTests
{
    private static string Uptime(TimeSpan t) =>
        (string)typeof(MainWindow).GetMethod("Uptime", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { t })!;

    [Theory]
    [InlineData(0, 0, 0, "1 мин")]
    [InlineData(0, 0, 42, "42 мин")]
    [InlineData(0, 2, 14, "2 ч 14 мин")]
    [InlineData(3, 5, 0, "3 д 5 ч")]
    public void UptimeIsShort(int days, int hours, int minutes, string expected) =>
        Assert.Equal(expected, Uptime(new TimeSpan(days, hours, minutes, 0)));

    [Fact]
    public void UpdateCheckingIsVisibleLongEnough() =>
        Assert.InRange(MainWindow.UpdateCheckMinShow.TotalMilliseconds, 500, 1500);
}
