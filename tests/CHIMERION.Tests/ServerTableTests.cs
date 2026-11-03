using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class ServerTableTests
{
    [Fact]
    public void Named_server_keeps_its_name_when_location_is_known()
    {
        var server = new DnsServer { Name = "Example DNS", Primary = "203.0.113.10", Country = "NL" };
        Assert.Equal("Example DNS", server.ShortName);
        server.Name = "https://dns.example.test/dns-query/private-token";
        Assert.Equal("dns.example.test", server.ShortName);
    }

    [Fact]
    public void Flags_use_only_each_services_measured_country_and_refresh()
    {
        var server = new DnsServer { Summary = "ChatGPT ✓ · Claude ✓ · Gemini ✓" };
        server.EgressBy["ChatGPT"] = new Egress("203.0.113.11", "NL");
        server.EgressBy["Claude"] = new Egress("203.0.113.12", "DE");
        server.NotifyEgress();
        Assert.Equal("NL", server.Chips.Single(x => x.Name == "ChatGPT").Country);
        Assert.Equal("DE", server.Chips.Single(x => x.Name == "Claude").Country);
        Assert.Null(server.Chips.Single(x => x.Name == "Gemini").Country);
        server.EgressBy.Remove("ChatGPT");
        server.NotifyEgress();
        Assert.Null(server.Chips.Single(x => x.Name == "ChatGPT").Country);
    }
}
