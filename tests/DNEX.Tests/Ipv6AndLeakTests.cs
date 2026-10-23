using System.Net;
using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>IPv6 в DNS-ответах и источниках, разбор ответа теста утечки dnscheck.tools.</summary>
public class Ipv6AndLeakTests
{
    [Fact]
    public void LeakTest_ParsesResolverLine()
    {
        var r = LeakTest.ParseFrom("FROM: 83.217.211.149#45634 (UDP) - Baykov Ilya Sergeevich - Dronten, Flevoland, NL");
        Assert.NotNull(r);
        Assert.Equal("83.217.211.149", r!.Ip);
        Assert.Equal("Baykov Ilya Sergeevich", r.Org);
        Assert.Equal("Dronten, Flevoland, NL", r.Place);
    }

    [Fact]
    public void LeakTest_ParsesIpv6ResolverWithoutCity()
    {
        var r = LeakTest.ParseFrom("FROM: 2a0c:a9c7:241:2000:1000::103#59152 (UDP) - Join-stock company \"Internet Exchange\"MSK-IX\" - RU");
        Assert.NotNull(r);
        Assert.Equal("2a0c:a9c7:241:2000:1000::103", r!.Ip);
        Assert.Equal("RU", r.Place);
    }

    [Theory]
    [InlineData("EDNS: version: 0; flags: do; udp: 1232")]
    [InlineData("ID: 28788")]
    [InlineData("FROM: не-адрес (UDP)")]
    public void LeakTest_IgnoresOtherLines(string line) => Assert.Null(LeakTest.ParseFrom(line));

    [Fact]
    public void Leak_IsDifferentOwners()
    {
        var one = new[] { new ResolverInfo("83.217.211.149", "Baykov Ilya Sergeevich", "NL"), new ResolverInfo("83.217.211.150", "Baykov Ilya Sergeevich", "NL") };
        Assert.False(LeakTest.LooksLikeLeak(one));
        var two = new[] { one[0], new ResolverInfo("193.232.231.49", "MSK-IX", "RU") };
        Assert.True(LeakTest.LooksLikeLeak(two));
    }

    [Theory]
    [InlineData("Join-stock company \"Internet Exchange\"MSK-IX\"", "Internet Exchange MSK-IX")]
    [InlineData("Edge Technology Plus d.o.o. Beograd", "Edge Technology Plus Beograd")]
    [InlineData("Baykov Ilya Sergeevich", "Baykov Ilya Sergeevich")]
    [InlineData("Google LLC", "Google")]
    public void CleanOrg_DropsLegalFormAndQuotes(string raw, string expected) => Assert.Equal(expected, LeakTest.CleanOrg(raw));

    [Fact]
    public void Leak_SameOwnerDifferentSpelling_IsNotLeak()
    {
        var list = new[] { new ResolverInfo("2a0c::103", "Join-stock company \"Internet Exchange\"MSK-IX\"", "RU"),
                           new ResolverInfo("193.232.231.49", "Join-stock company \"Internet Exchange\"MSK-IX\"", "RU") };
        Assert.False(LeakTest.LooksLikeLeak(list));
    }

    [Fact]
    public void DnsWire_ParsesAaaaAnswer()
    {
        // Ответ на AAAA-запрос имени «a»: заголовок, вопрос, один ответ с указателем на имя и 16 байтами адреса
        var ip = IPAddress.Parse("2a03:afc0:9::235d");
        var msg = new List<byte> { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 1, (byte)'a', 0, 0, 28, 0, 1,
                                   0xC0, 0x0C, 0, 28, 0, 1, 0, 0, 0, 60, 0, 16 };
        msg.AddRange(ip.GetAddressBytes());
        var list = DnsWire.ParseA(msg.ToArray(), 0x1234);
        Assert.Equal(new[] { ip }, list);
    }

    [Theory]
    [InlineData("2a03:afc0:9::235d", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("fe80::8eae:dbff:fe71:1bed", false)] // адрес роутера в локальной сети
    [InlineData("fec0:0:0:ffff::1", false)]          // служебный адрес Windows
    [InlineData("2001:db8::1", false)]               // адрес для документации
    [InlineData("12:30:45", false)]                  // время, а не адрес
    [InlineData("83.217.211.149", false)]
    public void IsPublic6_OnlyGlobalIpv6(string ip, bool expected) => Assert.Equal(expected, SourceParser.IsPublic6(ip));
}
