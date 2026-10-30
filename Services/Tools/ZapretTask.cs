using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Dnex.Services.Tools;

/// <summary>
/// Обход zapret без запроса прав при каждом включении. Драйвер WinDivert работает только с правами администратора,
/// а CHIMERION запускается без них (и вместе с Windows тоже). Раньше каждое включение и выключение обхода и каждая
/// загрузка Windows снова спрашивали права. Теперь при первом включении Windows спрашивает один раз, и программа
/// создает два задания Планировщика с наивысшими правами: «запустить обход» и «остановить обход». Задания запускают
/// саму CHIMERION с ключом <see cref="HelperArg"/>: она только включает или выключает winws и сразу закрывается.
/// Запустить задание может текущий пользователь, а изменить его только администратор (дескриптор безопасности в XML).
/// </summary>
public static class ZapretTask
{
    public const string StartName = "CHIMERION zapret start";
    public const string StopName = "CHIMERION zapret stop";
    public const string HelperArg = "--zapret-helper";

    private static string? ExePath => Updater.CurrentExePath;

    /// <summary>Оба задания есть и запускают эту копию программы. Тогда обход включается и выключается без вопроса Windows.</summary>
    public static bool Ready()
    {
        if (ExePath is not { } exe) return false;
        return TaskRuns(StartName, exe, "start") && TaskRuns(StopName, exe, "stop");
    }

    private static bool TaskRuns(string name, string exe, string verb)
    {
        var (code, xml) = Schtasks($"/Query /TN \"{name}\" /XML");
        // Вывод schtasks приходит в кодировке консоли: кириллица в пути (папка пользователя) может исказиться.
        // Путь сравнивается по латинской части, имя файла и ключ запуска проверяются точно
        return code == 0 && Ascii(xml).Contains(Ascii(Escape(Quote(exe))), StringComparison.OrdinalIgnoreCase)
                         && xml.Contains($"{HelperArg} {verb}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Создает задания (Windows спросит права один раз). Без прав администратора создается PowerShell с повышением.
    /// True, если после этого задания на месте. Отказ в правах пробрасывается как <see cref="UacDeclinedException"/>.
    /// </summary>
    public static async Task<bool> CreateAsync()
    {
        if (ExePath is not { } exe) return false;
        var user = WindowsIdentity.GetCurrent().User?.Value;
        if (user == null) return false;
        var dir = Path.Combine(Storage.Dir, "tasks");
        Directory.CreateDirectory(dir);
        var start = Path.Combine(dir, "zapret-start.xml");
        var stop = Path.Combine(dir, "zapret-stop.xml");
        // schtasks читает XML задания в UTF-16
        await File.WriteAllTextAsync(start, Xml(exe, "start", user), Encoding.Unicode);
        await File.WriteAllTextAsync(stop, Xml(exe, "stop", user), Encoding.Unicode);
        try
        {
            var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            var script = $"& '{schtasks}' /Create /TN '{StartName}' /XML '{start}' /F | Out-Null; " +
                         $"& '{schtasks}' /Create /TN '{StopName}' /XML '{stop}' /F | Out-Null";
            await DnsSwitcher.RunPsAsync(script, elevated: true);
        }
        finally
        {
            try { File.Delete(start); } catch (IOException) { }
            try { File.Delete(stop); } catch (IOException) { }
        }
        return Ready();
    }

    /// <summary>Запуск задания: права не спрашиваются, задание уже с ними. False — задание не запустилось.</summary>
    public static bool Run(string name) => Schtasks($"/Run /TN \"{name}\"").Code == 0;

    /// <summary>
    /// XML задания: запуск только по требованию (без расписания), с наивысшими правами от имени пользователя,
    /// пока он в системе. Дескриптор: администраторы и система могут все, пользователь только читать и запускать.
    /// </summary>
    internal static string Xml(string exe, string verb, string userSid)
    {
        var sddl = $"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{userSid})";
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>CHIMERION: {(verb == "start" ? "запуск" : "остановка")} обхода zapret без запроса прав</Description>
    <SecurityDescriptor>{sddl}</SecurityDescriptor>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>{Escape(userSid)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(Quote(exe))}</Command>
      <Arguments>{HelperArg} {verb}</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>Экранирование для текста элемента XML: кавычки в пути остаются как есть, так их и выводит schtasks.</summary>
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    internal static string Ascii(string s) => new(s.Where(c => c is >= ' ' and <= '~' and not '?').ToArray()); // «?» вместо нечитаемых букв в путях не бывает

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
}
