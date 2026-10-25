using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Dnex.Services;

/// <summary>Обновление не установилось. Message — понятная причина для окна обновления.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Обновление внутри программы. Из релиза на GitHub скачиваются архив DNEX-x.y.z-win-x64.zip и файл
/// с его контрольной суммой SHA256-pc-x.y.z.txt. Архив ставится, только если сумма совпала и внутри
/// программа нужной версии. Запущенный exe Windows удалить не дает, но дает переименовать: текущий файл
/// становится DNEX.old.exe, новый встает на его место под тем же именем. Путь не меняется, поэтому
/// автозапуск и ярлыки продолжают работать. Новая версия начинает работать после перезапуска,
/// старый файл удаляется при следующем запуске.
/// </summary>
public static class Updater
{
    public const string AfterUpdateArg = "--after-update";

    /// <summary>Версия, уже установленная в этом сеансе и ждущая перезапуска.</summary>
    public static Version? Installed { get; private set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Файлы релиза отдаются через перенаправление на хранилище GitHub: переходы разрешены
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DNEX-updater/1.0");
        return c;
    }

    private static string ExePath => Environment.ProcessPath ?? throw new UpdateException("Не удалось определить, где лежит программа");
    private static string Dir => Path.GetDirectoryName(ExePath)!;
    private static string Name => Path.GetFileNameWithoutExtension(ExePath);
    private static string OldPath => Path.Combine(Dir, Name + ".old.exe");
    private static string NewPath => Path.Combine(Dir, Name + ".new.exe");
    private static string TempDir => Path.Combine(Path.GetTempPath(), "DNEX-update");

    public static string ZipUrl(Version v) =>
        $"https://github.com/{UpdateChecker.Repo}/releases/download/v{v.ToString(3)}/DNEX-{v.ToString(3)}-win-x64.zip";

    public static string ShaUrl(Version v) =>
        $"https://github.com/{UpdateChecker.Repo}/releases/download/v{v.ToString(3)}/SHA256-pc-{v.ToString(3)}.txt";

    /// <summary>Запущена после установки обновления.</summary>
    public static bool StartedAfterUpdate =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(AfterUpdateArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Убирает старый файл программы и скачанный архив, оставшиеся от прошлого обновления.</summary>
    public static void Cleanup()
    {
        try { if (Environment.ProcessPath != null && File.Exists(OldPath)) File.Delete(OldPath); } catch (Exception) { }
        try { if (Environment.ProcessPath != null && File.Exists(NewPath)) File.Delete(NewPath); } catch (Exception) { }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch (Exception) { }
    }

    /// <summary>
    /// Скачивает и ставит версию v. progress — доля от 0 до 1 (null, если размер неизвестен) и текст этапа.
    /// После успеха нужен перезапуск (Restart).
    /// </summary>
    public static async Task InstallAsync(Version v, IProgress<(double? Part, string Stage)> progress, CancellationToken ct)
    {
        var ver = v.ToString(3);
        Directory.CreateDirectory(TempDir);
        var zipPath = Path.Combine(TempDir, $"DNEX-{ver}-win-x64.zip");

        // 1. Контрольная сумма: файл SHA256-pc-x.y.z.txt, строка «HEX  имя_архива»
        progress.Report((null, "Получаю контрольную сумму…"));
        string expected;
        try
        {
            var text = await Http.GetStringAsync(ShaUrl(v), ct);
            expected = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось получить контрольную сумму с GitHub: " + Short(e)); }
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new UpdateException("Файл контрольной суммы в релизе не читается");

        // 2. Архив с прогрессом
        try
        {
            using var resp = await Http.GetAsync(ZipUrl(v), HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress.Report((total > 0 ? (double)done / total.Value : null, $"Скачиваю: {done / 1024} из {(total > 0 ? total.Value / 1024 + " КБ" : "? КБ")}"));
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось скачать архив: " + Short(e)); }

        // 3. Проверка суммы: поврежденный или подмененный по дороге архив не ставится
        progress.Report((1, "Проверяю архив…"));
        string actual;
        await using (var fs = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (actual != expected)
            throw new UpdateException("Контрольная сумма архива не совпала. Архив не установлен, программа не изменена");

        // 4. Распаковка рядом с программой (тот же диск: переименование мгновенное и не ломается посередине)
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("DNEX.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new UpdateException("В архиве нет DNEX.exe");
            entry.ExtractToFile(NewPath, overwrite: true);
        }
        catch (UnauthorizedAccessException) { throw new UpdateException($"Нет прав на запись в папку программы ({Dir}). Скачайте версию вручную со страницы релиза"); }
        catch (InvalidDataException) { throw new UpdateException("Архив поврежден"); }

        var fileVer = FileVersionInfo.GetVersionInfo(NewPath).ProductVersion ?? "";
        if (!fileVer.StartsWith(ver, StringComparison.Ordinal))
        {
            TryDelete(NewPath);
            throw new UpdateException($"В архиве программа другой версии ({fileVer}), установка отменена");
        }

        // 5. Замена: текущий файл в DNEX.old.exe, новый на его место. Если второй шаг не удался, возвращаем старый
        progress.Report((1, "Устанавливаю…"));
        try
        {
            File.Move(ExePath, OldPath, overwrite: true);
            try { File.Move(NewPath, ExePath); }
            catch { File.Move(OldPath, ExePath); throw; }
        }
        catch (UnauthorizedAccessException) { TryDelete(NewPath); throw new UpdateException($"Нет прав на запись в папку программы ({Dir}). Скачайте версию вручную со страницы релиза"); }
        catch (IOException e) { TryDelete(NewPath); throw new UpdateException("Не удалось заменить файл программы: " + e.Message); }
        TryDelete(zipPath);
        Installed = v;
    }

    /// <summary>Запускает новую версию. Она дождется, пока эта копия закроется (см. App.OnStartup).</summary>
    public static void Restart() =>
        Process.Start(new ProcessStartInfo(ExePath, AfterUpdateArg) { UseShellExecute = false, WorkingDirectory = Dir });

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    private static string Short(Exception e) => e.GetBaseException() switch
    {
        HttpRequestException h when h.StatusCode == System.Net.HttpStatusCode.NotFound => "файла нет в релизе",
        HttpRequestException h when h.StatusCode != null => $"сервер ответил {(int)h.StatusCode}",
        TaskCanceledException or TimeoutException => "нет ответа",
        _ => "нет соединения",
    };
}
