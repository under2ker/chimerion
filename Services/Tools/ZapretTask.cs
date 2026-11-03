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
/// Задание запускает не программу с рабочего стола и не winws из ProgramData (туда может писать любой процесс без прав,
/// и подмена файла дала бы ему права администратора без вопроса), а их копии в Program Files\CHIMERION\helper,
/// куда пишет только администратор. Копии делаются вместе с созданием задания, когда Windows и так спрашивает права.
/// Запустить задание может текущий пользователь, а изменить его только администратор (дескриптор безопасности в XML).
/// </summary>
public static class ZapretTask
{
    public const string StartName = "CHIMERION zapret start";
    public const string StopName = "CHIMERION zapret stop";
    public const string HelperArg = "--zapret-helper";

    private static string? ExePath => Updater.CurrentExePath;

    /// <summary>Защищенная папка копий: запись только у администраторов (наследуется от Program Files).</summary>
    public static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CHIMERION", "helper");
    public static string HelperExe => Path.Combine(HelperDir, "CHIMERION.exe");
    public static string HelperBin => Path.Combine(HelperDir, "zapret-bin") + Path.DirectorySeparatorChar;

    /// <summary>Эта программа запущена из защищенной копии: значит, ее запустило задание.</summary>
    public static bool IsHelperCopy => Environment.ProcessPath is { } p &&
        string.Equals(Path.GetDirectoryName(p), HelperDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static (DateTime At, bool Value)? _ready;

    /// <summary>
    /// Оба задания есть, запускают защищенную копию, и копия winws совпадает с установленной (иначе после обновления zapret
    /// задание запускало бы старую версию). Ответ запоминается на минуту: schtasks медленный, а спрашивают часто.
    /// </summary>
    public static bool Ready()
    {
        if (_ready is { } r && DateTime.Now - r.At < TimeSpan.FromMinutes(1)) return r.Value;
        bool value = File.Exists(HelperExe) && SameFile(Path.Combine(HelperBin, "winws.exe"), Path.Combine(ZapretTool.BinDir, "winws.exe"))
                     && TaskRuns(StartName, HelperExe, "start") && TaskRuns(StopName, HelperExe, "stop");
        _ready = (DateTime.Now, value);
        return value;
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(File.ReadAllBytes(a)).AsSpan().SequenceEqual(sha.ComputeHash(File.ReadAllBytes(b)));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Файлы программы для копии: одиночный exe, а у сборки разработчика еще ее библиотеки и описания запуска.</summary>
    internal static IEnumerable<string> AppFiles(string exe)
    {
        yield return exe;
        if (!string.IsNullOrEmpty(typeof(ZapretTask).Assembly.Location) && Path.GetDirectoryName(exe) is { } dir)
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CHIMERION.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }
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
        await File.WriteAllTextAsync(start, Xml(HelperExe, "start", user), Encoding.Unicode);
        await File.WriteAllTextAsync(stop, Xml(HelperExe, "stop", user), Encoding.Unicode);
        try
        {
            var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            static string Q(string v) => DnsSwitcher.PsQuote(v);
            // Копии делаются в том же запросе прав: папка в Program Files, запись только у администраторов
            var copy = new StringBuilder();
            copy.Append($"$h = {Q(HelperDir)}; New-Item -ItemType Directory -Force -Path $h | Out-Null; ");
            foreach (var f in AppFiles(exe))
                copy.Append($"Copy-Item -LiteralPath {Q(f)} -Destination (Join-Path $h {Q(Path.GetFileName(f) == Path.GetFileName(exe) ? "CHIMERION.exe" : Path.GetFileName(f))}) -Force; ");
            copy.Append($"Remove-Item -LiteralPath {Q(HelperBin.TrimEnd(Path.DirectorySeparatorChar))} -Recurse -Force -ErrorAction SilentlyContinue; ");
            copy.Append($"Copy-Item -LiteralPath {Q(ZapretTool.BinDir.TrimEnd(Path.DirectorySeparatorChar))} -Destination {Q(HelperBin.TrimEnd(Path.DirectorySeparatorChar))} -Recurse -Force; ");
            // Пути из папки пользователя: имя с апострофом не должно ломать или менять команду
            var script = copy + $"& {Q(schtasks)} /Create /TN {Q(StartName)} /XML {Q(start)} /F | Out-Null; " +
                         $"& {Q(schtasks)} /Create /TN {Q(StopName)} /XML {Q(stop)} /F | Out-Null";
            await DnsSwitcher.RunPsAsync(script, elevated: true);
        }
        finally
        {
            try { File.Delete(start); } catch (IOException) { }
            try { File.Delete(stop); } catch (IOException) { }
            _ready = null;
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
