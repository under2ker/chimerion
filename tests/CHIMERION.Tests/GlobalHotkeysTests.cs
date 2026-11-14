using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Глобальные сочетания клавиш: описания не расходятся с кодами клавиш, повторов нет, по умолчанию выключены.</summary>
public class GlobalHotkeysTests
{
    [Fact]
    public void Ids_and_key_combinations_are_unique()
    {
        Assert.Equal(GlobalHotkeys.All.Count, GlobalHotkeys.All.Select(d => d.Id).Distinct().Count());
        Assert.Equal(GlobalHotkeys.All.Count, GlobalHotkeys.All.Select(d => (d.Modifiers, d.Key)).Distinct().Count());
    }

    [Fact]
    public void Shown_text_matches_modifiers_and_letter()
    {
        foreach (var d in GlobalHotkeys.All)
        {
            var mods = (d.Modifiers & 2) != 0 ? "Ctrl+" : "";
            mods += (d.Modifiers & 1) != 0 ? "Alt+" : "";
            mods += (d.Modifiers & 4) != 0 ? "Shift+" : "";
            Assert.Equal(mods + (char)d.Key, d.Text);
        }
    }

    [Fact]
    public void Protection_and_palette_are_both_offered()
    {
        Assert.Contains(GlobalHotkeys.All, d => d.Id == GlobalHotkeys.ProtectionId && d.Text == "Ctrl+Alt+D");
        Assert.Contains(GlobalHotkeys.All, d => d.Id == GlobalHotkeys.PaletteId && d.Text == "Ctrl+Alt+K");
    }

    [Fact]
    public void Hotkeys_are_off_until_the_person_turns_them_on()
    {
        Assert.False(new AppSettings().GlobalHotkeys);
    }
}
