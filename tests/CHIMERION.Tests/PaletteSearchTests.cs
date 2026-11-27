using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Поиск в палитре Ctrl+K: порядок, ключевые слова, ё, английская раскладка.</summary>
// Общий набор с LocTests и другими тестами языка: они на время переключают язык программы на английский, а поиск от языка зависит
[Collection("language")]
public class PaletteSearchTests
{
    private static PaletteCommand C(string title, string keywords = "", string group = "Действия") =>
        new(title, group, "zap", keywords, () => Task.CompletedTask);

    private static readonly IReadOnlyList<PaletteCommand> All = new[]
    {
        C("Включить защиту", "smart dns vpn"),
        C("Режим: Игры", "обход zapret"),
        C("Включить Два DNS", "split общий днс"),
        C("Тест скорости", "speed интернет"),
        C("Настройки", "параметры"),
        C("Тема: светлая", "оформление ёлка"),
    };

    [Fact]
    public void Empty_query_keeps_the_given_order()
    {
        var r = PaletteSearch.Rank("", All);
        Assert.Equal(All.Select(c => c.Title), r.Select(c => c.Title));
    }

    [Fact]
    public void Title_start_beats_title_middle_and_keywords()
    {
        var r = PaletteSearch.Rank("вкл", All);
        Assert.Equal("Включить защиту", r[0].Title); // начало названия, раньше по списку
        Assert.Equal("Включить Два DNS", r[1].Title);
    }

    [Fact]
    public void Every_word_of_the_query_must_match()
    {
        var r = PaletteSearch.Rank("два днс", All);
        Assert.Single(r);
        Assert.Equal("Включить Два DNS", r[0].Title);
    }

    [Fact]
    public void Keywords_are_searched_when_title_has_no_match()
    {
        Assert.Equal("Режим: Игры", PaletteSearch.Rank("zapret", All).Single().Title);
        Assert.Equal("Настройки", PaletteSearch.Rank("параметры", All).Single().Title);
    }

    [Fact]
    public void Letters_yo_and_ye_are_the_same()
    {
        Assert.Equal("Тема: светлая", PaletteSearch.Rank("елка", All).Single().Title);
    }

    [Fact]
    public void Query_typed_in_english_layout_finds_russian_title()
    {
        // «два днс» на английской раскладке: ldf lyc
        Assert.Equal("Включить Два DNS", PaletteSearch.Rank("ldf lyc", All).Single().Title);
        Assert.Equal("Тест скорости", PaletteSearch.Rank("ntcn", All).Single().Title);
    }

    [Fact]
    public void Nothing_found_gives_empty_list()
    {
        Assert.Empty(PaletteSearch.Rank("жзщшгх", All));
    }

    [Theory]
    [InlineData("«Два»  DNS: ЁЖ", "два dns еж")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_makes_plain_lowercase_words(string? input, string expected) =>
        Assert.Equal(expected, PaletteSearch.Normalize(input));

    [Fact]
    public void Swap_layout_maps_both_ways()
    {
        Assert.Equal("два днс", PaletteSearch.SwapLayout("ldf lyc"));
        Assert.Equal("dns", PaletteSearch.SwapLayout("вты"));
    }
}
