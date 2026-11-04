using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

namespace Dnex.Services;

/// <summary>
/// Уборка прежнего посредника DoH (0.1.120–0.1.125): задания Планировщика «CHIMERION DoH &lt;канал&gt;» с запуском
/// при входе, его процессы и файлы doh-relay*.json. Его заменил DoH-прокси внутри программы. Без уборки задание
/// при каждом входе запускало бы посредник, который держит 127.0.0.x:53 и мешает прокси.
/// </summary>
internal static partial class LegacyRelayCleanup
{
    /// <summary>Ключ, с которым задание запускало посредник. Новая версия с ним только убирает свое задание и выходит.</summary>
    internal const string HelperArgument = "--doh-relay-helper";
    private const string TaskPrefix = "CHIMERION DoH ";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ChannelRe();

    internal static bool IsChannel(string? value) => value != null && ChannelRe().IsMatch(value);

    /// <summary>Запуск старым заданием: удалить это задание и закрыться, ничего не запуская.</summary>
    internal static int RunAsLegacyHelper(string? channel)
    {
        if (IsChannel(channel))
        {
            try { Schtasks(TimeSpan.FromSeconds(10), "/Delete", "/TN", TaskPrefix + channel, "/F"); }
            catch (Exception) { /* убрать не вышло: уберет следующий обычный запуск */ }
        }
        return 0;
    }

    /// <summary>Убирает все следы прежнего посредника. В журнал — только если что-то нашлось.</summary>
    internal static async Task RunAsync(Action<string> log)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        int tasks = 0, stopped = 0, files = 0;
        await Task.Run(async () =>
        {
            // Задания прежнего посредника: имя «CHIMERION DoH <канал>»
            try
            {
                var (code, output) = Schtasks(TimeSpan.FromSeconds(15), "/Query", "/FO", "CSV", "/NH");
                if (code == 0)
                    foreach (Match m in Regex.Matches(output, @"\\" + Regex.Escape(TaskPrefix) + "([0-9a-f]{32})"))
                        channels.Add(m.Groups[1].Value);
            }
            catch (Exception) { }
            // Каналы из файлов настроек посредника
            foreach (var path in SafeFiles())
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var tail = name.Length >= 32 ? name[^32..] : "";
                if (IsChannel(tail)) channels.Add(tail);
            }
            foreach (var channel in channels)
            {
                if (await StopProcessAsync(channel).ConfigureAwait(false)) stopped++;
                try { if (Schtasks(TimeSpan.FromSeconds(10), "/Delete", "/TN", TaskPrefix + channel, "/F").Code == 0) tasks++; }
                catch (Exception) { }
            }
            foreach (var path in SafeFiles())
            {
                try { File.Delete(path); files++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }).ConfigureAwait(false);
        if (tasks + stopped + files > 0)
            log($"Убраны следы прежнего посредника DoH: заданий {tasks}, процессов {stopped}, файлов {files}");
    }

    private static IEnumerable<string> SafeFiles()
    {
        try { return Directory.Exists(Storage.Dir) ? Directory.GetFiles(Storage.Dir, "doh-relay*.json") : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Команда остановки работающему посреднику по его каналу. Без CurrentUserOnly: он отказывал, когда программа
    /// с правами администратора, а посредник без них; владельца канала проверяем сами.
    /// </summary>
    private static async Task<bool> StopProcessAsync(string channel)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "DNEX-doh-" + channel, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!OwnedByCurrentUser(pipe)) return false;
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("stop\n"), timeout.Token).ConfigureAwait(false);
            var reply = new byte[3];
            await pipe.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            return reply.AsSpan().SequenceEqual("ok\n"u8);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return false; }
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

    /// <summary>Запуск schtasks с ограничением по времени. Точка подмены для тестов.</summary>
    internal static Func<TimeSpan, string[], (int Code, string Output)> SchtasksRunner = RunSchtasks;

    private static (int Code, string Output) Schtasks(TimeSpan timeout, params string[] arguments) => SchtasksRunner(timeout, arguments);

    private static (int Code, string Output) RunSchtasks(TimeSpan timeout, string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Планировщик");
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "");
        }
        return (process.ExitCode, stdout.Result);
    }
}
