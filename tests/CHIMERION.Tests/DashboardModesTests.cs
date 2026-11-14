using System.Reflection;

namespace Dnex.Tests;

/// <summary>Режимы на Главной: плиток и подписей столько же, сколько режимов.</summary>
public class DashboardModesTests
{
    private static T Field<T>(string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void Every_mode_has_a_short_caption_for_its_tile()
    {
        var modes = Field<Array>("Modes");
        var captions = Field<string[]>("ModeShort");
        Assert.Equal(modes.Length, captions.Length);
        Assert.All(captions, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }
}
