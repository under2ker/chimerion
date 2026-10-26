using System.Runtime.CompilerServices;

namespace Dnex.Tests;

/// <summary>
/// До первого обращения к Storage: тесты работают со своей временной папкой данных, а не с %AppData%\DNEX
/// настоящей программы. Сценарии подключения и обновления пишут туда active.json, резервные копии DNS и отметки
/// обновления, и они не должны попасть в данные пользователя. Автовосстановление подключения тоже выключено.
/// </summary>
internal static class TestSetup
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "chimerion-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        // Папки прошлых прогонов (по одной на процесс) убираются, чтобы не копились во временной папке
        foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "chimerion-tests-*"))
            if (d != DataDir && Directory.GetLastWriteTime(d) < DateTime.Now.AddHours(-1))
                try { Directory.Delete(d, true); } catch (Exception) { }
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("DNEX_DATA_DIR", DataDir);
        Environment.SetEnvironmentVariable("DNEX_NO_AUTORESTORE", "1");
    }
}
