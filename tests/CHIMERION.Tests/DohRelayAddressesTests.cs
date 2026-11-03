using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayAddressesTests
{
    private static readonly IPAddress Old = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress New = IPAddress.Parse("203.0.113.2");

    [Fact]
    public async Task Server_with_a_new_ip_is_reached_after_refresh_and_the_new_address_is_saved()
    {
        IPAddress[]? saved = null;
        var addresses = new DohRelayAddresses(new[] { Old },
            _ => Task.FromResult<IReadOnlyList<IPAddress>>(new[] { New }), fresh => saved = fresh);
        var tried = new List<IPAddress>();
        using var connected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Прежний адрес больше не принимает подключения, новый отвечает.
        Task<Socket> Connect(IPEndPoint endpoint, CancellationToken token)
        {
            tried.Add(endpoint.Address);
            return endpoint.Address.Equals(New) ? Task.FromResult(connected)
                : Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused));
        }
        var (socket, _) = await DohRelayHttp.ConnectWithRefreshAsync(addresses, 443, 0, Connect, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Same(connected, socket);
        Assert.Equal(new[] { Old, New }, tried);
        Assert.Equal(new[] { New }, saved);
        Assert.Equal(new[] { New }, addresses.Current);
    }

    [Fact]
    public async Task Refresh_is_rate_limited_so_a_dead_server_does_not_cause_frequent_requests()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        int asked = 0;
        var addresses = new DohRelayAddresses(new[] { Old },
            _ => { asked++; return Task.FromResult<IReadOnlyList<IPAddress>>(new[] { Old }); }) { Now = () => now };
        Assert.False(await addresses.RefreshAsync(CancellationToken.None));
        Assert.False(await addresses.RefreshAsync(CancellationToken.None));
        Assert.Equal(1, asked);
        now = now.AddMinutes(2);
        Assert.False(await addresses.RefreshAsync(CancellationToken.None));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task Failed_or_empty_lookup_keeps_the_current_addresses()
    {
        var failing = new DohRelayAddresses(new[] { Old }, _ => throw new HttpRequestException("эталон недоступен")) { MinInterval = TimeSpan.Zero };
        Assert.False(await failing.RefreshAsync(CancellationToken.None));
        Assert.Equal(new[] { Old }, failing.Current);
        var empty = new DohRelayAddresses(new[] { Old }, _ => Task.FromResult<IReadOnlyList<IPAddress>>(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })) { MinInterval = TimeSpan.Zero };
        Assert.False(await empty.RefreshAsync(CancellationToken.None));
        Assert.Equal(new[] { Old }, empty.Current);
    }

    [Fact]
    public async Task Same_addresses_do_not_trigger_a_retry()
    {
        bool saved = false;
        var addresses = new DohRelayAddresses(new[] { Old, New },
            _ => Task.FromResult<IReadOnlyList<IPAddress>>(new[] { New, Old }), _ => saved = true);
        Assert.False(await addresses.RefreshAsync(CancellationToken.None));
        Assert.False(saved);
    }

    [Fact]
    public async Task Lookup_uses_only_references_with_an_ip_address_and_skips_failing_ones()
    {
        var asked = new List<string>();
        Task<List<IPAddress>> Query(string reference, string host, CancellationToken _)
        {
            asked.Add(reference);
            return asked.Count == 1 ? Task.FromException<List<IPAddress>>(new TimeoutException())
                : Task.FromResult(new List<IPAddress> { IPAddress.Parse("2001:db8::1"), New });
        }
        var result = await DohRelayAddresses.ResolveAsync("dns.example.test", Query, CancellationToken.None);
        Assert.Equal(new[] { New }, result);
        Assert.All(asked, reference => Assert.True(IPAddress.TryParse(new Uri(reference).Host, out _), reference));
        Assert.Equal(2, asked.Count);
    }
}
