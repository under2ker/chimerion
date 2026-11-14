using System.Windows;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Сжатие памяти при скрытом окне: когда сжимать и что вызов безопасен.</summary>
public class MemoryTrimmerTests
{
    [Theory]
    [InlineData(true, WindowState.Normal, false)]      // окно на виду: не трогаем
    [InlineData(true, WindowState.Maximized, false)]
    [InlineData(true, WindowState.Minimized, true)]    // свернуто в панель задач
    [InlineData(false, WindowState.Normal, true)]      // скрыто в трей
    [InlineData(false, WindowState.Minimized, true)]
    public void Trims_only_when_window_is_not_seen(bool visible, WindowState state, bool expected) =>
        Assert.Equal(expected, MemoryTrimmer.ShouldTrim(visible, state));

    [Fact]
    public void Trim_now_is_safe_to_call_repeatedly()
    {
        Assert.True(MemoryTrimmer.TrimNow());
        Assert.True(MemoryTrimmer.TrimNow());
    }

    [Fact]
    public void Repeat_period_is_longer_than_first_delay() =>
        Assert.True(MemoryTrimmer.WhileHidden > MemoryTrimmer.AfterHide);
}
