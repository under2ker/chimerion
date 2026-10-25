using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace Dnex.Services;

/// <summary>Обновление не установилось. Message — понятная причина для окна обновления.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Обновление внутри программы. Из релиза на GitHub скачиваются архив CHIMERION-x.y.z-win-x64.zip и файл
/// с его контрольной суммой SHA256-pc-x.y.z.txt. Архив ставится, только если сумма совпала и внутри
/// программа нужной версии. Запущенный exe Windows удалить не дает, но дает переименовать: текущий файл
/// становится CHIMERION.old.exe, новый встает на его место под тем же именем. Путь не меняется, поэтому
/// автозапуск и ярлыки продолжают работать. Новая версия начинает работать после перезапуска.
///
/// Подпись: архив подписан ключом автора (ECDSA P-256), открытая часть ключа встроена в программу. Сумма SHA-256
/// лежит в том же релизе и защищает только от поврежденного архива, а подпись еще и от подмены релиза целиком.
///
/// Откат: пока новая версия не проработала 30 секунд или не закрылась штатно, обновление считается
/// неподтвержденным (файл update-pending.json). Если она трижды подряд не дошла до подтверждения (падает
/// при запуске), при следующем запуске возвращается прежняя версия. Прежняя версия хранится как CHIMERION.prev.exe
/// для ручного возврата из настроек.
/// </summary>
public static class Updater
{
    public const string AfterUpdateArg = "--after-update";
    public const string RolledBackArg = "--rolled-back=";
    public const string RelaunchArg = "--relaunch";

    /// <summary>Открытый ключ подписи релизов (SubjectPublicKeyInfo, ECDSA P-256). Секретная часть только у автора.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGMeOOFlFlTXLz26x62wAzDxaknOR9wH+ICzlyHrgjT/riHsbB6RVL+sk/Ds5qNxl4aYUGMx0GUbs9V4wASPb0g==";

    /// <summary>Сколько запусков подряд без подтверждения терпим, прежде чем вернуть старую версию.</summary>
    private const int MaxUnconfirmedStarts = 3;

    /// <summary>Версия, уже установленная в этом сеансе и ждущая перезапуска.</summary>
    public static Version? Installed { get; private set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // Файлы релиза отдаются через перенаправление на хранилище GitHub: переходы разрешены
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-updater/1.0");
        return c;
    }

    private static string ExePath => Environment.ProcessPath ?? throw new UpdateException("Не удалось определить, где лежит программа");
    private static string Dir => Path.GetDirectoryName(ExePath)!;
    private static string Name => Path.GetFileNameWithoutExtension(ExePath);
    private static string OldPath => Path.Combine(Dir, Name + ".old.exe");
    private static string NewPath => Path.Combine(Dir, Name + ".new.exe");
    private static string PrevPath => Path.Combine(Dir, Name + ".prev.exe");
    private static string BadPath => Path.Combine(Dir, Name + ".bad.exe");
    private static string PendingPath => Storage.PathOf("update-pending.json");

    /// <summary>Неподтвержденное обновление: с какой версии на какую и сколько раз уже запускалась новая.</summary>
    public sealed class Pending
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Starts { get; set; }
    }
    private static string TempDir => Path.Combine(Path.GetTempPath(), "CHIMERION-update");

    /// <summary>
    /// Имя архива в релизе. С 0.1.25 программа называется CHIMERION: в релизе два одинаковых архива,
    /// CHIMERION-x.y.z (внутри CHIMERION.exe) и DNEX-x.y.z (внутри DNEX.exe) для уже установленных версий DNEX,
    /// которые ищут архив и файл со старым именем. Старые релизы (до 0.1.25) есть только с именем DNEX.
    /// </summary>
    public static string ZipName(Version v, bool legacy = false) => $"{(legacy ? "DNEX" : "CHIMERION")}-{v.ToString(3)}-win-x64.zip";

    public static string ZipUrl(Version v, bool legacy = false) =>
        $"https://github.com/{UpdateChecker.Repo}/releases/download/v{v.ToString(3)}/{ZipName(v, legacy)}";

    /// <summary>
    /// Сумма нужного архива из файла SHA256-pc-x.y.z.txt (строки «HEX  имя»). Первая строка — архив DNEX для старых
    /// версий (они берут первую сумму), поэтому свой архив ищем по имени. Если строки CHIMERION нет (релиз до
    /// переименования), берется архив DNEX.
    /// </summary>
    public static (string Hash, bool Legacy) PickSum(string shaFile, Version v)
    {
        string? Find(string name) => shaFile.Split('\n').Select(l => l.Trim())
            .Where(l => l.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant()).FirstOrDefault();
        if (Find(ZipName(v)) is { } h) return (h, false);
        if (Find(ZipName(v, legacy: true)) is { } l) return (l, true);
        return (shaFile.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "", true);
    }

    public static string ShaUrl(Version v) =>
        $"https://github.com/{UpdateChecker.Repo}/releases/download/v{v.ToString(3)}/SHA256-pc-{v.ToString(3)}.txt";

    public static string SigUrl(Version v, bool legacy = false) => ZipUrl(v, legacy) + ".sig";

    /// <summary>Запущена после установки обновления.</summary>
    public static bool StartedAfterUpdate =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(AfterUpdateArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Запущена после отката: версия, которая не запустилась. Null, если отката не было.</summary>
    public static string? RolledBackFrom =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(RolledBackArg, StringComparison.OrdinalIgnoreCase))?[RolledBackArg.Length..];

    /// <summary>Копия запущена этой же программой (обновление, откат) и должна дождаться, пока прежняя закроется.</summary>
    public static bool StartedByRestart => StartedAfterUpdate || RolledBackFrom != null ||
        Environment.GetCommandLineArgs().Any(a => a.Equals(RelaunchArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Проверка подписи архива открытым ключом. publicKey задается в тестах.</summary>
    public static bool VerifySignature(byte[] data, string signatureBase64, string publicKey = PublicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>
    /// При запуске, до окна: убирает остатки скачивания и, если новая версия уже трижды не дошла до подтверждения,
    /// возвращает прежнюю. True: откат сделан и прежняя версия запущена, эту копию надо закрыть.
    /// </summary>
    public static bool OnStartup()
    {
        if (Environment.ProcessPath == null) return false;
        try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch (Exception) { }
        try { if (File.Exists(BadPath)) File.Delete(BadPath); } catch (Exception) { }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch (Exception) { }
        var p = LoadPending();
        if (p == null) return false;
        if (p.To != UpdateChecker.CurrentVersion.ToString(3)) { DeletePending(); return false; } // это уже не та версия
        p.Starts++;
        if (p.Starts <= MaxUnconfirmedStarts || !File.Exists(OldPath)) { SavePending(p); return false; }
        // Новая версия не запускается: возвращаем прежнюю, неудачную откладываем в .bad.exe (удалится при следующем запуске)
        try
        {
            File.Move(ExePath, BadPath, overwrite: true);
            File.Move(OldPath, ExePath);
            DeletePending();
            Process.Start(new ProcessStartInfo(ExePath, RolledBackArg + p.To) { UseShellExecute = false, WorkingDirectory = Dir });
            return true;
        }
        catch (Exception) { SavePending(p); return false; }
    }

    /// <summary>
    /// Новая версия работает: проработала 30 секунд или закрылась штатно. Отметка убирается, прежняя версия
    /// остается как CHIMERION.prev.exe для ручного возврата.
    /// </summary>
    public static void ConfirmStarted()
    {
        if (Environment.ProcessPath == null) return;
        DeletePending();
        try { if (File.Exists(OldPath)) File.Move(OldPath, PrevPath, overwrite: true); } catch (Exception) { }
    }

    /// <summary>Прежняя версия для ручного возврата: номер из файла CHIMERION.prev.exe. Null, если возвращать не на что.</summary>
    public static Version? PreviousVersion()
    {
        try
        {
            if (Environment.ProcessPath == null || !File.Exists(PrevPath)) return null;
            var pv = FileVersionInfo.GetVersionInfo(PrevPath).ProductVersion ?? "";
            var plus = pv.IndexOf('+');
            return Version.TryParse(plus > 0 ? pv[..plus] : pv, out var v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Ручной возврат прежней версии: файлы меняются местами (текущая становится CHIMERION.prev.exe, вперед можно
    /// вернуться тем же способом). После этого нужен перезапуск (Restart).
    /// </summary>
    public static void SwapWithPrevious()
    {
        if (!File.Exists(PrevPath)) throw new UpdateException("Прежней версии нет");
        var tmp = Path.Combine(Dir, Name + ".swap.exe");
        try
        {
            File.Move(ExePath, tmp, overwrite: true);
            File.Move(PrevPath, ExePath);
            File.Move(tmp, PrevPath);
        }
        catch (UnauthorizedAccessException) { throw new UpdateException($"Нет прав на запись в папку программы ({Dir})"); }
        catch (IOException e) { throw new UpdateException("Не удалось поменять файлы: " + e.Message); }
        DeletePending();
    }

    private static Pending? LoadPending()
    {
        try { return File.Exists(PendingPath) ? System.Text.Json.JsonSerializer.Deserialize<Pending>(File.ReadAllText(PendingPath)) : null; }
        catch (Exception) { return null; }
    }

    private static void SavePending(Pending p)
    {
        try { File.WriteAllText(PendingPath, System.Text.Json.JsonSerializer.Serialize(p)); } catch (Exception) { }
    }

    private static void DeletePending() { try { File.Delete(PendingPath); } catch (Exception) { } }

    /// <summary>
    /// Скачивает и ставит версию v. progress — доля от 0 до 1 (null, если размер неизвестен) и текст этапа.
    /// После успеха нужен перезапуск (Restart).
    /// </summary>
    public static async Task InstallAsync(Version v, IProgress<(double? Part, string Stage)> progress, CancellationToken ct)
    {
        var ver = v.ToString(3);
        Directory.CreateDirectory(TempDir);
        var zipPath = Path.Combine(TempDir, ZipName(v));
        bool legacy;

        // 1. Контрольная сумма: файл SHA256-pc-x.y.z.txt, строка «HEX  имя_архива»
        progress.Report((null, "Получаю контрольную сумму…"));
        string expected;
        try
        {
            var text = await Http.GetStringAsync(ShaUrl(v), ct);
            (expected, legacy) = PickSum(text, v);
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось получить контрольную сумму с GitHub: " + Short(e)); }
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new UpdateException("Файл контрольной суммы в релизе не читается");

        // 2. Архив с прогрессом
        try
        {
            using var resp = await Http.GetAsync(ZipUrl(v, legacy), HttpCompletionOption.ResponseHeadersRead, ct);
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

        // 3а. Подпись автора: без нее архив не ставится, даже если сумма совпала
        progress.Report((1, "Проверяю подпись…"));
        string sig;
        try { sig = await Http.GetStringAsync(SigUrl(v, legacy), ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось получить подпись релиза: " + Short(e)); }
        if (!VerifySignature(await File.ReadAllBytesAsync(zipPath, ct), sig))
            throw new UpdateException("Подпись архива не прошла проверку. Архив не установлен, программа не изменена");

        // 4. Распаковка рядом с программой (тот же диск: переименование мгновенное и не ломается посередине)
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // Внутри CHIMERION.exe или DNEX.exe (архив для старых версий): ставится на место текущего файла под его именем
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("CHIMERION.exe", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e => e.Name.Equals("DNEX.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new UpdateException("В архиве нет CHIMERION.exe");
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

        // 5. Замена: текущий файл в CHIMERION.old.exe, новый на его место. Если второй шаг не удался, возвращаем старый
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
        SavePending(new Pending { From = UpdateChecker.CurrentVersion.ToString(3), To = ver });
    }

    /// <summary>Запускает программу заново (новую или возвращенную версию). Она дождется, пока эта копия закроется (см. App.OnStartup).</summary>
    public static void Restart(bool afterUpdate = true) =>
        Process.Start(new ProcessStartInfo(ExePath, afterUpdate ? AfterUpdateArg : RelaunchArg) { UseShellExecute = false, WorkingDirectory = Dir });

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    private static string Short(Exception e) => e.GetBaseException() switch
    {
        HttpRequestException h when h.StatusCode == System.Net.HttpStatusCode.NotFound => "файла нет в релизе",
        HttpRequestException h when h.StatusCode != null => $"сервер ответил {(int)h.StatusCode}",
        TaskCanceledException or TimeoutException => "нет ответа",
        _ => "нет соединения",
    };
}
