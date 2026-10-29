using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Общие каталоги DNS (public-dns.info) не раздувают список: однажды поиск набрал так 100 тысяч серверов.</summary>
public class ServerListGuardTests
{
    private static DnsServer Srv(string ip, string? sourceUrl, ServerStatus status = ServerStatus.Fail, bool fav = false) =>
        new() { Name = ip, Primary = ip, SourceUrl = sourceUrl, Status = status, IsFavorite = fav };

    [Theory]
    [InlineData("public-dns.info", true)]
    [InlineData("www.public-dns.info", true)]
    [InlineData("dnscrypt.info", true)]
    [InlineData("habr.com", false)]
    [InlineData(null, false)]
    public void RecognizesMassHosts(string? host, bool mass) => Assert.Equal(mass, ServerListGuard.IsMassHost(host));

    [Fact]
    public void RemovesCatalogServersButKeepsUsefulOnes()
    {
        var connected = Srv("203.0.113.1", "https://public-dns.info/nameservers.txt");
        var list = new List<DnsServer>
        {
            Srv("203.0.113.2", "https://public-dns.info/nameservers.txt"),
            Srv("203.0.113.3", "https://public-dns.info/nameservers-all.txt"),
            Srv("203.0.113.4", "https://public-dns.info/nameservers.txt", ServerStatus.Ok),
            Srv("203.0.113.5", "https://public-dns.info/nameservers.txt", fav: true),
            connected,
            Srv("203.0.113.6", "https://habr.com/ru/articles/870494/"),
        };
        Assert.Equal(2, ServerListGuard.Purge(list, connected.Id));
        Assert.Equal(new[] { "203.0.113.4", "203.0.113.5", "203.0.113.1", "203.0.113.6" }, list.Select(s => s.Primary));
    }

    [Fact]
    public void ShrinksBloatedListToLimitKeepingWorking()
    {
        var list = Enumerable.Range(0, ServerListGuard.MaxServers + 500)
            .Select(i => Srv($"198.51.{i / 250}.{i % 250}", "https://example.org/list", i < 10 ? ServerStatus.Ok : ServerStatus.Fail)).ToList();
        ServerListGuard.Purge(list, null);
        Assert.Equal(ServerListGuard.MaxServers, list.Count);
        Assert.Equal(10, list.Count(s => s.Status == ServerStatus.Ok));
    }

    [Fact]
    public void SmallListUntouched()
    {
        var list = new List<DnsServer> { Srv("203.0.113.7", "https://habr.com/"), Srv("203.0.113.8", null) };
        Assert.Equal(0, ServerListGuard.Purge(list, null));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void LimitsAreSane()
    {
        Assert.InRange(ServerListGuard.MaxPerPage, 50, 300);
        Assert.InRange(ServerListGuard.MaxNewPerSearch, ServerListGuard.MaxPerPage, ServerListGuard.MaxServers);
    }
}
