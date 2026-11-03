using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>HTTPS-клиент с заранее известными адресами DoH-сервера: не спрашивает DNS у самого себя.</summary>
internal static class DohRelayHttp
{
    internal static HttpClient Create(IReadOnlyList<IPAddress> addresses, RemoteCertificateValidationCallback? testCertificateValidation = null)
    {
        if (addresses.Count == 0) throw new ArgumentException("Нет адресов DoH-сервера", nameof(addresses));
        var endpoints = addresses.Distinct().ToArray();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = testCertificateValidation },
            ConnectCallback = async (context, token) =>
            {
                Exception? last = null;
                foreach (var address in endpoints)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (OperationCanceledException) { socket.Dispose(); throw; }
                    catch (SocketException ex) { last = ex; socket.Dispose(); }
                }
                throw new HttpRequestException("Не удалось подключиться к DoH-серверу", last);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}
