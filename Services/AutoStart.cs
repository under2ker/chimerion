using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>
/// Запуск CHIMERION вместе с Windows: ключ Run текущего пользователя, без прав администратора. Программа стартует
/// с ключом --tray (окно не показывается, работает значок в трее). Права Windows спрашивает только тогда, когда
/// нужно вернуть DNS (его сбросили обновление Windows, другой адаптер или программа): тогда появится запрос UAC.
///
/// До 0.1.27 автозапуск был заданием Планировщика с наивысшими правами. Оно запускало exe из папки, куда может
/// писать любая программа пользователя, и подмена файла давала права администратора без запроса. Такое задание
/// убирается при включении или выключении автозапуска (это один запрос UAC).
/// </summary>
public static class AutoStart
{
    public const string TaskName = "DNEX";
    public const string TrayArg = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DNEX";

    // С учетом переименования DNEX.exe → CHIMERION.exe в этом сеансе (см. Updater)
    private static string? ExePath => Updater.CurrentExePath;

    public static bool IsEnabled() => RunKeyCommand() != null || TaskExists();

    /// <summary>Осталось задание Планировщика старых версий (с правами администратора).</summary>
    public static bool LegacyTaskExists() => TaskExists();

    private static bool TaskExists() => Schtasks($"/Query /TN \"{TaskName}\"").Code == 0;

    /// <summary>XML задания (путь к программе внутри). Пусто, если задания нет.</summary>
    private static string TaskXml() { var r = Schtasks($"/Query /TN \"{TaskName}\" /XML"); return r.Code == 0 ? r.Output : ""; }

    private static (int Code, string Output) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return (p.HasExited ? p.ExitCode : -1, output);
        }
        catch (Exception) { return (-1, ""); }
    }

    /// <summary>Команда в ключе Run. Null, если автозапуска через ключ нет.</summary>
    internal static string? RunKeyCommand()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string v && v.Length > 0 ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Строка запуска для ключа Run: путь в кавычках и ключ --tray.</summary>
    internal static string CommandFor(string exe) => $"\"{exe}\" {TrayArg}";

    /// <summary>Путь к программе из строки ключа Run.</summary>
    internal static string ExeFromCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); return end > 0 ? cmd[1..end] : cmd.Trim('"'); }
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? cmd[..sp] : cmd;
    }

    private static bool WriteRunKey(string? command)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (command == null) k.DeleteValue(RunValue, throwOnMissingValue: false);
            else k.SetValue(RunValue, command);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Файл программы переименован или перенесен: автозапуск, который указывал на прежний файл, переводится на новый.</summary>
    internal static void Retarget(string from, string to)
    {
        if (RunKeyCommand() is { } cmd && ExeFromCommand(cmd).Equals(from, StringComparison.OrdinalIgnoreCase))
            WriteRunKey(CommandFor(to));
    }

    /// <summary>Убрать задание старых версий. Оно создано с правами администратора, и удалить его можно только с ними.</summary>
    private static async Task<bool> RemoveLegacyTaskAsync()
    {
        if (!TaskExists()) return true;
        try
        {
            await DnsSwitcher.RunPsAsync($"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue", elevated: true);
        }
        catch (OperationCanceledException) { return false; } // UAC отклонен
        return !TaskExists();
    }

    /// <summary>
    /// Включает или выключает автозапуск. Права нужны, только если осталось задание старых версий: его надо убрать.
    /// False — ключ не записался или старое задание убрать не дали.
    /// </summary>
    public static async Task<bool> SetAsync(bool enable)
    {
        if (enable)
        {
            if (ExePath is not { } exe || !WriteRunKey(CommandFor(exe))) return false;
        }
        else if (!WriteRunKey(null)) return false;
        return await RemoveLegacyTaskAsync();
    }

    /// <summary>
    /// После запуска: программу перенесли в другую папку — путь в ключе Run обновляется сам (права не нужны).
    /// Осталось задание старых версий — подсказка включить автозапуск заново, чтобы программа стартовала без прав.
    /// </summary>
    public static Task RefreshPathAsync(Action<string> log)
    {
        // Копия данных для тестов и снимков: автозапуск принадлежит настоящей программе, его не трогаем
        if (Environment.GetEnvironmentVariable("DNEX_DATA_DIR") is { Length: > 0 }) return Task.CompletedTask;
        if (TaskExists())
        {
            var xml = TaskXml();
            if (xml.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase))
                log("Автозапуск настроен по-старому, с правами администратора без запроса. Выключите и включите его в настройках: " +
                    "программа будет стартовать без прав, а права спросит, только когда нужно вернуть DNS");
            return Task.CompletedTask;
        }
        if (ExePath is not { } exe || RunKeyCommand() is not { } cmd) return Task.CompletedTask;
        var old = ExeFromCommand(cmd);
        if (old.Equals(exe, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        // Путь меняем, только если прежнего файла больше нет (программу перенесли). Если он на месте, значит
        // запущена вторая копия из другой папки (например, свежая сборка): автозапуск остается за прежней
        if (File.Exists(old))
        {
            log($"Автозапуск запускает другую копию программы: {old}. Чтобы запускалась эта, включите автозапуск заново в настройках");
            return Task.CompletedTask;
        }
        if (WriteRunKey(CommandFor(exe))) log("Автозапуск: путь к программе обновлен");
        return Task.CompletedTask;
    }

    /// <summary>Запущена из автозагрузки: окно не показываем, только трей.</summary>
    public static bool StartedHidden =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(TrayArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ссылку открываем обычным браузером. Программа может работать с правами администратора (автозапуск),
    /// и тогда браузер открылся бы тоже с правами администратора; через проводник он запускается как обычно.
    /// </summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (DnsSwitcher.IsAdmin()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
