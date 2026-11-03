using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;

namespace Dnex.Services;

/// <summary>Независимый процесс посредника: проверяет DoH, занимает локальный порт и принимает только команды текущего пользователя.</summary>
internal static class DohRelayHost
{
    private static string PipeName(string channel) => "DNEX-doh-" + channel;

    internal static async Task RunAsync(DohRelayConfig config, HttpClient client, int port, CancellationToken cancellationToken)
    {
        var (endpoint, listen, _) = config.Decode();
        var transport = new DohRelayTransport(client, endpoint);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Локальный адрес захватывается до проверки сети: занятый порт не меняет настройки Windows.
        await using var listener = new DohLocalListener(listen, port, transport);
        // После перезагрузки интернет может подняться позже. Запуск не должен зависеть от внешнего ответа.
        listener.Start();
        while (!stopping.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName(config.Channel), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var connection = pipe.WaitForConnectionAsync(stopping.Token);
                var completed = await Task.WhenAny(connection, listener.Completion).ConfigureAwait(false);
                if (completed != connection)
                {
                    await completed.ConfigureAwait(false);
                    throw new InvalidOperationException("Посредник перестал принимать DNS-запросы");
                }
                await connection.ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var command = new byte[5];
                await pipe.ReadExactlyAsync(command, timeout.Token).ConfigureAwait(false);
                var text = Encoding.ASCII.GetString(command);
                if (text is "ping\n" or "stop\n")
                {
                    await pipe.WriteAsync("ok\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    if (text == "stop\n") stopping.Cancel();
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    internal static async Task<bool> CommandAsync(string channel, string command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(channel, "N", out _) || command is not ("ping" or "stop")) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(channel), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), cancellationToken).ConfigureAwait(false);
            var reply = new byte[3];
            await pipe.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
            return reply.AsSpan().SequenceEqual("ok\n"u8);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return false; }
    }
}
