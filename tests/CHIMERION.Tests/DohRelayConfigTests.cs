using System.Net;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("scenarios")]
public class DohRelayConfigTests
{
    [Fact]
    public void Saved_configuration_protects_the_subscription_address()
    {
        var endpoint = new Uri("https://dns.example.test/dns-query/fake-secret");
        var config = DohRelayConfig.Create(endpoint, IPAddress.Parse("127.0.0.2"), new[] { IPAddress.Parse("203.0.113.10") });
        config.Save();
        var file = File.ReadAllText(Storage.PathOf(DohRelayConfig.FileName));
        Assert.DoesNotContain("fake-secret", file);
        Assert.DoesNotContain("dns.example.test", file);
        var restored = DohRelayConfig.Load()!.Decode();
        Assert.Equal(endpoint, restored.Endpoint);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), restored.Listen);
        Assert.Equal(new[] { IPAddress.Parse("203.0.113.10") }, restored.Upstream);
    }

    [Fact]
    public void Rejects_plaintext_endpoint_and_non_loopback_listener()
    {
        Assert.Throws<InvalidDataException>(() => new DohRelayConfig { ProtectedEndpoint = "https://dns.example.test/", ListenAddress = "127.0.0.2", BootstrapAddresses = new() { "203.0.113.10" } }.Decode());
        Assert.Throws<ArgumentException>(() => DohRelayConfig.Create(new Uri("https://dns.example.test/"), IPAddress.Loopback, new[] { IPAddress.Parse("203.0.113.10") }));
        Assert.Throws<ArgumentException>(() => DohRelayConfig.Create(new Uri("https://dns.example.test/"), IPAddress.Any, new[] { IPAddress.Parse("203.0.113.10") }));
    }
}
