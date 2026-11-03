using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dnex.Services;

/// <summary>Создание и запуск задания под учетной записью пользователя, без изменения сети.</summary>
internal sealed class DohRelayScheduler(Func<string[], CancellationToken, Task<int>> run)
{
    internal static DohRelayScheduler SystemScheduler() => new(RunSchtasksAsync);

    internal async Task<bool> CreateAndRunAsync(string executable, string userSid, string channel, CancellationToken cancellationToken)
    {
        // Уникальный канал не позволяет затронуть задание другого подключения.
        var name = DohRelayTask.TaskName(channel);
        if (await run(new[] { "/Query", "/TN", name }, cancellationToken).ConfigureAwait(false) == 0) return false;
        Directory.CreateDirectory(Storage.Dir);
        var path = Storage.PathOf("doh-task-" + Guid.NewGuid().ToString("N") + ".xml");
        bool createAttempted = false, running = false;
        try
        {
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + DohRelayTask.Xml(executable, userSid);
            await File.WriteAllTextAsync(path, xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
            createAttempted = true;
            if (await run(new[] { "/Create", "/TN", name, "/XML", path }, cancellationToken).ConfigureAwait(false) != 0) return false;
            running = await run(new[] { "/Run", "/TN", name }, cancellationToken).ConfigureAwait(false) == 0;
            return running;
        }
        finally
        {
            try
            {
                if (createAttempted && !running)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    if (await run(new[] { "/Delete", "/TN", name, "/F" }, cleanup.Token).ConfigureAwait(false) != 0)
                        throw new InvalidOperationException("Не удалось подтвердить удаление задания посредника после ошибки запуска");
                }
            }
            finally { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static async Task<int> RunSchtasksAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Планировщик");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
