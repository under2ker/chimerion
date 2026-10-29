using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Поиск серверов: ленты Telegram, выдача Brave, README репозиториев, шумные сайты.</summary>
public class SearchImprovementsTests
{
    [Theory]
    [InlineData("https://t.me/dnsclub", "https://t.me/s/dnsclub")]
    [InlineData("https://t.me/smartdns_ru/154", "https://t.me/s/smartdns_ru?before=155")]
    [InlineData("https://t.me/s/dnsclub", "https://t.me/s/dnsclub")]
    public void Telegram_Channel_Links_Open_Public_Feed(string from, string to) =>
        Assert.Equal(to, SourceParser.TelegramToFeed(new Uri(from)).AbsoluteUri);

    [Theory]
    [InlineData("https://t.me/joinchat/AAAA")]
    [InlineData("https://t.me/+rXN4fR7HWcxlMTYy")]
    [InlineData("https://t.me/CHIMERIONDNS_BOT")]
    [InlineData("https://t.me/share/url?url=x")]
    public void Bots_Invites_And_Shares_Stay_As_Is(string url) =>
        Assert.Equal(new Uri(url).AbsoluteUri, SourceParser.TelegramToFeed(new Uri(url)).AbsoluteUri);

    [Theory]
    [InlineData("https://t.me/s/dnsclub", "https://chimerion-crowd.chimerion.workers.dev/v1/tg?ch=dnsclub")]
    [InlineData("https://t.me/s/dnsclub?before=120", "https://chimerion-crowd.chimerion.workers.dev/v1/tg?ch=dnsclub&before=120")]
    public void Telegram_Feed_Goes_Through_Relay(string from, string to) =>
        Assert.Equal(to, SourceParser.RelayUrl(new Uri(from))!.AbsoluteUri);

    [Theory]
    [InlineData("https://t.me/dnsclub")]
    [InlineData("https://example.com/s/dnsclub")]
    [InlineData("https://t.me/s/dnsclub/123")]
    public void Only_Channel_Feeds_Use_Relay(string url) => Assert.Null(SourceParser.RelayUrl(new Uri(url)));

    [Fact]
    public void Brave_Results_Skip_Brave_Own_Links()
    {
        const string html = """<a href="https://search.brave.com/settings">x</a><a href="https://dns.example-smart.ru/">y</a><a href="https://imgs.search.brave.com/a.png">z</a><a href="https://habr.com/ru/articles/1/?utm_source=brave">w</a>""";
        var urls = SourceParser.ParseBrave(html).Select(u => u.Host).ToList();
        Assert.Equal(new[] { "dns.example-smart.ru", "habr.com" }, urls);
    }

    [Fact]
    public void Repository_Readme_Is_Taken_Directly()
    {
        Assert.Equal("https://raw.githubusercontent.com/Kam300/KodVsego-SmartDNS/HEAD/README.md",
            SourceParser.ReadmeOf(new Uri("https://github.com/Kam300/KodVsego-SmartDNS"))!.AbsoluteUri);
        Assert.Null(SourceParser.ReadmeOf(new Uri("https://github.com/Kam300/KodVsego-SmartDNS/issues")));
    }

    [Fact]
    public void Documentation_Sites_Are_Blocked_By_Default()
    {
        var st = new Dnex.Models.AppSettings();
        Assert.Contains("code.claude.com", st.BlockedHosts);
        Assert.Contains("yogadns.com", st.BlockedHosts);
        var old = new Dnex.Models.AppSettings { BlockedHosts = { } };
        old.BlockedHosts.Clear();
        Catalog.Merge(old); // у тех, кто обновился со старой версии, шумные сайты добавятся сами
        Assert.Contains("developers.openai.com", old.BlockedHosts);
    }
}
