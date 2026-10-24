using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>
/// Запуск DNEX вместе с Windows через Планировщик заданий: при входе в систему, с наивысшими правами.
/// Так программа стартует уже с правами администратора и после загрузки возвращает прежний DNS без запроса UAC.
/// Права спрашиваются один раз — при включении или выключении автозапуска. Программа стартует с ключом --tray:
/// окно не показывается, работает значок в трее.
/// </summary>
public static class AutoStart
{
    public const string TaskName = "DNEX";
    public const string TrayArg = "--tray";

    // Прежний способ (ключ Run без прав администратора): при переходе на планировщик убирается
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string? ExePath => Environment.ProcessPath;

    public static bool IsEnabled() => TaskExists() || RunKeyExists();

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

    private static bool RunKeyExists()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(TaskName) is string v && v.Length > 0;
        }
        catch (Exception) { return false; }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(TaskName, throwOnMissingValue: false);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Включает или выключает автозапуск (задание планировщика). Спрашивает права администратора, если программа
    /// запущена без них. False — права не выданы или планировщик отказал.
    /// </summary>
    public static async Task<bool> SetAsync(bool enable)
    {
        RemoveRunKey();
        static string Q(string v) => "'" + v.Replace("'", "''") + "'";
        var user = Environment.UserDomainName + "\\" + Environment.UserName; // пользователь, а не учетка, выдавшая права
        string script;
        if (enable)
        {
            if (ExePath is not { } exe) return false;
            script = $"""
                $a = New-ScheduledTaskAction -Execute {Q(exe)} -Argument '{TrayArg}'
                $t = New-ScheduledTaskTrigger -AtLogOn -User {Q(user)}
                $t.Delay = 'PT10S'
                $p = New-ScheduledTaskPrincipal -UserId {Q(user)} -LogonType Interactive -RunLevel Highest
                $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
                Register-ScheduledTask -TaskName {Q(TaskName)} -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null
                Write-Output 'OK'
                """;
        }
        else script = $"Unregister-ScheduledTask -TaskName {Q(TaskName)} -Confirm:$false -ErrorAction SilentlyContinue; Write-Output 'OK'";

        try
        {
            var (code, output) = await DnsSwitcher.RunPsAsync(script, elevated: true);
            return code == 0 && output.Contains("OK") && TaskExists() == enable;
        }
        catch (OperationCanceledException) { return false; } // UAC отклонен
    }

    /// <summary>
    /// Программу перенесли в другую папку: задание указывает на старый путь. С правами администратора путь
    /// обновляется сам, без них — пишется подсказка включить автозапуск заново.
    /// </summary>
    public static async Task RefreshPathAsync(Action<string> log)
    {
        if (RunKeyExists() && !TaskExists())
        {
            log("Автозапуск настроен по-старому, без прав администратора: включите его заново в настройках, чтобы DNS возвращался без запроса прав");
            return;
        }
        if (ExePath is not { } exe || !TaskExists()) return;
        if (TaskXml().Contains(exe, StringComparison.OrdinalIgnoreCase)) return;
        if (DnsSwitcher.IsAdmin() && await SetAsync(true)) log("Автозапуск: путь к программе обновлен");
        else log("Программу перенесли в другую папку: включите автозапуск заново в настройках");
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
