using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Поддельный GitHub: адрес → ответ. Адреса без ответа дают 404, а Offline имитирует отсутствие сети.</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public bool Offline { get; set; }
    /// <summary>Адреса, которые «режет провайдер»: запрос к ним падает, как при блокировке.</summary>
    public Func<string, bool> Blocked { get; set; } = _ => false;
    public List<string> Requested { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Requested) Requested.Add(url);
        if (Offline || Blocked(url)) throw new HttpRequestException("Этот хост неизвестен", new System.Net.Sockets.SocketException(11001));
        return Task.FromResult(Files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Установка обновления целиком, как в программе: сумма, архив, подпись, распаковка, замена exe, откат. Сеть,
/// путь к exe, ключ подписи и запуск процесса подменены; exe лежит во временной папке.
/// </summary>
[Collection("scenarios")]
public sealed class UpdateScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"chimerion-upd-{Guid.NewGuid():N}");
    private readonly FakeGitHub _gh = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<ProcessStartInfo> _launched = new();
    private static readonly Version V = UpdateChecker.CurrentVersion; // в архиве CHIMERION.dll этой сборки: версия совпадает
    private static readonly byte[] OldExe = "старая версия"u8.ToArray();
    private static readonly byte[] NewExe = File.ReadAllBytes(typeof(Updater).Assembly.Location);

    public UpdateScenarioTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "app"));
        Updater.Http = new HttpClient(_gh);
        Updater.TrustedKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        Updater.Launch = psi => _launched.Add(psi);
        Updater.TempDirOverride = Path.Combine(_dir, "tmp");
        UseExe("CHIMERION.exe");
        File.Delete(Storage.PathOf("update-pending.json"));
        if (Directory.Exists(Storage.PathOf("backup"))) Directory.Delete(Storage.PathOf("backup"), true);
        // Ярлыки ищутся только во временной папке теста, а не на настоящем рабочем столе
        _links = Directory.CreateDirectory(Path.Combine(_dir, "links")).FullName;
        Shortcuts.Folders = () => new[] { _links };
    }

    private readonly string _links;
    private static readonly Func<IEnumerable<string>> DefaultFolders = Shortcuts.Folders;
    private string Backup => Storage.PathOf(Path.Combine("backup", "previous.exe"));

    public void Dispose()
    {
        Updater.Http = new HttpClient();
        Updater.TrustedKey = Updater.PublicKey;
        Updater.Launch = psi => Process.Start(psi)?.Dispose();
        Updater.TempDirOverride = null;
        Updater.ExePathOverride = null;
        Shortcuts.Folders = DefaultFolders;
        File.Delete(Storage.PathOf("update-pending.json"));
        if (Directory.Exists(Storage.PathOf("backup"))) Directory.Delete(Storage.PathOf("backup"), true);
        _key.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Exe => Updater.ExePathOverride!;
    private string Side(string suffix) => Path.Combine(Path.GetDirectoryName(Exe)!, Path.GetFileNameWithoutExtension(Exe) + suffix);

    private void UseExe(string name)
    {
        Updater.ExePathOverride = Path.Combine(_dir, "app", name);
        File.WriteAllBytes(Updater.ExePathOverride, OldExe);
    }

    private static byte[] Zip(string entry, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = z.CreateEntry(entry).Open()) s.Write(content);
        return ms.ToArray();
    }

    /// <summary>Релиз как у release.ps1: архив CHIMERION, подпись и файл сумм. legacyOnly — релиз до переименования (только DNEX).</summary>
    private void Publish(byte[]? chimerionZip = null, byte[]? dnexZip = null, bool legacyOnly = false, ECDsa? signer = null, string? sumsOverride = null)
    {
        var sums = "";
        void Put(bool legacy, byte[] zip)
        {
            _gh.Files[Updater.ZipUrl(V, legacy)] = zip;
            _gh.Files[Updater.SigUrl(V, legacy)] = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String((signer ?? _key).SignData(zip, HashAlgorithmName.SHA256)));
            sums += $"{Convert.ToHexString(SHA256.HashData(zip))}  {Updater.ZipName(V, legacy)}\n";
        }
        if (legacyOnly) Put(true, dnexZip ?? Zip("DNEX.exe", NewExe));
        else Put(false, chimerionZip ?? Zip("CHIMERION.exe", NewExe));
        _gh.Files[Updater.ShaUrl(V)] = System.Text.Encoding.ASCII.GetBytes(sumsOverride ?? sums);
    }

    private Task Install() => Updater.InstallAsync(V, new Progress<(double?, string)>(), CancellationToken.None);

    private void AssertUntouched()
    {
        Assert.Equal(OldExe, File.ReadAllBytes(Exe));
        Assert.False(File.Exists(Side(".new.exe")));
        Assert.False(File.Exists(Side(".bad.exe")));
        Assert.False(File.Exists(Storage.PathOf("update-pending.json")));
    }

    [Fact]
    public async Task Good_release_replaces_exe_and_keeps_old_for_rollback()
    {
        Publish();
        await Install();
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Equal(OldExe, File.ReadAllBytes(Backup));           // прежняя версия в папке данных, не рядом с программой
        Assert.True(File.GetAttributes(Side(".bad.exe")).HasFlag(FileAttributes.Hidden)); // запущенный файл отложен скрытым
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "app")), f => !File.GetAttributes(f).HasFlag(FileAttributes.Hidden));
        Assert.True(File.Exists(Storage.PathOf("update-pending.json")));
    }

    [Fact]
    public async Task Corrupted_archive_is_not_installed()
    {
        // Сумма и подпись честно посчитаны от битого файла: ловит именно распаковка
        var broken = Zip("CHIMERION.exe", NewExe)[..2000];
        Publish(chimerionZip: broken);
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("поврежден", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task Sha256_mismatch_is_not_installed()
    {
        Publish(sumsOverride: new string('A', 64) + "  " + Updater.ZipName(V) + "\n");
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("Контрольная сумма архива не совпала", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task Wrong_signature_is_not_installed()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(signer: stranger); // сумма верная, но подписал не автор: подмена релиза целиком
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("Подпись архива не прошла проверку", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task Archive_with_other_version_is_not_installed()
    {
        var other = File.ReadAllBytes(typeof(FactAttribute).Assembly.Location); // xunit: версия не совпадает
        Publish(chimerionZip: Zip("CHIMERION.exe", other));
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("другой версии", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task GitHub_unavailable_changes_nothing()
    {
        Publish();
        _gh.Offline = true;
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("нет соединения", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task Update_check_reports_failure_when_GitHub_is_unavailable()
    {
        var (http, web, cdn) = (UpdateChecker.Http, UpdateChecker.Web, UpdateChecker.Cdn);
        UpdateChecker.Http = new HttpClient(new FakeGitHub { Offline = true });
        UpdateChecker.Web = new HttpClient(new FakeGitHub { Offline = true });
        UpdateChecker.Cdn = new HttpClient(new FakeGitHub { Offline = true });
        try
        {
            var r = await UpdateChecker.CheckDetailedAsync();
            Assert.Equal(UpdateStatus.Failed, r.Status);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        }
        finally { (UpdateChecker.Http, UpdateChecker.Web, UpdateChecker.Cdn) = (http, web, cdn); }
    }

    /// <summary>Файлы релиза доступны только через прокси-зеркало: прямой github.com «заблокирован».</summary>
    private void MirrorOnly()
    {
        foreach (var (url, body) in _gh.Files.ToList()) _gh.Files[GitHubMirrors.ReleaseProxies[1] + url] = body;
        _gh.Blocked = u => u.StartsWith("https://github.com/") || u.StartsWith(GitHubMirrors.ReleaseProxies[0]);
    }

    [Fact]
    public async Task Update_installs_through_mirror_when_GitHub_is_blocked()
    {
        Publish();
        MirrorOnly();
        await Install();
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Contains(_gh.Requested, u => u.StartsWith(GitHubMirrors.ReleaseProxies[1]));
    }

    [Fact]
    public async Task Mirror_cannot_slip_in_a_forged_archive()
    {
        // Зеркало подменило архив и подогнало под него файл сумм: не пропускает подпись автора
        Publish();
        MirrorOnly();
        var forged = Zip("CHIMERION.exe", NewExe.Concat(new byte[] { 1, 2, 3 }).ToArray());
        var proxy = GitHubMirrors.ReleaseProxies[1];
        _gh.Files[proxy + Updater.ZipUrl(V)] = forged;
        _gh.Files[proxy + Updater.ShaUrl(V)] = System.Text.Encoding.ASCII.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(forged))}  {Updater.ZipName(V)}\n");
        var e = await Assert.ThrowsAsync<UpdateException>(Install);
        Assert.Contains("Подпись архива не прошла проверку", e.Message);
        AssertUntouched();
    }

    [Fact]
    public async Task Broken_mirror_is_skipped_for_the_next_one()
    {
        // Первое зеркало отдает свою страницу вместо архива, второе исправное
        Publish();
        foreach (var (url, body) in _gh.Files.ToList())
        {
            _gh.Files[GitHubMirrors.ReleaseProxies[0] + url] = url.EndsWith(".zip") ? "<html>ошибка прокси</html>"u8.ToArray() : body;
            _gh.Files[GitHubMirrors.ReleaseProxies[1] + url] = body;
        }
        _gh.Blocked = u => u.StartsWith("https://github.com/");
        await Install();
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
    }

    [Fact]
    public async Task Update_check_falls_back_to_jsdelivr_versions()
    {
        var (http, web, cdn) = (UpdateChecker.Http, UpdateChecker.Web, UpdateChecker.Cdn);
        var cdnFake = new FakeGitHub();
        var next = new Version(V.Major, V.Minor, V.Build + 1);
        cdnFake.Files[$"https://data.jsdelivr.com/v1/packages/gh/{UpdateChecker.Repo}"] = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"type\":\"gh\",\"versions\":[{{\"version\":\"{V.ToString(3)}\"}},{{\"version\":\"{next.ToString(3)}\"}},{{\"version\":\"0.1.2\"}}]}}");
        UpdateChecker.Http = new HttpClient(new FakeGitHub { Offline = true });
        UpdateChecker.Web = new HttpClient(new FakeGitHub { Offline = true });
        UpdateChecker.Cdn = new HttpClient(cdnFake);
        try
        {
            var r = await UpdateChecker.CheckDetailedAsync();
            Assert.Equal(UpdateStatus.Available, r.Status);
            Assert.Equal(next, r.Latest);
        }
        finally { (UpdateChecker.Http, UpdateChecker.Web, UpdateChecker.Cdn) = (http, web, cdn); }
    }

    [Fact]
    public void Raw_github_files_map_to_jsdelivr()
    {
        var urls = GitHubMirrors.RawUrls("https://raw.githubusercontent.com/under2ker/chimerion/main/catalog/servers.json").ToList();
        Assert.Equal("https://cdn.jsdelivr.net/gh/under2ker/chimerion@main/catalog/servers.json", urls[1]);
        Assert.Equal(new[] { "https://example.com/x" }, GitHubMirrors.RawUrls("https://example.com/x"));
        Assert.Equal(4, GitHubMirrors.ReleaseUrls("https://github.com/a/b/releases/download/v1/x.zip").Count());
    }

    [Fact]
    public async Task Signed_list_comes_from_jsdelivr_when_raw_github_is_blocked()
    {
        var fake = new FakeGitHub { Blocked = u => u.StartsWith("https://raw.githubusercontent.com/") };
        fake.Files["https://cdn.jsdelivr.net/gh/under2ker/chimerion@main/catalog/x.json"] = "{\"ok\":1}"u8.ToArray();
        fake.Files["https://cdn.jsdelivr.net/gh/under2ker/chimerion@main/catalog/x.json.sig"] = "SIG"u8.ToArray();
        var got = await GitHubMirrors.SignedRawAsync(new HttpClient(fake), "https://raw.githubusercontent.com/under2ker/chimerion/main/catalog/x.json",
            (b, sig) => sig == "SIG", CancellationToken.None);
        Assert.Equal(("{\"ok\":1}", "SIG"), got);
    }

    [Fact]
    public async Task Locked_exe_is_not_replaced()
    {
        Publish();
        // Файл открыт без права удаления (антивирус, второй процесс): переименовать его нельзя
        using (new FileStream(Exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var e = await Assert.ThrowsAsync<UpdateException>(Install);
            Assert.Contains("Не удалось заменить файл программы", e.Message);
        }
        AssertUntouched();
    }

    private void PendingFor(string? oldPath) => File.WriteAllText(Storage.PathOf("update-pending.json"),
        System.Text.Json.JsonSerializer.Serialize(new Updater.Pending { From = "0.1.20", To = V.ToString(3), OldPath = oldPath }));

    [Fact]
    public void Three_failed_starts_roll_back_to_previous_version()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
        File.WriteAllBytes(Backup, "прежняя версия"u8.ToArray());
        PendingFor(Exe);

        // Первые три запуска новой версии упали до подтверждения: каждый раз только счетчик
        for (int i = 1; i <= 3; i++) Assert.False(Updater.OnStartup());
        Assert.Equal(OldExe, File.ReadAllBytes(Exe));
        Assert.Empty(_launched);

        // Четвертый: возвращается прежняя версия и запускается с пометкой, какая версия не запустилась
        Assert.True(Updater.OnStartup());
        Assert.Equal("прежняя версия"u8.ToArray(), File.ReadAllBytes(Exe));
        Assert.Equal(Updater.RolledBackArg + V.ToString(3), Assert.Single(_launched).Arguments);
        Assert.False(File.Exists(Storage.PathOf("update-pending.json")));

        // Неудачная версия отложена и убирается при следующем запуске
        Assert.True(File.Exists(Side(".bad.exe")));
        Assert.False(Updater.OnStartup());
        Assert.False(File.Exists(Side(".bad.exe")));
    }

    [Fact]
    public void Rollback_works_for_update_made_by_old_version()
    {
        // Обновление поставила 0.1.26: прежний файл лежит рядом как .old.exe, пути в отметке нет
        File.WriteAllBytes(Side(".old.exe"), "прежняя версия"u8.ToArray());
        PendingFor(null);
        for (int i = 1; i <= 3; i++) Assert.False(Updater.OnStartup());
        Assert.True(Updater.OnStartup());
        Assert.Equal("прежняя версия"u8.ToArray(), File.ReadAllBytes(Exe));
        Assert.False(File.Exists(Side(".old.exe")));
    }

    [Fact]
    public void Rollback_returns_old_DNEX_file_and_shortcut()
    {
        // Обновление переименовало DNEX.exe в CHIMERION.exe, новая версия не запускается: возвращается DNEX.exe
        var dnex = Path.Combine(_dir, "app", "DNEX.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
        File.WriteAllBytes(Backup, "прежняя версия"u8.ToArray());
        PendingFor(dnex);
        for (int i = 1; i <= 3; i++) Assert.False(Updater.OnStartup());
        Assert.True(Updater.OnStartup());
        Assert.Equal("прежняя версия"u8.ToArray(), File.ReadAllBytes(dnex));
        Assert.False(File.Exists(Exe));
        Assert.Equal(dnex, Assert.Single(_launched).FileName);
        Assert.True(File.Exists(Path.Combine(_dir, "app", "DNEX.bad.exe"))); // его удалит сама прежняя версия при запуске
    }

    [Fact]
    public void Confirmed_start_moves_old_copies_out_of_program_folder()
    {
        File.WriteAllBytes(Side(".old.exe"), "прежняя версия"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_dir, "app", "DNEX.prev.exe"), "совсем старая"u8.ToArray());
        PendingFor(null);
        Assert.False(Updater.OnStartup());
        Updater.ConfirmStarted();
        Assert.False(File.Exists(Storage.PathOf("update-pending.json")));
        Assert.Equal(new[] { Exe }, Directory.GetFiles(Path.Combine(_dir, "app")));
        Assert.Equal("прежняя версия"u8.ToArray(), File.ReadAllBytes(Backup)); // осталась самая новая из прежних
        Assert.Equal(new Version(0, 0), Updater.PreviousVersion() ?? new Version(0, 0)); // в тестовом файле номера нет
    }

    private static void MakeLink(string lnk, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(lnk);
        link.TargetPath = target;
        link.Save();
    }

    private static string LinkTarget(string lnk)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return shell.CreateShortcut(lnk).TargetPath;
    }

    [Fact]
    public async Task Update_replaces_DNEX_on_desktop_with_CHIMERION()
    {
        // На рабочем столе DNEX.exe и ярлык на него. После обновления кнопкой там CHIMERION.exe, ярлык ведет на него
        File.Delete(Exe);
        UseExe("DNEX.exe");
        var dnex = Exe;
        var chimerion = Path.Combine(_dir, "app", "CHIMERION.exe");
        MakeLink(Path.Combine(_links, "DNEX.lnk"), dnex);
        MakeLink(Path.Combine(_links, "Other app.lnk"), Path.Combine(_dir, "other.exe"));
        Publish();
        await Install();
        Assert.Equal(NewExe, File.ReadAllBytes(chimerion));
        Assert.False(File.Exists(dnex));
        Assert.Equal(chimerion, LinkTarget(Path.Combine(_links, "CHIMERION.lnk")));
        Assert.False(File.Exists(Path.Combine(_links, "DNEX.lnk")));
        Assert.Equal(Path.Combine(_dir, "other.exe"), LinkTarget(Path.Combine(_links, "Other app.lnk")));
        Assert.Contains(Updater.ZipUrl(V), _gh.Requested);

        // Новая версия запустилась: отложенный DNEX.bad.exe убирается, в папке один файл
        Updater.ExePathOverride = chimerion;
        Assert.False(Updater.OnStartup());
        Assert.Equal(new[] { chimerion }, Directory.GetFiles(Path.Combine(_dir, "app")));
    }

    [Fact]
    public void DNEX_installed_by_old_updater_renames_itself_after_confirmed_start()
    {
        // 0.1.26 обновил DNEX.exe на месте. Новая версия после успешного запуска сама становится CHIMERION.exe
        File.Delete(Exe);
        UseExe("DNEX.exe");
        var dnex = Exe;
        MakeLink(Path.Combine(_links, "DNEX.lnk"), dnex);
        Updater.ConfirmStarted();
        var chimerion = Path.Combine(_dir, "app", "CHIMERION.exe");
        Assert.True(File.Exists(chimerion));
        Assert.False(File.Exists(dnex));
        Assert.Equal(chimerion, LinkTarget(Path.Combine(_links, "CHIMERION.lnk")));
    }

    [Fact]
    public void Manual_return_swaps_versions_through_backup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
        File.WriteAllBytes(Backup, "прежняя версия"u8.ToArray());
        Updater.SwapWithPrevious();
        Assert.Equal("прежняя версия"u8.ToArray(), File.ReadAllBytes(Exe));
        Assert.Equal(OldExe, File.ReadAllBytes(Backup)); // вперед можно вернуться тем же способом
        Assert.True(File.Exists(Side(".bad.exe")));
    }

    [Fact]
    public async Task Release_before_rename_installs_from_DNEX_archive()
    {
        Publish(legacyOnly: true);
        await Install();
        Assert.Equal(NewExe, File.ReadAllBytes(Exe));
        Assert.Contains(Updater.ZipUrl(V, legacy: true), _gh.Requested);
        Assert.DoesNotContain(Updater.ZipUrl(V), _gh.Requested);
    }
}
