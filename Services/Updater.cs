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

    // Точки подмены для сценарных тестов: сеть, путь к exe, ключ подписи, запуск процесса и временная папка.
    // В программе всегда значения по умолчанию.
    internal static HttpClient Http = CreateClient();
    internal static string? ExePathOverride;
    internal static string TrustedKey = PublicKey;
    internal static Action<ProcessStartInfo> Launch = psi => Process.Start(psi)?.Dispose();
    internal static string? TempDirOverride;

    private static HttpClient CreateClient()
    {
        // Файлы релиза отдаются через перенаправление на хранилище GitHub: переходы разрешены
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("CHIMERION-updater/1.0");
        return c;
    }

    /// <summary>Файл программы переименован в этом сеансе (DNEX.exe → CHIMERION.exe): процесс работает уже из него.</summary>
    private static string? _movedTo;

    private static string? ProcessPath => ExePathOverride ?? _movedTo ?? Environment.ProcessPath;
    /// <summary>Где сейчас лежит файл программы (с учетом переименования в этом сеансе).</summary>
    public static string? CurrentExePath => ProcessPath;
    private static string ExePath => ProcessPath ?? throw new UpdateException("Не удалось определить, где лежит программа");
    private static string Dir => Path.GetDirectoryName(ExePath)!;
    private static string Name => Path.GetFileNameWithoutExtension(ExePath);
    /// <summary>Имя файла программы. Обновление всегда ставит ее под этим именем, прежний DNEX.exe убирается.</summary>
    public const string AppFile = "CHIMERION.exe";
    private static string TargetPath => Path.Combine(Dir, AppFile);
    private static string NewPath => Path.Combine(Dir, Name + ".new.exe");
    /// <summary>Отложенный прежний файл. Удаляется при следующем запуске (так делают и версии с 0.1.25).</summary>
    private static string StashFor(string exePath) => Path.Combine(Path.GetDirectoryName(exePath)!, Path.GetFileNameWithoutExtension(exePath) + ".bad.exe");
    // Резервная копия прежней версии лежит в папке данных, а не рядом с программой: на рабочем столе остается один файл
    private static string BackupDir => Storage.PathOf("backup");
    private static string PrevBackup => Path.Combine(BackupDir, "previous.exe");
    // Копии, которые клали рядом с программой версии до 0.1.27
    private static string LegacyOld => Path.Combine(Dir, Name + ".old.exe");
    private static string LegacyPrev => Path.Combine(Dir, Name + ".prev.exe");
    private static string PendingPath => Storage.PathOf("update-pending.json");

    /// <summary>Неподтвержденное обновление: с какой версии на какую, сколько раз уже запускалась новая и где лежал прежний файл.</summary>
    public sealed class Pending
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Starts { get; set; }
        /// <summary>Путь прежнего файла (DNEX.exe или CHIMERION.exe). Нет у обновлений, поставленных версиями до 0.1.27.</summary>
        public string? OldPath { get; set; }
    }
    private static string TempDir => TempDirOverride ?? Path.Combine(Path.GetTempPath(), "CHIMERION-update");

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
        if (ProcessPath == null) return false;
        try { if (File.Exists(NewPath)) File.Delete(NewPath); } catch (Exception) { }
        // Отложенные прежние файлы: и под своим именем, и под старым (после переименования DNEX.exe → CHIMERION.exe)
        foreach (var n in new[] { Name, "DNEX", "CHIMERION" }.Distinct(StringComparer.OrdinalIgnoreCase))
            try { var bad = Path.Combine(Dir, n + ".bad.exe"); if (File.Exists(bad)) File.Delete(bad); } catch (Exception) { }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch (Exception) { }
        var p = LoadPending();
        if (p == null) return false;
        if (p.To != UpdateChecker.CurrentVersion.ToString(3)) { DeletePending(); return false; } // это уже не та версия
        p.Starts++;
        // Откуда возвращать: копия в папке данных (с 0.1.27) или файл .old.exe рядом с программой (обновление старой версией)
        var restoreTo = p.OldPath ?? ExePath;
        var source = p.OldPath != null ? PrevBackup : LegacyOld;
        if (p.Starts <= MaxUnconfirmedStarts || !File.Exists(source)) { SavePending(p); return false; }
        // Новая версия не запускается: возвращаем прежнюю. Неудачную откладываем под именем, которое прежняя версия
        // сама удалит при запуске (<имя>.bad.exe)
        try
        {
            File.Move(ExePath, StashFor(restoreTo), overwrite: true);
            Hide(StashFor(restoreTo));
            if (p.OldPath != null) File.Copy(source, restoreTo, overwrite: true);
            else File.Move(source, restoreTo, overwrite: true);
            if (!restoreTo.Equals(ExePath, StringComparison.OrdinalIgnoreCase)) Shortcuts.Retarget(ExePath, restoreTo);
            DeletePending();
            Launch(new ProcessStartInfo(restoreTo, RolledBackArg + p.To) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restoreTo)! });
            return true;
        }
        catch (Exception) { SavePending(p); return false; }
    }

    /// <summary>
    /// Новая версия работает: проработала 30 секунд или закрылась штатно. Отметка убирается. Копии, которые прежние
    /// версии оставляли рядом с программой (.old.exe, .prev.exe), уходят в папку данных, а файл DNEX.exe
    /// переименовывается в CHIMERION.exe: на рабочем столе остается одна программа с новым именем.
    /// </summary>
    public static void ConfirmStarted()
    {
        if (ProcessPath == null) return;
        DeletePending();
        try
        {
            Directory.CreateDirectory(BackupDir);
            // Сначала .prev, потом .old: более новая прежняя версия (.old) остается последней
            foreach (var suffix in new[] { ".prev.exe", ".old.exe" })
                foreach (var n in new[] { Name, "DNEX", "CHIMERION" }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var f = Path.Combine(Dir, n + suffix);
                    if (File.Exists(f)) File.Move(f, PrevBackup, overwrite: true);
                }
        }
        catch (Exception) { }
        RenameToAppFile();
    }

    /// <summary>
    /// Программа лежит под старым именем DNEX.exe: файл переименовывается в CHIMERION.exe (Windows разрешает
    /// переименовать запущенный exe), ярлыки и автозапуск переводятся на новое имя. Если CHIMERION.exe рядом уже
    /// есть, ничего не трогаем: это другая копия.
    /// </summary>
    internal static void RenameToAppFile()
    {
        try
        {
            if (!Name.Equals("DNEX", StringComparison.OrdinalIgnoreCase) || File.Exists(TargetPath)) return;
            var from = ExePath;
            var to = TargetPath;
            File.Move(from, to);
            _movedTo = to;
            Shortcuts.Retarget(from, to);
        }
        catch (Exception) { }
    }

    /// <summary>Прежняя версия для ручного возврата: номер из резервной копии. Null, если возвращать не на что.</summary>
    public static Version? PreviousVersion()
    {
        try
        {
            if (ProcessPath == null) return null;
            var prev = File.Exists(PrevBackup) ? PrevBackup : File.Exists(LegacyPrev) ? LegacyPrev : null;
            if (prev == null) return null;
            var pv = FileVersionInfo.GetVersionInfo(prev).ProductVersion ?? "";
            var plus = pv.IndexOf('+');
            return Version.TryParse(plus > 0 ? pv[..plus] : pv, out var v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Ручной возврат прежней версии: она встает на место программы, а текущая уходит в резервную копию (вперед можно
    /// вернуться тем же способом). Запущенный файл откладывается под именем, которое удалит следующий запуск.
    /// После этого нужен перезапуск (Restart).
    /// </summary>
    public static void SwapWithPrevious()
    {
        var prev = File.Exists(PrevBackup) ? PrevBackup : File.Exists(LegacyPrev) ? LegacyPrev : throw new UpdateException("Прежней версии нет");
        var swap = Path.Combine(BackupDir, "swap.exe");
        var stash = StashFor(ExePath);
        try
        {
            Directory.CreateDirectory(BackupDir);
            File.Copy(ExePath, swap, overwrite: true); // запущенный файл можно читать
            File.Move(ExePath, stash, overwrite: true);
            Hide(stash);
            try { File.Copy(prev, ExePath, overwrite: true); }
            catch { File.Move(stash, ExePath); throw; }
            File.Move(swap, PrevBackup, overwrite: true);
            if (prev == LegacyPrev) TryDelete(LegacyPrev);
        }
        catch (UnauthorizedAccessException) { throw new UpdateException($"Нет прав на запись в папку программы ({Dir})"); }
        catch (IOException e) { throw new UpdateException("Не удалось поменять файлы: " + e.Message); }
        DeletePending();
    }

    private static void Hide(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); } catch (Exception) { }
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
    public static async Task InstallAsync(Version v, IProgress<(double? Part, string Stage)> progress, CancellationToken ct,
                                          HotfixChannel.Manifest? hotfix = null, IProgress<(long Done, long? Total)>? bytes = null)
    {
        var ver = v.ToString(3);
        Directory.CreateDirectory(TempDir);
        var zipPath = Path.Combine(TempDir, ZipName(v));
        bool legacy;

        // 1. Контрольная сумма: файл SHA256-pc-x.y.z.txt, строка «HEX  имя_архива». GitHub бывает недоступен или
        //    замедлен: тогда файлы берутся через зеркала (GitHubMirrors). Это безопасно: архив ставится только с подписью автора
        progress.Report((null, "Получаю контрольную сумму…"));
        string expected;
        if (hotfix != null)
        {
            // Быстрое исправление из R2: сумма в подписанном манифесте, архив и подпись лежат рядом в бакете
            if (hotfix.ParsedVersion != v) throw new UpdateException("Манифест быстрого обновления не про эту версию");
            expected = hotfix.Sha256;
            legacy = false;
        }
        else try
        {
            var text = await GitHubMirrors.GetStringAsync(Http, GitHubMirrors.ReleaseUrls(ShaUrl(v)), ct,
                t => PickSum(t, v).Hash is { Length: 64 } h && h.All(Uri.IsHexDigit));
            (expected, legacy) = PickSum(text, v);
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось получить контрольную сумму с GitHub: " + Short(e)); }

        // 2–3. Архив с прогрессом и проверка суммы. Путь, с которого пришел поврежденный или чужой архив, пропускается
        Exception? lastError = null;
        bool mismatch = false, got = false;
        foreach (var url in hotfix != null ? new[] { HotfixChannel.ZipUrl(hotfix) } : GitHubMirrors.ReleaseUrls(ZipUrl(v, legacy)))
        {
            try { await DownloadAsync(url, zipPath, progress, ct, bytes); }
            catch (Exception e) when (!ct.IsCancellationRequested) { lastError = e; continue; }
            progress.Report((1, "Проверяю архив…"));
            string actual;
            await using (var fs = File.OpenRead(zipPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (actual == expected) { got = true; break; }
            mismatch = true;
        }
        if (!got)
        {
            TryDelete(zipPath);
            throw mismatch
                ? new UpdateException("Контрольная сумма архива не совпала. Архив не установлен, программа не изменена")
                : new UpdateException("Не удалось скачать архив: " + Short(lastError!));
        }

        // 3а. Подпись автора: без нее архив не ставится, даже если сумма совпала
        progress.Report((1, "Проверяю подпись…"));
        string sig;
        try
        {
            sig = await GitHubMirrors.GetStringAsync(Http, hotfix != null ? new[] { HotfixChannel.SigUrl(hotfix) } : GitHubMirrors.ReleaseUrls(SigUrl(v, legacy)), ct,
                t => { try { return Convert.FromBase64String(t.Trim()).Length > 0; } catch (FormatException) { return false; } });
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { throw new UpdateException("Не удалось получить подпись релиза: " + Short(e)); }
        if (!VerifySignature(await File.ReadAllBytesAsync(zipPath, ct), sig, TrustedKey))
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

        // 5. Замена. Прежняя версия копируется в папку данных (для отката и ручного возврата), запущенный файл
        //    откладывается под именем <имя>.bad.exe (удалится при следующем запуске), новая встает как CHIMERION.exe.
        //    Старый DNEX.exe так исчезает с рабочего стола, ярлыки и автозапуск переводятся на новый файл
        progress.Report((1, "Устанавливаю…"));
        var target = TargetPath;
        var stash = StashFor(ExePath);
        try
        {
            Directory.CreateDirectory(BackupDir);
            File.Copy(ExePath, PrevBackup, overwrite: true);
            File.Move(ExePath, stash, overwrite: true);
            try { File.Move(NewPath, target, overwrite: true); }
            catch { File.Move(stash, ExePath); throw; }
            Hide(stash);
        }
        catch (UnauthorizedAccessException) { TryDelete(NewPath); throw new UpdateException($"Нет прав на запись в папку программы ({Dir}). Скачайте версию вручную со страницы релиза"); }
        catch (IOException e) { TryDelete(NewPath); throw new UpdateException("Не удалось заменить файл программы: " + e.Message); }
        TryDelete(zipPath);
        var oldPath = ExePath;
        if (!oldPath.Equals(target, StringComparison.OrdinalIgnoreCase)) { Shortcuts.Retarget(oldPath, target); _movedTo = target; }
        Installed = v;
        SavePending(new Pending { From = UpdateChecker.CurrentVersion.ToString(3), To = ver, OldPath = oldPath });
    }

    /// <summary>Запускает программу заново (новую или возвращенную версию). Она дождется, пока эта копия закроется (см. App.OnStartup).</summary>
    public static void Restart(bool afterUpdate = true) =>
        Launch(new ProcessStartInfo(ExePath, afterUpdate ? AfterUpdateArg : RelaunchArg) { UseShellExecute = false, WorkingDirectory = Dir });

    /// <summary>Скачивание архива одним путем. Ответа ждем не дольше GitHubMirrors.AttemptTimeout, само скачивание не ограничено.</summary>
    private static async Task DownloadAsync(string url, string path, IProgress<(double? Part, string Stage)> progress, CancellationToken ct,
                                            IProgress<(long Done, long? Total)>? bytes = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(GitHubMirrors.AttemptTimeout);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        cts.CancelAfter(Timeout.InfiniteTimeSpan); // ответ пришел: дальше ограничивает только таймаут клиента
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long done = 0;
        int n;
        var host = new Uri(url).Host;
        var via = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase) ? "" : $" через {host}";
        while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
            done += n;
            progress.Report((total > 0 ? (double)done / total.Value : null, $"Скачиваю{via}: {done / 1024} из {(total > 0 ? total.Value / 1024 + " КБ" : "? КБ")}"));
            bytes?.Report((done, total));
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    private static string Short(Exception e) => e.GetBaseException() switch
    {
        HttpRequestException h when h.StatusCode == System.Net.HttpStatusCode.NotFound => "файла нет в релизе",
        HttpRequestException h when h.StatusCode != null => $"сервер ответил {(int)h.StatusCode}",
        TaskCanceledException or TimeoutException => "нет ответа",
        _ => "нет соединения",
    };
}
