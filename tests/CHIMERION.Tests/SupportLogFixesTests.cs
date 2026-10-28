using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Случаи из журналов пользователей: общий DNS совпал со Smart DNS, служебные адреса проверки интернета.</summary>
public class SupportLogFixesTests
{
    [Fact]
    public void General_Dns_Same_As_Smart_Dns_Is_Detected()
    {
        var split = new SplitDnsSettings { Enabled = true, GeneralName = "Zapret DNS", GeneralAddresses = { "83.217.211.149" } };
        Assert.True(SplitDns.GeneralIsSmart(split, new[] { "83.217.211.149" }));
        Assert.False(SplitDns.GeneralIsSmart(split, new[] { "203.0.113.11" }));
        Assert.False(SplitDns.GeneralIsSmart(new SplitDnsSettings(), new[] { "83.217.211.149" })); // «не менять DNS системы»
    }

    [Theory]
    [InlineData("steamconnecttest.com")]
    [InlineData("connectivitycheck.gstatic.com")]
    [InlineData("captive.apple.com")]
    [InlineData("detectportal.firefox.com")]
    public void Connectivity_Check_Hosts_Are_Not_Sites(string host) => Assert.Null(SiteWatch.SiteOf(host));

    [Fact]
    public void Real_Sites_Are_Still_Watched() => Assert.Equal("dns-ai.ru", SiteWatch.SiteOf("dns-ai.ru"));
}
