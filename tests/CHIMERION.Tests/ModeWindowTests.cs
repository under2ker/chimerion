using Xunit;
using T = Dnex.ModeWindow.Target;

namespace Dnex.Tests;

/// <summary>Окно «Режим» отмечает текущий режим по фактическому состоянию DNS, Telegram и zapret.</summary>
public class ModeWindowTests
{
    private static readonly ModeWindow.ModeInfo[] Modes =
    {
        new(0, "Только ИИ", "", "shield-check", true, T.On, T.Off, T.Off),
        new(1, "Все сразу", "", "zap", false, T.On, T.On, T.On),
        new(2, "Игры", "", "activity", false, T.Keep, T.Keep, T.Games),
        new(3, "Все выключить", "", "x", false, T.Off, T.Off, T.Off),
    };

    private static ModeWindow.State S(bool dns, bool tg, bool z, bool games = false) => new(dns, dns ? "Тест" : null, tg, false, z, games, true);

    [Theory]
    [InlineData(true, false, false, false, 0)]
    [InlineData(true, true, true, false, 1)]
    [InlineData(false, false, true, true, 2)]
    [InlineData(true, true, true, true, 2)]
    [InlineData(false, false, false, false, 3)]
    public void RecognizesCurrentMode(bool dns, bool tg, bool z, bool games, int expected) =>
        Assert.Equal(expected, ModeWindow.Current(Modes, S(dns, tg, z, games)));

    [Fact]
    public void MixedStateIsOwnSet() => Assert.Null(ModeWindow.Current(Modes, S(true, true, false)));
}
