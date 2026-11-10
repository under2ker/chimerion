using Xunit;

namespace Dnex.Tests;

/// <summary>Тексты окна «Проверка браузера»: что ожидать на странице и как читать результат.</summary>
public class BrowserCheckTests
{
    [Fact]
    public void Expected_country_is_named_with_its_loc_code()
    {
        var text = BrowserCheckWindow.ExpectedText(true, "NL");
        Assert.Contains("loc=NL", text);
        Assert.Contains("loc=NL: этот браузер идет через CHIMERION", BrowserCheckWindow.VerdictText(true, "NL"));
    }

    [Fact]
    public void Without_measurement_or_connection_no_country_is_promised()
    {
        Assert.Contains("еще не измерила", BrowserCheckWindow.ExpectedText(true, null));
        Assert.Contains("не подключен", BrowserCheckWindow.ExpectedText(false, "NL"));
        Assert.DoesNotContain("NL", BrowserCheckWindow.VerdictText(false, "NL"));
        Assert.DoesNotContain("через CHIMERION", BrowserCheckWindow.VerdictText(true, null));
    }

    [Fact]
    public void Trace_page_is_on_claude_site() =>
        Assert.Equal("https://claude.ai/cdn-cgi/trace", BrowserCheckWindow.TraceUrl);
}
