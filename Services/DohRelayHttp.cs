using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>HTTPS-клиент с заранее известными адресами DoH-сервера: не спрашивает DNS у самого себя.</summary>
internal static class DohRelayHttp
{
    /// <summary>Сколько ждать один адрес. Общий таймаут подключения тратился на первый адрес, и второй не пробовался.</summary>
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    internal static HttpClient Create(IReadOnlyList<IPAddress> addresses, RemoteCertificateValidationCallback? testCertificateValidation = null)
    {
        if (addresses.Count == 0) throw new ArgumentException("Нет адресов DoH-сервера", nameof(addresses));
        return Create(new DohRelayAddresses(addresses), testCertificateValidation);
    }

    /// <summary>Клиент с обновляемыми адресами: если не ответил ни один, адреса спрашиваются заново и подключение повторяется.</summary>
    internal static HttpClient Create(DohRelayAddresses addresses, RemoteCertificateValidationCallback? testCertificateValidation = null)
    {
        int preferred = 0;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            // Общий предел на все адреса; каждый адрес ограничен AttemptTimeout.
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = testCertificateValidation },
            ConnectCallback = async (context, token) =>
            {
                var (socket, index) = await ConnectWithRefreshAsync(addresses, context.DnsEndPoint.Port, Volatile.Read(ref preferred), ConnectSocketAsync, AttemptTimeout, token).ConfigureAwait(false);
                // Следующие подключения начинаются с адреса, который ответил: недоступный не задерживает каждый запрос.
                Volatile.Write(ref preferred, index);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    private static async Task<Socket> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Подключение по текущим адресам; если не ответил ни один, один повтор по обновленным адресам.</summary>
    internal static async Task<(Socket Socket, int Index)> ConnectWithRefreshAsync(DohRelayAddresses addresses, int port, int preferred,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect, TimeSpan attemptTimeout, CancellationToken token)
    {
        try { return await ConnectAnyAsync(addresses.Current, port, preferred, connect, attemptTimeout, token).ConfigureAwait(false); }
        catch (HttpRequestException) when (!token.IsCancellationRequested)
        {
            if (!await addresses.RefreshAsync(token).ConfigureAwait(false)) throw;
            return await ConnectAnyAsync(addresses.Current, port, 0, connect, attemptTimeout, token).ConfigureAwait(false);
        }
    }

    /// <summary>Перебирает адреса, начиная с предпочтительного; на каждый адрес отдельный предел ожидания.</summary>
    internal static async Task<(Socket Socket, int Index)> ConnectAnyAsync(IReadOnlyList<IPAddress> endpoints, int port, int preferred,
        Func<IPEndPoint, CancellationToken, Task<Socket>> connect, TimeSpan attemptTimeout, CancellationToken token)
    {
        Exception? last = null;
        int start = preferred >= 0 && preferred < endpoints.Count ? preferred : 0;
        for (int i = 0; i < endpoints.Count; i++)
        {
            int index = (start + i) % endpoints.Count;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(attemptTimeout);
            try { return (await connect(new IPEndPoint(endpoints[index], port), attempt.Token).ConfigureAwait(false), index); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { last = new TimeoutException("Адрес DoH-сервера не ответил вовремя", ex); }
            catch (SocketException ex) { last = ex; }
        }
        token.ThrowIfCancellationRequested();
        throw new HttpRequestException("Не удалось подключиться к DoH-серверу", last);
    }
}
