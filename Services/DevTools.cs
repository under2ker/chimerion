using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>
/// Режим для разработчиков. Инструменты (Cursor, Copilot, Windsurf, JetBrains AI) работают через DNS Windows и
/// открываются наборами каталога. Отдельная история — WSL: Linux внутри Windows ходит в DNS через свой посредник,
/// и без DNS-туннеля (dnsTunneling) запросы из WSL идут мимо правил NRPT. Тогда в «Двух DNS» Claude Code, aider
/// и pip внутри WSL видят обычные адреса и получают «регион не поддерживается».
/// Начиная с WSL 2.2 туннель включен по умолчанию; выключить его может строка dnsTunneling=false в .wslconfig.
/// </summary>
public static class DevTools
{
    /// <summary>Наборы каталога для разработчиков: ИИ для кода, IDE (Cursor, Windsurf, JetBrains, VS Code), прочие LLM.</summary>
    public static readonly string[] Groups = { "ai-code", "ide", "llm" };

    public enum WslState
    {
        /// <summary>WSL не установлен или в нем нет ни одного дистрибутива.</summary>
        None,
        /// <summary>DNS-туннель включен: запросы из WSL подчиняются DNS Windows и правилам NRPT.</summary>
        Ok,
        /// <summary>Туннель выключен строкой в .wslconfig.</summary>
        TunnelOff,
        /// <summary>WSL старше 2.2: туннель по умолчанию выключен.</summary>
        Old,
    }

    public sealed record WslInfo(WslState State, int Distros, Version? Version, string ConfigPath);

    /// <summary>Тесты подменяют путь к .wslconfig и источники данных.</summary>
    internal static Func<string> ConfigPath = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
    internal static Func<int> DistroCount = CountDistros;
    internal static Func<Version?> WslVersion = ReadWslVersion;

    /// <summary>Дистрибутивы WSL текущего пользователя (из реестра, без запуска WSL).</summary>
    private static int CountDistros()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return k?.GetSubKeyNames().Count(n => n.StartsWith('{')) ?? 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Версия WSL из «wsl --version». Вывод в UTF-16; на старых сборках команды нет, тогда null.</summary>
    private static Version? ReadWslVersion()
    {
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(exe)) return null;
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode,
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (Exception) { } return null; }
            return ParseVersion(output);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Первая версия вида 2.3.26 в выводе «wsl --version» (строка «WSL version» на любом языке идет первой).</summary>
    internal static Version? ParseVersion(string output)
    {
        var m = Regex.Match(output, @"\b(\d+)\.(\d+)\.(\d+)(?:\.\d+)?\b");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    /// <summary>Значение dnsTunneling из раздела [wsl2]: true, false или null (не задано).</summary>
    internal static bool? TunnelSetting(string? config)
    {
        if (string.IsNullOrEmpty(config)) return null;
        bool inWsl2 = false;
        bool? value = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('[')) { inWsl2 = line.Equals("[wsl2]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inWsl2) continue;
            var kv = line.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("dnsTunneling", StringComparison.OrdinalIgnoreCase))
                value = kv[1].Trim().Split('#')[0].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return value;
    }

    public static WslInfo Check()
    {
        var path = ConfigPath();
        int distros = DistroCount();
        if (distros == 0) return new WslInfo(WslState.None, 0, null, path);
        string? config = null;
        try { if (File.Exists(path)) config = File.ReadAllText(path); } catch (Exception) { }
        var tunnel = TunnelSetting(config);
        var version = WslVersion();
        var state = tunnel == false ? WslState.TunnelOff
            : tunnel == true ? WslState.Ok
            : version != null && version < new Version(2, 2, 0) ? WslState.Old
            : WslState.Ok;
        return new WslInfo(state, distros, version, path);
    }

    /// <summary>
    /// Включить DNS-туннель: dnsTunneling=true в разделе [wsl2] файла .wslconfig. Прежний файл сохраняется рядом
    /// (.wslconfig.chimerion.bak). Остальные строки не трогаются. Подействует после «wsl --shutdown».
    /// </summary>
    public static void EnableTunnel()
    {
        var path = ConfigPath();
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (text.Length > 0) File.Copy(path, path + ".chimerion.bak", overwrite: true);
        File.WriteAllText(path, WithTunnel(text), new UTF8Encoding(false));
    }

    /// <summary>Текст .wslconfig с dnsTunneling=true в разделе [wsl2]: заменяет строку, если есть, иначе добавляет.</summary>
    internal static string WithTunnel(string config)
    {
        var nl = config.Contains("\r\n") ? "\r\n" : "\n";
        var lines = config.Length == 0 ? new List<string>() : config.Replace("\r\n", "\n").Split('\n').ToList();
        int section = lines.FindIndex(l => l.Trim().Equals("[wsl2]", StringComparison.OrdinalIgnoreCase));
        if (section < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[wsl2]");
            lines.Add("dnsTunneling=true");
            return string.Join(nl, lines) + nl;
        }
        int end = lines.FindIndex(section + 1, l => l.Trim().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (int i = section + 1; i < end; i++)
        {
            var kv = lines[i].Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("dnsTunneling", StringComparison.OrdinalIgnoreCase) && !lines[i].TrimStart().StartsWith('#'))
            {
                lines[i] = "dnsTunneling=true";
                return string.Join(nl, lines);
            }
        }
        lines.Insert(section + 1, "dnsTunneling=true");
        return string.Join(nl, lines);
    }

    public static string Describe(WslInfo w) => w.State switch
    {
        WslState.Ok => $"DNS-туннель включен: запросы из WSL идут через DNS Windows и правила «Двух DNS»{(w.Version != null ? $" (WSL {w.Version})" : "")}",
        WslState.TunnelOff => "В .wslconfig выключен DNS-туннель (dnsTunneling=false): в «Двух DNS» запросы из WSL идут мимо Smart DNS, и Claude Code, aider, pip внутри WSL получают «регион не поддерживается»",
        WslState.Old => $"WSL {w.Version}: в версиях до 2.2 DNS-туннель выключен по умолчанию, и в «Двух DNS» запросы из WSL идут мимо Smart DNS. Включите туннель или обновите WSL командой «wsl --update»",
        _ => "WSL не установлен",
    };
}
