using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>Что программа считает публичным адресом и допустимым DoH-адресом.</summary>
public class NetworkSafetyTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("172.16.5.5", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    public void IsPublic_SeparatesPublicFromLocal(string ip, bool expected)
    {
        Assert.Equal(expected, SourceParser.IsPublic(ip));
    }

    [Fact]
    public void GeoLocation_UsesTheSamePublicRule()
    {
        Assert.True(GeoLocation.IsPublicIPv4("8.8.4.4"));
        Assert.False(GeoLocation.IsPublicIPv4("192.168.0.1"));
        Assert.False(GeoLocation.IsPublicIPv4(null!));
    }

    [Theory]
    [InlineData("https://dns.bezmezhau.com/dns-query", true)]
    [InlineData("http://dns.example.ru/dns-query", false)]
    [InlineData("https://user:pw@dns.example.ru/dns-query", false)]
    [InlineData("https://192.168.1.1/dns-query", false)]
    [InlineData("https://localhost/dns-query", false)]
    public void IsValidDoh_AcceptsOnlyPublicHttpsWithoutCredentials(string url, bool expected)
    {
        Assert.Equal(expected, SourceParser.IsValidDoh(url, out _));
    }
}
