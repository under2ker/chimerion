using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class PowerShellQuoteTests
{
    [Theory]
    [InlineData("обычное имя")]
    [InlineData("O'Brien")]
    [InlineData("Тест’; Write-Output ВНЕДРЕНО; ’")]
    [InlineData("‘левая’ ‚нижняя‛ кавычки")]
    [InlineData("$env:TEMP `n \"двойные\" ; & | ( )")]
    public async Task Quoted_text_reaches_powershell_unchanged(string value)
    {
        var (code, output) = await DnsSwitcher.RunPsAsync("Write-Output " + DnsSwitcher.PsQuote(value));
        Assert.Equal(0, code);
        Assert.Equal(value, output.Trim());
    }

    [Fact]
    public void Every_single_quote_variant_is_doubled()
    {
        Assert.Equal("a''b‘‘c’’d‚‚e‛‛f", DnsSwitcher.PsEscape("a'b‘c’d‚e‛f"));
    }
}
