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
    public void ManualServersSurviveOverflow()
    {
        // Аудит CR-15: свой сервер, добавленный вручную, но сейчас нерабочий, выпадал при переполнении списка
        var list = Enumerable.Range(0, ServerListGuard.MaxServers + 500)
            .Select(i => Srv($"198.51.{i / 250}.{i % 250}", "https://example.org/list", ServerStatus.Fail)).ToList();
        var mine = Srv("203.0.113.99", null, ServerStatus.Fail);
        mine.Manual = true;
        mine.AddedAt = DateTime.Now.AddDays(1); // самый новый: раньше уходил первым
        list.Add(mine);
        ServerListGuard.Purge(list, null);
        Assert.Contains(mine, list);
        Assert.Equal(ServerListGuard.MaxServers, list.Count);
    }

    [Fact]
    public void SmallListUntouched()
    {
        var list = new List<DnsServer> { Srv("203.0.113.7", "https://habr.com/"), Srv("203.0.113.8", null) };
        Assert.Equal(0, ServerListGuard.Purge(list, null));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void HostsFileWithManyLinesButFewServersIsNotMassList()
    {
        // Как файл hosts GeoHideDNS: тысячи строк с одними и теми же двадцатью адресами
        var lines = Enumerable.Range(0, 4128).Select(i => Srv($"203.0.113.{i % 19 + 1}", "https://raw.githubusercontent.com/x/hosts")).ToList();
        Assert.Equal(19, ServerListGuard.Distinct(lines));
        Assert.True(ServerListGuard.Distinct(lines) <= ServerListGuard.MaxPerPage);
    }

    [Fact]
    public void LimitsAreSane()
    {
        Assert.InRange(ServerListGuard.MaxPerPage, 50, 300);
        Assert.InRange(ServerListGuard.MaxNewPerSearch, ServerListGuard.MaxPerPage, ServerListGuard.MaxServers);
    }

    private static List<DnsServer> Batch(int n, ServerStatus status) =>
        Enumerable.Range(1, n).Select(i => Srv("203.0.113." + i, null, status)).ToList();

    [Fact]
    public void NoServerAnsweredIsNetworkProblemNotDeadList()
    {
        // сеть пускает в интернет, но не к чужим DNS: все серверы «не работают», хотя раньше не работал ни один
        Assert.True(ServerListGuard.NoneAnswered(Batch(ServerListGuard.NoAnswerMinBatch, ServerStatus.Fail)));
        Assert.True(ServerListGuard.NoneAnswered(Batch(350, ServerStatus.Fail)));
    }

    [Fact]
    public void FewOrMixedResultsAreNotNetworkProblem()
    {
        Assert.False(ServerListGuard.NoneAnswered(Batch(ServerListGuard.NoAnswerMinBatch - 1, ServerStatus.Fail)));
        var mixed = Batch(20, ServerStatus.Fail); mixed[7].Status = ServerStatus.Partial;
        Assert.False(ServerListGuard.NoneAnswered(mixed));
        Assert.False(ServerListGuard.NoneAnswered(Batch(20, ServerStatus.Unknown)));   // непроверенные не считаются ответившими, но и сбоем тоже
    }

    [Fact]
    public void AutoCleanNeverEmptiesTheList()
    {
        var all = Batch(10, ServerStatus.Fail);
        Assert.True(ServerListGuard.WouldEmptyList(all, 10));
        Assert.False(ServerListGuard.WouldEmptyList(all, 9));
        Assert.False(ServerListGuard.WouldEmptyList(all, 0));
        // строка CHIMERION DNS не считается настоящим сервером
        all.Add(new DnsServer { Name = "promo", Primary = "203.0.113.200", IsPromo = true });
        Assert.True(ServerListGuard.WouldEmptyList(all, 10));
    }
}
