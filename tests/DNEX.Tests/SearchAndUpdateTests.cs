using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>Новые правила поиска серверов и запасная проверка обновления через ленту релизов.</summary>
public class SearchAndUpdateTests
{
    [Fact]
    public void PrivateDnsLine_HostWithoutDnsPrefix_IsFound()
    {
        var text = "Для Android откройте «Частный DNS» и впишите имя хоста: eu.geohide.ru\nДругое: ничего";
        var list = SourceParser.ParseText(text, "тест", "https://example.org/page", isHtml: false);
        Assert.Contains(list, s => s.DohUrl == "https://eu.geohide.ru/dns-query");
    }

    [Fact]
    public void TlsRecord_IsFound()
    {
        var list = SourceParser.ParseText("Сервер: tls://dns.keyoneday.ru", "тест", "https://example.org/", isHtml: false);
        Assert.Contains(list, s => s.DohUrl == "https://dns.keyoneday.ru/dns-query");
    }

    [Fact]
    public void PrivateDnsLine_SkipsKnownNonDnsSites()
    {
        var list = SourceParser.ParseText("Частный DNS: инструкция на github.com и t.me", "тест", "https://example.org/", isHtml: false);
        Assert.DoesNotContain(list, s => s.DohUrl?.Contains("github.com") == true || s.DohUrl?.Contains("t.me") == true);
    }

    [Fact]
    public void BingRedirect_IsUnwrapped()
    {
        var target = "https://dns.comss.one/";
        var code = "a1" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(target)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var real = SourceParser.UnwrapBing(new Uri("https://www.bing.com/ck/a?!&&p=abc&ptn=3&u=" + code + "&ntb=1"));
        Assert.Equal(new Uri(target), real);
        Assert.Equal(new Uri("https://example.org/x"), SourceParser.UnwrapBing(new Uri("https://example.org/x")));
    }

    [Fact]
    public void CloudflareTrace_GivesEgressIpAndCountry()
    {
        var e = EgressTrace.Parse("fl=123\nh=chatgpt.com\nip=104.28.196.54\nts=1\ncolo=FRA\nloc=FR\nwarp=off\n");
        Assert.NotNull(e);
        Assert.Equal("104.28.196.54", e!.Ip);
        Assert.Equal("FR", e.Country);
        Assert.Null(EgressTrace.Parse("<html>blocked</html>"));
        Assert.Null(EgressTrace.Parse("ip=1.2.3.4\nloc=XX"));
    }

    private const string Atom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>DNEX 0.1.21</title>
            <link rel="alternate" type="text/html" href="https://github.com/under2ker/dnex-dns-hub/releases/tag/v0.1.21"/>
            <content type="html">&lt;p&gt;Адреса сервисов в панели.&lt;/p&gt;&lt;ul&gt;&lt;li&gt;Первое&lt;/li&gt;&lt;/ul&gt;</content>
          </entry>
          <entry>
            <title>DNEX 0.1.20</title>
            <link rel="alternate" type="text/html" href="https://github.com/under2ker/dnex-dns-hub/releases/tag/v0.1.20"/>
            <content type="html">старая</content>
          </entry>
        </feed>
        """;

    [Fact]
    public void Atom_NewerVersion_IsAvailableWithNotes()
    {
        var r = UpdateChecker.ParseAtom(Atom, new Version(0, 1, 19));
        Assert.NotNull(r);
        Assert.Equal(UpdateStatus.Available, r!.Status);
        Assert.Equal(new Version(0, 1, 21), r.Info!.Version);
        Assert.Contains("Адреса сервисов в панели.", r.Info.Notes);
        Assert.Contains("Первое", r.Info.Notes);
        Assert.Equal("https://github.com/under2ker/dnex-dns-hub/releases/tag/v0.1.21", r.Info.PageUrl);
    }

    [Fact]
    public void Atom_SameVersion_IsUpToDate()
    {
        var r = UpdateChecker.ParseAtom(Atom, new Version(0, 1, 21));
        Assert.Equal(UpdateStatus.UpToDate, r!.Status);
        Assert.Equal(new Version(0, 1, 21), r.Latest);
    }

    [Theory]
    [InlineData("https://www.rusporno.pro/go?l=1&u=video", true)]
    [InlineData("https://example.com/video/porno-123", true)]
    [InlineData("https://1xbet-mirror.com/", true)]
    [InlineData("https://dns-ai.ru/", false)]
    [InlineData("https://habr.com/ru/articles/870494/", false)]
    [InlineData("https://keyoneday.ru/dns.html", false)]
    public void Adult_sites_are_never_crawled(string url, bool adult) =>
        Assert.Equal(adult, SourceParser.IsAdult(new Uri(url)));
}
