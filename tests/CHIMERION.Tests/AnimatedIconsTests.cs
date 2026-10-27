using Dnex.Controls;
using Xunit;

namespace Dnex.Tests;

public class AnimatedIconsTests
{
    [Theory]
    [InlineData("settings")] [InlineData("zap")] [InlineData("refresh-cw")] [InlineData("circle-check")]
    [InlineData("download")] [InlineData("sliders-horizontal")] [InlineData("circle-help")] [InlineData("x")]
    public void Animation_Loads_From_Resource(string kind)
    {
        var a = AnimatedIcons.Get(kind);
        Assert.NotNull(a);
        Assert.True(a!.Frames.Length > 5);
        Assert.True(a.Fps > 0);
        Assert.InRange(a.RestFrame, 0, a.Frames.Length - 1);
        Assert.All(a.Frames, f => Assert.NotEmpty(f));
    }

    [Fact]
    public void Unknown_Kind_Has_No_Animation()
    {
        Assert.Null(AnimatedIcons.Get("star"));
        Assert.Null(AnimatedIcons.Get(""));
    }
}
