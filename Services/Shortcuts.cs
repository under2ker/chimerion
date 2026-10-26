using System.IO;

namespace Dnex.Services;

/// <summary>
/// Файл программы сменил имя или место (DNEX.exe → CHIMERION.exe при обновлении): ярлыки на рабочем столе,
/// в меню «Пуск» и на панели задач, а также автозапуск переводятся на новый файл, чтобы не остались битыми.
/// Трогаются только ярлыки, которые указывали ровно на прежний файл.
/// </summary>
public static class Shortcuts
{
    /// <summary>Папки с ярлыками пользователя. В тестах подменяется.</summary>
    internal static Func<IEnumerable<string>> Folders = () => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
    };

    public static void Retarget(string from, string to)
    {
        try { AutoStart.Retarget(from, to); } catch (Exception) { }
        foreach (var dir in Folders().Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
        {
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch (Exception) { continue; }
            foreach (var lnk in links) RetargetLink(lnk, from, to);
        }
    }

    /// <summary>Ярлык через WScript.Shell (стандартный COM Windows): меняем путь, рабочую папку и значок, если они на прежний файл.</summary>
    private static void RetargetLink(string lnk, string from, string to)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(lnk);
                string target = link.TargetPath;
                if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(from), StringComparison.OrdinalIgnoreCase)) return;
                link.TargetPath = to;
                string icon = link.IconLocation ?? "";
                if (icon.StartsWith(from, StringComparison.OrdinalIgnoreCase)) link.IconLocation = to + icon[from.Length..];
                link.Save();
                // Ярлык назывался по старому имени программы: переименовываем, если имя не занято
                if (Path.GetFileNameWithoutExtension(lnk).Equals(Path.GetFileNameWithoutExtension(from), StringComparison.OrdinalIgnoreCase))
                {
                    var renamed = Path.Combine(Path.GetDirectoryName(lnk)!, Path.GetFileNameWithoutExtension(to) + ".lnk");
                    if (!File.Exists(renamed)) File.Move(lnk, renamed);
                }
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception) { }
    }
}
