using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DohRelayEndToEndTests
{
    [Fact]
    public async Task Udp_query_reaches_local_https_server_and_returns_matching_answer()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=dns-relay.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("dns-relay.invalid");
        request.CertificateExtensions.Add(names.Build());
        using var original = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using var cert = new X509Certificate2(original.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            int port = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var received = Task.Run(async () =>
            {
                using var socket = await upstream.AcceptTcpClientAsync(timeout.Token);
                using var ssl = new SslStream(socket.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token);
                var headerBytes = new List<byte>();
                var one = new byte[1];
                while (headerBytes.Count < 16384)
                {
                    await ssl.ReadExactlyAsync(one, timeout.Token); headerBytes.Add(one[0]);
                    int n = headerBytes.Count;
                    if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                Assert.Contains("POST /dns-query", headers);
                Assert.Contains("dns-relay.invalid", headers);
                var body = new byte[12]; await ssl.ReadExactlyAsync(body, timeout.Token);
                Assert.Equal(0x12, body[0]); Assert.Equal(0x34, body[1]);
                body[2] |= 0x80;
                var prefix = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/dns-message\r\nContent-Length: 12\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(prefix, timeout.Token); await ssl.WriteAsync(body, timeout.Token);
            }, timeout.Token);
            using var client = DohRelayHttp.Create(new[] { IPAddress.Loopback }, (_, certificate, _, _) => certificate?.GetCertHashString() == cert.GetCertHashString());
            var transport = new DohRelayTransport(client, new Uri($"https://dns-relay.invalid:{port}/dns-query"));
            await using var proxy = new DohLocalListener(IPAddress.Loopback, 0, transport); proxy.Start();
            using var udp = new UdpClient();
            var query = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, proxy.Port), timeout.Token);
            await received;
            var result = await udp.ReceiveAsync(timeout.Token);
            Assert.Equal(0x12, result.Buffer[0]); Assert.Equal(0x34, result.Buffer[1]); Assert.Equal(0x81, result.Buffer[2]);
        }
        finally { upstream.Stop(); }
    }
}
