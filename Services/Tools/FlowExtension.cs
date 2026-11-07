using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Dnex.Services.Tools;

/// <summary>
/// Расширение Google Flow Geo Bypass (github.com/LZTD1/google-flow-geobypass, MIT): на flow.google.com меняет в ответе
/// сайта флаг доступности страны, IP и аккаунт не трогает. Файлы лежат в программе, CHIMERION распаковывает их в свою
/// папку, а в браузере расширение подключается вручную («Загрузить распакованное»): поставить его без участия человека
/// Chrome и Edge не дают, а адрес chrome://extensions из командной строки открывают как новую вкладку.
/// </summary>
internal static class FlowExtension
{
    public const string SourceUrl = "https://github.com/LZTD1/google-flow-geobypass";
    public const string FlowUrl = "https://flow.google.com/";
    private const string ResourcePrefix = "google-flow/";

    /// <summary>Браузер на движке Chromium: где лежит программа, где профили, страница расширений и где на ней режим разработчика.</summary>
    public sealed record Browser(string Name, string Exe, string UserData, string ExtensionsPage, string DevModeWhere);

    public enum State { NotConnected, Enabled, Disabled }

    // Точки подмены для тестов
    internal static string? FolderOverride;
    internal static Func<IReadOnlyList<Browser>>? BrowsersOverride;

    /// <summary>Папка распакованного расширения. Путь не меняется между версиями: браузер помнит именно его.</summary>
    public static string Folder => FolderOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHIMERION", "google-flow");

    public static bool Prepared => File.Exists(Path.Combine(Folder, "manifest.json"));

    /// <summary>Установленные Chrome, Edge и Яндекс Браузер.</summary>
    public static IReadOnlyList<Browser> Find()
    {
        if (BrowsersOverride != null) return BrowsersOverride();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            local,
        };
        string? Exe(string rel) => roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, rel)).FirstOrDefault(File.Exists);
        var list = new List<Browser>();
        if (Exe(@"Google\Chrome\Application\chrome.exe") is { } chrome)
            list.Add(new("Chrome", chrome, Path.Combine(local, @"Google\Chrome\User Data"), "chrome://extensions", "справа вверху"));
        if (Exe(@"Microsoft\Edge\Application\msedge.exe") is { } edge)
            list.Add(new("Edge", edge, Path.Combine(local, @"Microsoft\Edge\User Data"), "edge://extensions", "слева внизу"));
        if (Exe(@"Yandex\YandexBrowser\Application\browser.exe") is { } yandex)
            list.Add(new("Яндекс Браузер", yandex, Path.Combine(local, @"Yandex\YandexBrowser\User Data"), "browser://extensions", "справа вверху"));
        return list;
    }

    /// <summary>Браузер по умолчанию из списка найденных, иначе первый.</summary>
    public static Browser? Preferred(IReadOnlyList<Browser> found)
    {
        var progId = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            progId = key?.GetValue("ProgId") as string ?? "";
        }
        catch (Exception) { }
        var name = progId.StartsWith("ChromeHTML", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : progId.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : progId.StartsWith("Yandex", StringComparison.OrdinalIgnoreCase) ? "Яндекс Браузер" : null;
        return found.FirstOrDefault(b => b.Name == name) ?? found.FirstOrDefault();
    }

    /// <summary>
    /// Распаковывает файлы расширения. Переписывает только изменившиеся: браузер берет новую версию при следующем
    /// запуске, а неизменные файлы не трогаются. Возвращает true, если что-то записано.
    /// </summary>
    public static bool Prepare()
    {
        var asm = typeof(FlowExtension).Assembly;
        Directory.CreateDirectory(Folder);
        var changed = false;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var path = Path.Combine(Folder, res[ResourcePrefix.Length..]);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) continue;
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            changed = true;
        }
        return changed;
    }

    /// <summary>После обновления CHIMERION подкладывает новую версию расширения, если его уже распаковывали.</summary>
    public static bool RefreshIfPrepared() => Prepared && Prepare();

    /// <summary>Удаляет папку расширения. В браузере его нужно убрать отдельно: удаленная папка там станет ошибкой.</summary>
    public static void Remove()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }

    /// <summary>Подключено ли расширение хотя бы в одном профиле браузера и включено ли оно.</summary>
    public static State StateIn(Browser b)
    {
        var best = State.NotConnected;
        List<string> profiles;
        try
        {
            if (!Directory.Exists(b.UserData)) return best;
            profiles = Directory.EnumerateDirectories(b.UserData)
                .Where(d => Path.GetFileName(d) is var n && (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)))
                .ToList();
        }
        catch (Exception) { return best; }
        foreach (var profile in profiles)
            foreach (var file in new[] { "Secure Preferences", "Preferences" })
            {
                var s = StateInPreferences(Path.Combine(profile, file), Folder);
                if (s == State.Enabled) return s;
                if (s == State.Disabled) best = s;
            }
        return best;
    }

    /// <summary>
    /// Ищет в файле настроек профиля запись расширения с путем нашей папки. Отключенное расширение хранит
    /// непустой disable_reasons (в новых версиях список, в старых число).
    /// </summary>
    internal static State StateInPreferences(string prefsPath, string folder)
    {
        try
        {
            if (!File.Exists(prefsPath)) return State.NotConnected;
            // Браузер держит файл открытым: читаем, разрешая ему писать
            using var fs = new FileStream(prefsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object) return State.NotConnected;
            var want = Normalize(folder);
            foreach (var e in settings.EnumerateObject())
            {
                var v = e.Value;
                if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
                if (!string.Equals(Normalize(p.GetString()!), want, StringComparison.OrdinalIgnoreCase)) continue;
                return Disabled(v) ? State.Disabled : State.Enabled;
            }
        }
        catch (Exception) { }
        return State.NotConnected;
    }

    private static bool Disabled(JsonElement v)
    {
        if (v.TryGetProperty("disable_reasons", out var r))
        {
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) return true;
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var n) && n != 0) return true;
        }
        return v.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var s) && s == 0;
    }

    private static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch (Exception) { }
        return path.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Запускает браузер, с адресом или без. Если CHIMERION работает с правами администратора, браузер запускается через
    /// проводник, как обычная программа (иначе он тоже получил бы права администратора). Проводник не передает аргументы
    /// программе, поэтому адрес идет через ярлык. Возвращает false, если запустить не удалось.
    /// </summary>
    public static bool Launch(Browser b, string? url = null)
    {
        try
        {
            if (DnsSwitcher.IsAdmin())
            {
                var target = url == null ? b.Exe : WriteShortcut(b, url);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                return true;
            }
            var psi = new ProcessStartInfo(b.Exe) { UseShellExecute = false };
            if (url != null) psi.ArgumentList.Add(url);
            Process.Start(psi);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Ярлык «браузер + адрес» рядом с папкой расширения. Перезаписывается при каждом запуске.</summary>
    internal static string WriteShortcut(Browser b, string url)
    {
        var path = Path.Combine(Path.GetDirectoryName(Folder.TrimEnd('\\', '/'))!, "open-in-browser.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try
            {
                link.TargetPath = b.Exe;
                link.Arguments = "\"" + url + "\"";
                link.WorkingDirectory = Path.GetDirectoryName(b.Exe) ?? "";
                link.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        return path;
    }
}
