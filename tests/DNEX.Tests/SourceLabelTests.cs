using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>Источник сервера в читаемом виде: сайт и путь «хлебными крошками».</summary>
public class SourceLabelTests
{
    [Theory]
    [InlineData("https://github.com/MAECTRO/magicdns/commits/main/", "github.com › MAECTRO › magicdns › commits › main")]
    [InlineData("https://t.me/s/dns_club", "Telegram › @dns_club")]
    [InlineData("https://raw.githubusercontent.com/user/repo/main/lists/dns.txt", "github.com › user › repo › dns.txt")]
    [InlineData("https://4pda.to/forum/index.php?showtopic=1117490", "4pda.to › тема 1117490")]
    [InlineData("https://4pda.to/forum/index.php?showtopic=1117490&view=findpost&p=129800546", "4pda.to › тема 1117490 › сообщение 129800546")]
    [InlineData("https://www.comss.ru/page.php?id=16095", "comss.ru › №16095")]
    [InlineData("https://habr.com/ru/articles/982070/", "habr.com › ru › articles › 982070")]
    public void ReadablePath(string url, string expected) => Assert.Equal(expected, SourceLabel.Pretty(url, "-"));

    [Fact]
    public void LongPath_IsShortened_KeepingSiteStartAndEnd()
    {
        var p = SourceLabel.Pretty("https://example.com/aaaa/bbbbbbbbbb/cccccccccc/dddddddddd/eeeeeeeeee/final-page", "-", max: 45);
        Assert.StartsWith("example.com › aaaa › …", p);
        Assert.EndsWith("final-page", p);
        Assert.True(p.Length <= 60);
    }

    [Theory]
    [InlineData("https://github.com/MAECTRO/magicdns/commits/main/", "github.com › main")]
    [InlineData("https://4pda.to/forum/index.php?showtopic=1117490&view=findpost&p=129800546", "4pda.to › тема 1117490")]
    [InlineData("https://t.me/s/dns_club", "Telegram › @dns_club")]
    [InlineData("https://example.com/", "example.com")]
    public void CompactForColumn(string url, string expected) => Assert.Equal(expected, SourceLabel.Compact(url, "-"));

    [Fact]
    public void NoLink_UsesFallback() => Assert.Equal("вручную", SourceLabel.Pretty(null, "вручную"));
}
