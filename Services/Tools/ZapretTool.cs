using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace Dnex.Services.Tools;

/// <summary>
/// zapret-discord-youtube (github.com/Flowseal/zapret-discord-youtube): обход замедления YouTube и Discord.
/// Внутри winws.exe из zapret (bol-van) и драйвер WinDivert. Стратегии — файлы general*.bat: каждый запускает
/// winws.exe со своим набором параметров. CHIMERION эти bat-файлы не запускает (они вызывают service.bat, который
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

    // ---------- Свои сайты и обход по адресам ----------

    private const string UserList = "list-general-user.txt";
    private const string Placeholder = "domain.example.abc";

    /// <summary>
    /// Свои сайты для обхода. Хранятся в настройках CHIMERION отдельно от наборов; в первый раз переносятся из файла
    /// списка zapret (туда их могли вписать раньше вручную или прежней версией CHIMERION).
    /// </summary>
    public IReadOnlyList<string> UserDomains()
    {
        if (State.OwnSites != null) return State.OwnSites;
        var p = Path.Combine(Lists, UserList);
        if (!File.Exists(p)) return Array.Empty<string>();
        return File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l != Placeholder).ToList();
    }

    /// <summary>Сколько доменов уйдет в обход: свои плюс домены выбранных наборов (без повторов).</summary>
    public int SitesCount(Dnex.Services.BypassCatalog.Data? catalog) => BuildList(catalog).Count;

    private List<string> BuildList(Dnex.Services.BypassCatalog.Data? catalog)
    {
        var all = new List<string>(UserDomains());
        all.AddRange(Dnex.Services.BypassCatalog.DomainsOf(catalog, State.Groups));
        foreach (var l in State.BigLists.Select(RemoteLists.Get).OfType<RemoteList>().Where(l => !l.IsIp))
            all.AddRange(RemoteLists.Cached(l));
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Сохраняет свои сайты и выбранные наборы и пересобирает список для winws. Если обход работает,
    /// он перезапускается, чтобы список сразу применился.
    /// </summary>
    public async Task SaveSitesAsync(IEnumerable<string> own, IEnumerable<string> groups, IEnumerable<string> bigLists, Dnex.Services.BypassCatalog.Data? catalog)
    {
        State.OwnSites = own.Select(Dnex.Services.SplitDns.Clean).Where(d => d.Length > 0).Distinct().ToList();
        State.Groups = groups.Distinct().ToList();
        State.BigLists = bigLists.Distinct().ToList();
        SaveState();
        foreach (var l in State.BigLists.Select(RemoteLists.Get).OfType<RemoteList>()) await RemoteLists.EnsureAsync(l);
        WriteUserList(catalog);
        if (IsRunning() && State.Enabled) { await StopAsync(); await StartAsync(); }
    }

    /// <summary>
    /// Файл lists\list-general-user.txt, на который ссылаются все стратегии: свои сайты и наборы. Пустой список
    /// заменяется заглушкой: пустой файл winws не принимает (так пишет и сборка).
    /// </summary>
    private void WriteUserList(Dnex.Services.BypassCatalog.Data? catalog)
    {
        var list = BuildList(catalog);
        Directory.CreateDirectory(Lists);
        var text = "# Never leave this file empty\r\n# Собрано CHIMERION: свои сайты и готовые наборы. Правьте в CHIMERION, ручные правки перезапишутся\r\n" +
                   string.Join("\r\n", list.Count > 0 ? list : new List<string> { Placeholder }) + "\r\n";
        try { File.WriteAllText(Path.Combine(Lists, UserList), text); }
        catch (UnauthorizedAccessException) { throw new ToolException($"Нет прав на папку {Dir}: запустите CHIMERION с правами администратора"); }
    }

    /// <summary>
    /// Сколько покрывает запущенный обход: сайты (основной список сборки и ваши «Сайты для обхода») и, если включен
    /// обход по адресам, сети из ipset-all.txt. Для шапки главного окна.
    /// </summary>
    public (int Sites, int Networks) Coverage(Dnex.Services.BypassCatalog.Data? catalog)
    {
        static int Lines(string path)
        {
            try
            {
                return File.Exists(path)
                    ? File.ReadLines(path).Count(l => l.Length > 0 && l[0] != '#' && !l.StartsWith("203.0.113.113") && !l.StartsWith("domain.example"))
                    : 0;
            }
            catch (Exception) { return 0; }
        }
        var sites = Lines(Path.Combine(Lists, "list-general.txt")) + SitesCount(catalog);
        return (sites, State.IpSet ? Lines(Path.Combine(Lists, "ipset-all.txt")) : 0);
    }

    /// <summary>
    /// Обход по адресам: в ipset-all.txt либо полный список сборки (ipset-all.txt.backup, в нем и сети Cloudflare),
    /// либо заглушка, как в сборке по умолчанию (режим «none» в service.bat).
    /// </summary>
    private void ApplyIpSet()
    {
        var target = Path.Combine(Lists, "ipset-all.txt");
        var full = Path.Combine(Lists, "ipset-all.txt.backup");
        // Внешний список адресов, если выбран и уже скачан; иначе список сборки
        var remote = State.IpSetSource is { } id && RemoteLists.Get(id) is { IsIp: true } rl ? RemoteLists.CachedPath(rl) : null;
        try
        {
            if (State.IpSet && remote != null) File.Copy(remote, target, overwrite: true);
            else if (State.IpSet && File.Exists(full)) File.Copy(full, target, overwrite: true);
            else File.WriteAllText(target, "203.0.113.113/32\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* файл занят или нет прав: остается как был */ }
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
            if (State.Enabled) return; // уже работает наша копия (например, после перезапуска CHIMERION)
            throw new ToolException("winws.exe уже запущен другой программой или вручную. Закройте его, чтобы CHIMERION управляла обходом сама");
        }
        EnsureNoForeignService();
        var strategy = State.Strategy is { } s && File.Exists(Path.Combine(Dir, s)) ? s : DefaultStrategy;
        await LaunchAsync(strategy, State.Games);
    }

    private static void EnsureNoForeignService()
    {
        if (ForeignServiceInstalled())
            throw new ToolException("Установлена служба zapret из service.bat. Удалите ее там (Remove Services), чтобы CHIMERION управляла обходом сама");
    }

    /// <summary>Запуск winws.exe со стратегией strategy. Выбор пользователя (State) не меняется.</summary>
    private async Task LaunchAsync(string strategy, bool games)
    {
        if (!IsInstalled) throw new ToolException("zapret не установлен");
        // Большие списки обновляются перед запуском (без сети остаются прежние копии)
        foreach (var l in State.BigLists.Append(State.IpSet ? State.IpSetSource : null).OfType<string>().Select(RemoteLists.Get).OfType<RemoteList>())
            await RemoteLists.EnsureAsync(l);
        ApplyIpSet();
        // Наборы могли обновиться с прошлого запуска: список пересобирается из последних принятых
        if (State.OwnSites != null || State.Groups.Count > 0 || State.BigLists.Count > 0)
            try { WriteUserList(Dnex.Services.BypassCatalog.LoadCached()); } catch (ToolException) { }
        var args = ParseArgs(await File.ReadAllTextAsync(Path.Combine(Dir, strategy)), Bin, Lists, games)
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

    // ---------- Подбор стратегии ----------

    /// <summary>
    /// Итог проверки стратегии: открылись ли YouTube и Discord, какие из ваших сайтов догрузились полностью, и за
    /// сколько. File = null — проверка без обхода.
    /// </summary>
    public sealed record StrategyResult(string? File, bool YouTube, bool Discord, long Ms,
        IReadOnlyList<string>? SitesOk = null, IReadOnlyList<string>? SitesFailed = null)
    {
        public string Title => File == null ? "Без обхода" : StrategyTitle(File);
        public int SitesTotal => (SitesOk?.Count ?? 0) + (SitesFailed?.Count ?? 0);
        /// <summary>Сколько всего открылось: YouTube, Discord и каждый ваш сайт по единице.</summary>
        public int Score => (YouTube ? 1 : 0) + (Discord ? 1 : 0) + (SitesOk?.Count ?? 0);
        public int MaxScore => 2 + SitesTotal;
    }

    /// <summary>Сколько своих сайтов проверять при подборе: каждая стратегия проверяется на всех, долго не ждем.</summary>
    private const int MaxPickSites = 8;

    // Что проверяем: сайт YouTube и сервер видео googlevideo; API, шлюз и CDN Discord. Для Discord любой
    // ответ HTTP означает, что соединение прошло (шлюз отвечает ошибкой на обычный запрос, это нормально)
    private static readonly string[] YouTubeUrls = { "https://www.youtube.com/", "https://redirector.googlevideo.com/report_mapping" };
    private static readonly string[] DiscordUrls = { "https://discord.com/api/v9/gateway", "https://gateway.discord.gg/", "https://cdn.discordapp.com/" };

    /// <summary>Запрос через новое соединение: ответ пришел за 7 секунд (сервер не 5xx) и сколько это заняло.</summary>
    private static async Task<(bool Ok, long Ms)> ProbeAsync(string url, CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5), AllowAutoRedirect = false, UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(7) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CHIMERION/0.1");
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            return ((int)resp.StatusCode < 500, sw.ElapsedMilliseconds);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (false, sw.ElapsedMilliseconds); }
    }

    /// <summary>
    /// Ваш сайт с этой стратегией: страница и ее самый большой файл (стили или скрипт) должны догрузиться полностью
    /// и без обрыва, всего не дольше 10 секунд. Так ловится и «не открывается вовсе», и «открывается, но
    /// не догружается» (замедление хостингов и Cloudflare обрывает загрузку на 15–30 КБ).
    /// </summary>
    private static async Task<(bool Ok, long Ms)> SiteLoadsAsync(string host, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false,
            });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
            http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
            var page = await Dnex.Services.SiteCheck.FetchAsync(http, $"https://{host}/", 1_500_000, cts.Token);
            if (page.Error != null || page.Stalled || page.Status >= 500) return (false, sw.ElapsedMilliseconds);
            var asset = System.Text.RegularExpressions.Regex.Match(page.Body, @"(?:src|href)=""(/[^""/][^""]*?\.(?:css|js))""");
            if (asset.Success)
            {
                var f = await Dnex.Services.SiteCheck.FetchAsync(http, $"https://{host}{asset.Groups[1].Value}", 3_000_000, cts.Token);
                if (f.Error != null || f.Stalled) return (false, sw.ElapsedMilliseconds);
            }
            return (true, sw.ElapsedMilliseconds);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (false, sw.ElapsedMilliseconds); }
    }

    /// <summary>Проверка YouTube, Discord и ваших сайтов сейчас (с тем обходом, что запущен, или без него).</summary>
    public static async Task<StrategyResult> MeasureAsync(string? file, CancellationToken ct, IReadOnlyList<string>? sites = null)
    {
        var yt = YouTubeUrls.Select(u => ProbeAsync(u, ct)).ToList();
        var dc = DiscordUrls.Select(u => ProbeAsync(u, ct)).ToList();
        var own = (sites ?? Array.Empty<string>()).Select(h => (Host: h, Task: SiteLoadsAsync(h, ct))).ToList();
        await Task.WhenAll(yt.Concat(dc).Concat(own.Select(o => o.Task)));
        bool ytOk = yt.All(t => t.Result.Ok), dcOk = dc.All(t => t.Result.Ok);
        var ms = yt.Concat(dc).Concat(own.Select(o => o.Task)).Where(t => t.Result.Ok).Select(t => t.Result.Ms).DefaultIfEmpty(0).Max();
        return new StrategyResult(file, ytOk, dcOk, ms,
            own.Where(o => o.Task.Result.Ok).Select(o => o.Host).ToList(),
            own.Where(o => !o.Task.Result.Ok).Select(o => o.Host).ToList());
    }

    /// <summary>
    /// Подбор: сначала проверка без обхода, затем каждая стратегия по очереди (запуск, проверка, остановка).
    /// Нужны права администратора: без них каждая смена стратегии спрашивала бы UAC. После подбора возвращается
    /// то, что было до него (обход работал или нет, прежняя стратегия). Результаты: лучшие первыми.
    /// </summary>
    public async Task<List<StrategyResult>> PickAsync(IProgress<string> progress, CancellationToken ct, IEnumerable<string>? sites = null)
    {
        // Кроме YouTube и Discord проверяются ваши сайты из «Сайты для обхода» (не больше восьми)
        var targets = (sites ?? UserDomains()).Select(Dnex.Services.SplitDns.Clean).Where(d => d.Length > 0).Distinct().Take(MaxPickSites).ToList();
        if (!DnsSwitcher.IsAdmin()) throw new ToolException("Для подбора нужны права администратора: перезапустите CHIMERION с ними");
        if (!IsInstalled) throw new ToolException("zapret не установлен");
        if (IsRunning() && !State.Enabled)
            throw new ToolException("winws.exe уже запущен другой программой или вручную. Закройте его, чтобы CHIMERION могла проверять стратегии");
        EnsureNoForeignService();

        var wasRunning = IsRunning();
        var results = new List<StrategyResult>();
        try
        {
            if (wasRunning) { progress.Report("Останавливаю обход…"); KillWinws(); }
            progress.Report("Проверяю без обхода…");
            results.Add(await MeasureAsync(null, ct, targets));
            var list = Strategies();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"Проверяю {StrategyTitle(list[i])} ({i + 1} из {list.Count})…");
                try
                {
                    await LaunchAsync(list[i], State.Games);
                    await Task.Delay(1000, ct); // обходу нужно мгновение, чтобы встать на пакеты
                    results.Add(await MeasureAsync(list[i], ct, targets));
                }
                catch (ToolException) { results.Add(new StrategyResult(list[i], false, false, 0, Array.Empty<string>(), targets)); }
                finally { KillWinws(); }
            }
        }
        finally
        {
            KillWinws();
            if (wasRunning)
            {
                progress.Report("Возвращаю прежнюю стратегию…");
                try { await LaunchAsync(State.Strategy ?? DefaultStrategy, State.Games); } catch (ToolException) { }
            }
        }
        return Rank(results);
    }

    /// <summary>Лучшие первыми: больше открытых сервисов, при равенстве быстрее. «Без обхода» остается в списке для сравнения.</summary>
    public static List<StrategyResult> Rank(IEnumerable<StrategyResult> results) =>
        results.OrderByDescending(r => r.Score).ThenBy(r => r.Score == 0 ? long.MaxValue : r.Ms).ThenBy(r => r.File == null ? 0 : 1).ToList();

    private static void KillWinws()
    {
        Kill(Process.GetProcessesByName("winws"));
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
        var temp = Path.Combine(Path.GetTempPath(), "CHIMERION-tools", "zapret-" + Guid.NewGuid().ToString("N"));
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
                throw new ToolException($"Нет прав на папку {Dir}. Скорее всего, прошлую версию ставила CHIMERION с правами администратора: запустите CHIMERION от администратора и повторите");
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
