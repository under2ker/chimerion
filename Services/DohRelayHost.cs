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
            // Без CurrentUserOnly: он сравнивает владельца канала с владельцем по умолчанию у токена, а у программы,
            // запущенной с правами администратора, это группа Administrators, хотя посредник без прав того же
            // пользователя. Связь не устанавливалась, и посредник считался не запущенным (Windows 10 у тестера).
            // Пользователя проверяем сами сразу после подключения: чужой канал с тем же именем не пройдет.
            using var pipe = new NamedPipeClientStream(".", PipeName(channel), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!OwnedByCurrentUser(pipe)) return false;
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), cancellationToken).ConfigureAwait(false);
            var reply = new byte[3];
            await pipe.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);
            return reply.AsSpan().SequenceEqual("ok\n"u8);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return false; }
    }

    /// <summary>Канал создан этим же пользователем: владелец — его SID или владелец по умолчанию его токена.</summary>
    internal static bool OwnedByCurrentUser(PipeStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            return owner != null && (owner.Equals(me.User) || owner.Equals(me.Owner));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or System.Security.Principal.IdentityNotMappedException)
        {
            return false;
        }
    }
}
