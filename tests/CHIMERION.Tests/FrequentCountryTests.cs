using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Частая смена страны: недавняя история и что считается «частой».</summary>
[Collection("scenarios")]
public class FrequentCountryTests : IDisposable
{
    private DateTime _today = new(2026, 10, 28);
    public FrequentCountryTests() { AccountSafety.Today = () => _today; AccountSafety.Pinned = null; }
    public void Dispose() { AccountSafety.Today = () => DateTime.Today; AccountSafety.Pinned = null; }

    [Fact]
    public void One_Country_Is_Not_Frequent()
    {
        var s = new AccountSafety();
        for (int i = 10; i >= 0; i--) { _today = new DateTime(2026, 10, 28).AddDays(-i); s.Record("Claude", "FR"); }
        Assert.Equal(new[] { "FR" }, s.Recent("Claude"));
        Assert.False(s.Frequent(new[] { "Claude" }));
    }

    [Fact]
    public void France_Lithuania_France_Within_Two_Weeks_Is_Frequent()
    {
        var s = new AccountSafety();
        _today = new DateTime(2026, 10, 20); s.Record("Claude", "FR");
        _today = new DateTime(2026, 10, 23); s.Record("Claude", "LT");
        _today = new DateTime(2026, 10, 26); s.Record("Claude", "FR");
        _today = new DateTime(2026, 10, 28);
        Assert.Equal(new[] { "FR", "LT" }, s.Recent("Claude"));
        Assert.True(s.Frequent(new[] { "Claude" }));
        Assert.False(s.Frequent(new[] { "ChatGPT" })); // по другому сервису истории нет
    }

    [Fact]
    public void Old_Change_Outside_Window_Does_Not_Count()
    {
        var s = new AccountSafety();
        _today = new DateTime(2026, 10, 1); s.Record("Claude", "LT");
        _today = new DateTime(2026, 10, 25); s.Record("Claude", "FR");
        _today = new DateTime(2026, 10, 28);
        Assert.Equal(new[] { "FR" }, s.Recent("Claude"));
        Assert.False(s.Frequent(new[] { "Claude" }));
    }
}
