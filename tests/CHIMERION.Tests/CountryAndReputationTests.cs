using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Выбранная страна выхода и репутация адресов выхода.</summary>
[Collection("scenarios")]
public class CountryAndReputationTests : IDisposable
{
    private readonly Func<string, CancellationToken, Task<(int?, string?, string?)>> _risk = IpReputation.RiskOf;
    private readonly Func<string, string, CancellationToken, Task<bool>> _listed = IpReputation.ListedIn;

    public CountryAndReputationTests()
    {
        AccountSafety.Pinned = null;
        IpReputation.Reset();
    }

    public void Dispose()
    {
        AccountSafety.Pinned = null;
        IpReputation.Reset();
        IpReputation.RiskOf = _risk;
        IpReputation.ListedIn = _listed;
    }

    private static DnsServer Server(string exitIp, params string[] countries)
    {
        var s = new DnsServer { Name = "s", Primary = "203.0.113.10", Status = ServerStatus.Ok };
        var names = new[] { "ChatGPT", "Claude", "Gemini" };
        for (int i = 0; i < countries.Length; i++) s.EgressBy[names[i]] = new Egress(exitIp, countries[i]);
        return s;
    }

    [Fact]
    public void Without_Pinned_Country_Any_Server_Fits()
    {
        Assert.True(AccountSafety.Fits(Server("8.8.8.8", "DE")));
        Assert.True(AccountSafety.Fits(Server("8.8.8.8")));
    }

    [Fact]
    public void Pinned_Country_Allows_Only_Servers_With_All_Exits_There()
    {
        AccountSafety.Pinned = "FR";
        Assert.True(AccountSafety.Fits(Server("8.8.8.8", "FR", "FR")));
        Assert.False(AccountSafety.Fits(Server("8.8.8.8", "FR", "DE")));
        Assert.False(AccountSafety.Fits(Server("8.8.8.8", "DE")));
        Assert.False(AccountSafety.Fits(Server("8.8.8.8"))); // страна выхода неизвестна: не подключаем сами
    }

    [Fact]
    public void Pinned_Country_Becomes_Home_Even_Without_History()
    {
        var safety = new AccountSafety();
        Assert.Null(safety.Home("Claude"));
        AccountSafety.Pinned = "FR";
        Assert.Equal("FR", safety.Home("Claude"));
        var mism = safety.Compare(new[] { new KeyValuePair<string, string?>("Claude", "DE") });
        Assert.Single(mism); // переход на Германию при выбранной Франции: предупредим
    }

    [Fact]
    public async Task Blacklisted_Exit_Makes_Server_Bad()
    {
        IpReputation.RiskOf = (_, _) => Task.FromResult<(int?, string?, string?)>((66, "VPN", "Example"));
        IpReputation.ListedIn = (name, _, _) => Task.FromResult(name.EndsWith("dnsbl.dronebl.org"));
        var rep = await IpReputation.CheckAsync("8.8.4.4", "https://1.1.1.1/dns-query");
        Assert.NotNull(rep);
        Assert.True(rep!.Bad);
        Assert.Contains("DroneBL", rep.Lists);
        Assert.Contains("риск 66 из 100", IpReputation.Describe(rep));
        Assert.True(IpReputation.IsBad(Server("8.8.4.4", "NL")));
    }

    [Fact]
    public async Task Typical_Smart_Dns_Risk_Is_Not_Bad()
    {
        // У любого Smart DNS выход на сервере: «VPN» и риск 66 обычное дело, плохим это не считается
        IpReputation.RiskOf = (_, _) => Task.FromResult<(int?, string?, string?)>((66, "VPN", null));
        IpReputation.ListedIn = (_, _, _) => Task.FromResult(false);
        var rep = await IpReputation.CheckAsync("1.0.0.1", "https://1.1.1.1/dns-query");
        Assert.False(rep!.Bad);
        Assert.False(IpReputation.IsBad(Server("1.0.0.1", "NL")));
    }

    [Fact]
    public async Task Private_Addresses_Are_Never_Sent()
    {
        int calls = 0;
        IpReputation.RiskOf = (_, _) => { calls++; return Task.FromResult<(int?, string?, string?)>((1, null, null)); };
        Assert.Null(await IpReputation.CheckAsync("192.168.1.10", "https://1.1.1.1/dns-query"));
        Assert.Equal(0, calls);
    }
}
