using System.Reflection;
using System.Runtime.CompilerServices;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Первый запуск: когда показывать приветствие, что в нем предлагается и что попадает в настройки.</summary>
public class FirstRunTests
{
    [Theory]
    [InlineData(true, false, false, false, true)]   // самый первый запуск
    [InlineData(false, false, false, false, false)] // обновившийся пользователь: файл настроек уже был
    [InlineData(true, true, false, false, false)]   // уже показывали
    [InlineData(true, false, true, false, false)]   // тестовая копия данных
    [InlineData(false, true, true, true, true)]     // команда палитры и DNEX_FIRST_RUN=1 открывают всегда
    public void Welcome_is_shown_only_on_the_very_first_start(bool fresh, bool done, bool testCopy, bool forced, bool expected) =>
        Assert.Equal(expected, FirstRun.ShouldShow(fresh, done, testCopy, forced));

    [Fact]
    public void Options_have_unique_keys_and_texts()
    {
        Assert.Equal(FirstRun.Options.Count, FirstRun.Options.Select(o => o.Key).Distinct().Count());
        Assert.All(FirstRun.Options, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Title));
            Assert.False(string.IsNullOrWhiteSpace(o.Sub));
        });
    }

    [Fact]
    public void Options_point_to_the_right_modes_of_the_main_window()
    {
        var modes = (Array)typeof(MainWindow).GetField("Modes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string ModeName(int id) => ((ITuple)modes.GetValue(id)!)[0]!.ToString()!;
        Assert.Equal("AiOnly", ModeName(FirstRun.Options.Single(o => o.Key == "ai").ModeId!.Value));
        Assert.Equal("All", ModeName(FirstRun.Options.Single(o => o.Key == "all").ModeId!.Value));
        Assert.Equal("Games", ModeName(FirstRun.Options.Single(o => o.Key == "games").ModeId!.Value));
        Assert.Null(FirstRun.Options.Single(o => o.Key == "none").ModeId); // «просто посмотреть» ничего не включает
    }

    [Fact]
    public void Recommended_choice_is_AI_only()
    {
        Assert.Equal("ai", FirstRun.Default.Key);
    }

    [Fact]
    public void Apply_moves_choice_to_settings_and_marks_welcome_done()
    {
        var s = new AppSettings { CloseToTray = true, GlobalHotkeys = false };
        FirstRun.Apply(s, new FirstRunChoice(0, AutoStart: true, CloseToTray: false, Hotkeys: true));
        Assert.False(s.CloseToTray);
        Assert.True(s.GlobalHotkeys);
        Assert.True(s.FirstRunDone);
    }

    [Fact]
    public void New_settings_start_with_welcome_not_done()
    {
        Assert.False(new AppSettings().FirstRunDone);
    }
}
