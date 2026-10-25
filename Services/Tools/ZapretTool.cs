using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Dnex.Services.Tools;

/// <summary>
/// zapret-discord-youtube (github.com/Flowseal/zapret-discord-youtube): обход замедления YouTube и Discord.
/// Внутри winws.exe из zapret (bol-van) и драйвер WinDivert. Стратегии — файлы general*.bat: каждый запускает
/// winws.exe со своим набором параметров. DNEX эти bat-файлы не запускает (они вызывают service.bat, который
/// может ждать ответа в консоли), а берет из них строку запуска winws.exe, подставляет пути и запускает winws
/// сама, без окна. Нужны права администратора: WinDivert работает с сетевыми пакетами.
/// </summary>
public sealed class ZapretTool : Tool
{
    public override string Id => "zapret";
    public override string Title => "YouTube и Discord";
    public override string Repo => "Flowseal/zapret-discord-youtube";
    public override string Authors => "zapret-discord-youtube, авторы bol-van и Flowseal, лицензия MIT; WinDivert, basil00";

    public const string DefaultStrategy = "general.bat";
    /// <summary>Игровой фильтр выключен: service.bat подставляет вместо диапазона портов заглушку 12.</summary>
    private const string GamesOff = "12";
    private const string GamesOn = "1024-65535";

    /// <summary>
    /// Путь без кириллицы и пробелов: winws с ними не работает (так пишет автор сборки), а профиль пользователя
    /// бывает с русским именем. Копия данных для тестов (DNEX_DATA_DIR) держит zapret у себя.
    /// </summary>
    public static string Dir => Environment.GetEnvironmentVariable("DNEX_DATA_DIR") is { Length: > 0 }
        ? Path.Combine(Storage.Dir, "tools", "zapret")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DNEX", "zapret");
    private static string Bin => Path.Combine(Dir, "bin") + Path.DirectorySeparatorChar;
    private static string Lists => Path.Combine(Dir, "lists") + Path.DirectorySeparatorChar;

    public override bool IsInstalled => File.Exists(Path.Combine(Bin, "winws.exe"));

    public override bool IsRunning()
    {
        var list = Process.GetProcessesByName("winws");
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    protected override ReleaseAsset? PickAsset(Release r) =>
        r.Assets.FirstOrDefault(a => a.Name.StartsWith("zapret-discord-youtube", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>Стратегии из установленной сборки: файлы general*.bat, основная первой.</summary>
    public IReadOnlyList<string> Strategies() => !Directory.Exists(Dir) ? Array.Empty<string>() :
        Directory.GetFiles(Dir, "general*.bat").Select(Path.GetFileName).OfType<string>()
            .OrderBy(f => f == DefaultStrategy ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Имя стратегии для списка: «general.bat» — «Основная», «general (ALT2).bat» — «ALT2».</summary>
    public static string StrategyTitle(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var open = name.IndexOf('(');
        return open < 0 ? "Основная" : name[(open + 1)..].TrimEnd(')', ' ');
    }

    /// <summary>
    /// Параметры winws.exe из bat-файла стратегии: строки с «^» в конце склеиваются, берется все после
    /// "%BIN%winws.exe", переменные заменяются путями. Null, если строки запуска в файле нет.
    /// </summary>
    public static string? ParseArgs(string bat, string bin, string lists, bool games)
    {
        var text = bat.Replace("\r\n", "\n").Replace("^\n", " ");
        const string marker = "\"%BIN%winws.exe\"";
        var line = text.Split('\n').FirstOrDefault(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (line == null) return null;
        var args = line[(line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Trim();
        args = UnescapeCmd(args);
        var range = games ? GamesOn : GamesOff;
        return args.Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase)
                   .Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase)
                   .Replace("%GameFilterTCP%", range, StringComparison.OrdinalIgnoreCase)
                   .Replace("%GameFilterUDP%", range, StringComparison.OrdinalIgnoreCase)
                   .Replace("%GameFilter%", range, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Как cmd читает строку: вне кавычек «^» экранирует следующий символ («^!» дает «!»).</summary>
    private static string UnescapeCmd(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') quoted = !quoted;
            else if (c == '^' && !quoted && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Пользовательские списки, на которые ссылаются стратегии. Без них winws не стартует; содержимое как у service.bat.</summary>
    private static void EnsureUserLists()
    {
        Directory.CreateDirectory(Lists);
        void Make(string name, string content) { var p = Path.Combine(Lists, name); if (!File.Exists(p)) File.WriteAllText(p, content); }
        Make("ipset-exclude-user.txt", "203.0.113.113/32\r\n");
        Make("list-general-user.txt", "# Never leave this file empty\r\ndomain.example.abc\r\n");
        Make("list-exclude-user.txt", "domain.example.abc\r\n");
    }

    /// <summary>Служба zapret, поставленная через service.bat: тогда обходом уже управляет она, две копии мешают друг другу.</summary>
    private static bool ForeignServiceInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "query zapret")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    public override async Task StartAsync()
    {
        if (IsRunning())
        {
            if (State.Enabled) return; // уже работает наша копия (например, после перезапуска DNEX)
            throw new ToolException("winws.exe уже запущен другой программой или вручную. Закройте его, чтобы DNEX управляла обходом сама");
        }
        if (ForeignServiceInstalled())
            throw new ToolException("Установлена служба zapret из service.bat. Удалите ее там (Remove Services), чтобы DNEX управляла обходом сама");
        if (!IsInstalled) throw new ToolException("zapret не установлен");

        var strategy = State.Strategy is { } s && File.Exists(Path.Combine(Dir, s)) ? s : DefaultStrategy;
        var args = ParseArgs(await File.ReadAllTextAsync(Path.Combine(Dir, strategy)), Bin, Lists, State.Games)
                   ?? throw new ToolException($"В стратегии {strategy} не нашлась строка запуска winws.exe. Возможно, формат сборки изменился");
        EnsureUserLists();

        var exe = Path.Combine(Bin, "winws.exe");
        var psi = DnsSwitcher.IsAdmin()
            ? new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Bin }
            : new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Bin };
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { throw new ToolException("Права администратора не выданы: без них zapret не работает"); }
        catch (Exception e) { throw new ToolException("Не удалось запустить winws.exe: " + e.Message + ". Возможно, антивирус удалил файлы WinDivert"); }
        using (proc)
        {
            await Task.Delay(2000);
            if (proc == null || proc.HasExited)
                throw new ToolException($"winws.exe сразу завершился (код {proc?.ExitCode}). Частая причина: антивирус удалил WinDivert. Добавьте папку {Dir} в исключения");
        }
    }

    public override async Task StopAsync()
    {
        if (!IsRunning()) return;
        // Драйвер WinDivert тоже останавливаем: иначе при обновлении его файл занят
        await DnsSwitcher.RunPsAsync(
            "Get-Process winws -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500; " +
            "& \"$env:SystemRoot\\System32\\sc.exe\" stop WinDivert | Out-Null", elevated: true);
        if (IsRunning()) throw new ToolException("winws.exe не остановился: права администратора не выданы");
    }

    /// <summary>
    /// Ставит архив: распаковка во временную папку, затем замена папки zapret. Свои списки пользователя
    /// (lists\*-user.txt) переносятся в новую версию.
    /// </summary>
    protected override async Task PlaceAsync(string file, IProgress<string> progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "DNEX-tools", "zapret-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(file, temp), ct);
            // В архиве одна папка верхнего уровня (zapret-discord-youtube-1.10.4), внутри bin, lists и стратегии
            var root = Directory.GetDirectories(temp).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "bin")))
                       ?? (Directory.Exists(Path.Combine(temp, "bin")) ? temp : throw new ToolException("В архиве нет папки bin: сборка не похожа на zapret"));

            var keep = new Dictionary<string, byte[]>();
            if (Directory.Exists(Lists))
                foreach (var f in Directory.GetFiles(Lists, "*-user.txt"))
                    keep[Path.GetFileName(f)] = await File.ReadAllBytesAsync(f, ct);

            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
                CopyDir(root, Dir);
            }
            catch (UnauthorizedAccessException)
            {
                throw new ToolException($"Нет прав на папку {Dir}. Скорее всего, прошлую версию ставила DNEX с правами администратора: запустите DNEX от администратора и повторите");
            }
            catch (IOException e) { throw new ToolException("Не удалось заменить файлы zapret: " + e.Message); }

            Directory.CreateDirectory(Lists);
            foreach (var (name, data) in keep) await File.WriteAllBytesAsync(Path.Combine(Lists, name), data, ct);
            EnsureUserLists();
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch (Exception) { } }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
