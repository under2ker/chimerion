using System.Collections.Concurrent;
using System.Net.Http;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>Принимает DNS только на указанном локальном адресе и передает пакеты через DoH.</summary>
internal sealed class DohLocalListener : IAsyncDisposable
{
    private readonly DohRelayTransport _transport;
    private readonly UdpClient _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(64, 64);
    private Task? _udpLoop, _tcpLoop;
    private readonly ConcurrentDictionary<int, Task> _pending = new();
    private int _nextTask;

    internal int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;
    internal Task Completion => _udpLoop != null && _tcpLoop != null
        ? Task.WhenAny(_udpLoop, _tcpLoop).Unwrap() : throw new InvalidOperationException("Обработчик еще не запущен");

    internal DohLocalListener(IPAddress address, int port, DohRelayTransport transport)
    {
        if (!IPAddress.IsLoopback(address)) throw new ArgumentException("Нужен локальный адрес", nameof(address));
        _transport = transport;
        _tcp = new TcpListener(address, port);
        _tcp.Server.ExclusiveAddressUse = true;
        _tcp.Start();
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(address.AddressFamily);
            udp.ExclusiveAddressUse = true;
            udp.Client.Bind(new IPEndPoint(address, Port));
            _udp = udp;
        }
        catch { udp?.Dispose(); _tcp.Stop(); throw; }
    }

    internal void Start()
    {
        if (_udpLoop != null) throw new InvalidOperationException("Обработчик уже запущен");
        _udpLoop = ReceiveUdpAsync();
        _tcpLoop = AcceptTcpAsync();
    }

    private async Task ReceiveUdpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || !_slots.Wait(0)) continue;
            Track(HandleUdpAsync(packet));
        }
    }

    private async Task HandleUdpAsync(UdpReceiveResult packet)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var answer = await _transport.ForwardAsync(packet.Buffer, timeout.Token).ConfigureAwait(false);
            await _udp.SendAsync(answer, packet.RemoteEndPoint, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { _slots.Release(); }
    }

    private async Task AcceptTcpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || !IPAddress.IsLoopback(peer.Address) || !_slots.Wait(0)) { client.Dispose(); continue; }
            Track(HandleTcpAsync(client));
        }
    }

    private async Task HandleTcpAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var stream = client.GetStream();
                var header = new byte[2];
                while (!timeout.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                    int length = (header[0] << 8) | header[1];
                    if (length < 12) break;
                    var query = new byte[length];
                    await stream.ReadExactlyAsync(query, timeout.Token).ConfigureAwait(false);
                    var answer = await _transport.ForwardAsync(query, timeout.Token).ConfigureAwait(false);
                    header[0] = (byte)(answer.Length >> 8); header[1] = (byte)answer.Length;
                    await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(answer, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { _slots.Release(); }
    }

    private void Track(Task task)
    {
        int id = Interlocked.Increment(ref _nextTask);
        _pending[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _pending.TryRemove(id, out _);
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        if (_udpLoop != null) await _udpLoop.ConfigureAwait(false);
        if (_tcpLoop != null) await _tcpLoop.ConfigureAwait(false);
        await Task.WhenAll(_pending.Values).ConfigureAwait(false);
        _stop.Dispose();
        _slots.Dispose();
    }
}
