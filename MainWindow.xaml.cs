using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

public partial class MainWindow : Window
{
    private const string ServersFile = "servers.json";
    private const string SettingsFile = "settings.json";

    private readonly ObservableCollection<DnsServer> _servers;
    private ICollectionView? _view;
    private AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsive();
        ServerGrid.SizeChanged += (_, _) => { if (EmptyState.Visibility == Visibility.Visible) UpdateEmptyState(0); };

        // Завершение Windows (выключение, выход из сеанса) не должно прятать программу в трей
        // Выключение или перезагрузка Windows: выходим без вопроса и без отключения, чтобы DNS остался после перезагрузки
        Application.Current.SessionEnding += (_, _) => { _sessionEnding = true; _exitRequested = true; };
        AddPanel.RenderTransform = new TranslateTransform();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_toastQueue.TryDequeue(out var next)) { ShowToast(next.Text, next.Key, next.Feature); return; } // следующее из очереди
            Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, Motion(300)));
        };

        _settings = LoadSettings();
        // Хранилище: блокировки файлов в журнал, а настройки, не прочитанные из-за блокировки, перечитываются
        Storage.Trace = msg => Log(msg);
        Storage.Reread = file => Dispatcher.InvokeAsync(() =>
        {
            if (!file.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase)) return;
            _settings = LoadSettings();
            ApplyTimerSettings();
            Log("Настройки перечитаны после блокировки файла");
        });
        SiteCheck.ZapretRunning = () => _tools.OfType<ZapretTool>().FirstOrDefault()?.IsRunning() == true;
        Loc.SetLanguage(_settings.Language);
        Loc.Changed += OnLanguageChanged;
        ThemeColors.Changed += OnThemeChanged;
        ThemeColors.Apply(_settings.Colors, _settings.Theme);
        // Режим скрытия данных: до появления первого окна, чтобы ни один кадр не попал в захват
        PrivacyGuard.Install();
        PrivacyGuard.Set(_settings.PrivacyMode, _settings.PrivacyHideWindows);
        PrivacyGuard.Changed += OnPrivacyChanged;
        SetupTray(); // после настроек: пункты сервисов берутся из списка проверок
        ApplyTimerSettings();
        ApplyCountryPin();
        _pingTimer.Tick += async (_, _) => await RefreshPingsAsync();
        _pingTimer.Start();
        _servers = new ObservableCollection<DnsServer>(Storage.LoadOrCreate(ServersFile, () => new List<DnsServer>()));
        foreach (var s in _servers.Where(s => s.Status == ServerStatus.Checking))
            s.Status = ServerStatus.Unknown;

        _junk = Storage.LoadOrCreate(JunkFile, () => new Dictionary<string, DateTime>());
        foreach (var k in _junk.Where(kv => DateTime.Now - kv.Value > JunkTtl).Select(kv => kv.Key).ToList()) _junk.Remove(k);
        foreach (var s in _servers) _known.Add(s.Key);
        // Встроенный каталог: новые сервисы появляются в списке сразу, еще до парсинга.
        foreach (var c in Catalog.Servers())
            if (!_junk.ContainsKey(c.Key) && _known.Add(c.Key)) _servers.Add(c);
        AutoPurge(silent: true);

        _view = CollectionViewSource.GetDefaultView(_servers);
        _view.Filter = FilterServer;
        // Рабочие серверы наверху. Сортировка НЕ живая: строки не переставляются при обновлении статуса и пинга.
        // Порядок пересчитывается после пакетной проверки, при смене фильтра и при добавлении сервера.
        // Подключенный сервер всегда первый, затем избранные, затем рабочие наверху
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.IsConnected), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.IsFavorite), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Rank), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.LatencyMs), ListSortDirection.Ascending));
        // Клик по заголовку колонки заменяет правила сортировки: подключенный сервер закрепляем заново
        ((System.Collections.Specialized.INotifyCollectionChanged)_view.SortDescriptions).CollectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PinConnectedFirst));
        ServerGrid.ItemsSource = _view;

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _view?.Refresh(); UpdateStats(); };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _servers.CollectionChanged += (_, _) => _statsTimer.Start();

        SetupDashboard();
        Loaded += async (_, _) => await InitAsync();
        _watchdog.Tick += Watchdog_Tick;
        WatchVpn();
        TitleVersion.Text = UpdateChecker.CurrentVersion.ToString();
        PanelUpdateHost.Content = CreateUpdateCard();
        _undoTimer.Tick += (_, _) => UndoTick();
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += OnClosing;
    }

    private bool _initStarted;

    /// <summary>Запуск вместе с Windows: все, что делается при запуске, но без показа окна (см. App.OnStartup).</summary>
    public Task StartHiddenAsync()
    {
        Log("Запуск вместе с Windows: программа в трее");
        return InitAsync();
    }

    /// <summary>PowerShell ограничен политикой (ConstrainedLanguage): смена DNS здесь не сработает, говорим об этом сразу.</summary>
    private async Task WarnIfPowerShellRestrictedAsync()
    {
        if (DnsSwitcher.LanguageModeWarning(await DnsSwitcher.LanguageModeAsync()) is not { } warn) return;
        Log(warn);
        ShowToast("PowerShell ограничен политикой: подключение здесь не сработает, подробности в журнале", "Bad");
    }

    /// <summary>Поврежденные файлы данных: что пропало и где копия. Сам файл перезапишется при следующем сохранении.</summary>
    private void ReportRecoveredFiles()
    {
        var names = new List<string>();
        while (Storage.Recovered.TryDequeue(out var f)) if (!names.Contains(f)) names.Add(f);
        if (names.Count == 0) return;
        string What(string f) => f switch
        {
            "servers.json" => "список серверов начат заново",
            "settings.json" => "настройки сброшены на стандартные",
            "active.json" => "запись о подключении потеряна. Если DNS остался измененным, верните его через reset-dns.cmd",
            "tools.json" => "состояние YouTube, Discord и Telegram сброшено",
            _ => "данные начаты заново",
        };
        foreach (var f in names)
            Log($"Файл {f} поврежден: {What(f)}. Копия испорченного файла: {Storage.PathOf(f + ".broken")}");
        ShowToast($"Поврежден файл данных: {string.Join(", ", names)}. Копия сохранена, подробности в журнале", "Warn");
    }

    /// <summary>Подготовка после запуска. Один раз: из Loaded или, при запуске в трей, без окна.</summary>
    private async Task InitAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        {
            Log($"Данные: {Storage.Dir}. Серверов в списке: {_servers.Count}");
            ReportRecoveredFiles();
            _ = WarnIfPowerShellRestrictedAsync();
            if (Updater.RolledBackFrom is { } failed)
            {
                Log($"Версия {failed} не запустилась, возвращена {UpdateChecker.CurrentVersion}. Эта версия больше не будет предлагаться");
                _settings.SkippedUpdate = failed;
                try { Storage.Save("settings.json", _settings); } catch (Exception) { }
                ShowToast($"Обновление до {failed} не запустилось, возвращена версия {UpdateChecker.CurrentVersion}", "Warn");
            }
            // Обновление считается удачным, если программа проработала 30 секунд (или закрылась штатно, см. OnClosed)
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Updater.ConfirmStarted(), TaskScheduler.Default);
            if (Updater.StartedAfterUpdate)
            {
                Log($"Программа обновлена до версии {UpdateChecker.CurrentVersion}");
                ShowToast($"Обновлено до версии {UpdateChecker.CurrentVersion}");
            }
            UpdateStats();
            ShowCompare();
            // «Открывать в компактном виде» сильнее запомненного вида: иначе открывается тот, в котором закрыли
            try { if (_settings.StartCompact || Storage.LoadOrCreate("ui.json", () => new Dictionary<string, bool>()).GetValueOrDefault("compact")) { SetCompact(true, animate: false); ShowControlPanel(); } }
            catch (Exception) { }
            await RefreshConnectionAsync();
            _ = AutoStart.RefreshPathAsync(Log);
            await EnsureConnectionAfterStartAsync();
            _ = StartToolsAsync();
            _ = RefreshTrayServicesAsync(); // первая проверка сервисов для трея сразу после запуска
            StartCrowd();
            StartSiteWatch();
            ScheduleUpdateCheck(); // настройку «Проверять новую версию» смотрит сама проверка: ее можно включить на ходу
            if (DnsSwitcher.GetActive() is { } a)
                Log($"Активно подключение «{a.Name}» с {a.Since:dd.MM HH:mm}.");
            SelectBest();

            _ = ResolveLocationsAsync();

            // Автопроверка при запуске: все, что не проверялось или проверялось больше 3 часов назад. Раньше порог был
            // 30 минут, и каждый перезапуск давал сотни запросов к сервисам; стабильность от этого точнее не становилась
            await MergeRemoteCatalogAsync();
            var stale = _servers.Where(x => x.LastChecked == null || DateTime.Now - x.LastChecked > TimeSpan.FromHours(3)).ToList();
            if (stale.Count > 0) await RunChecksAsync(stale);
        }
    }


    private bool _closeConfirmed;
    /// <summary>При выходе выбрано «оставить все работать»: zapret и прокси не выключаются.</summary>
    private bool _keepToolsOnExit;

    /// <summary>При выходе с активным подключением спрашиваем, отключиться ли.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Крестик прячет программу в трей: подключение и проверки продолжают работать.
        // Выход — только из меню трея (ExitFromTray).
        if (!_exitRequested && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveServersNow();
        // Windows завершает сеанс: не спрашиваем и не отключаем. Статический DNS сохраняется на адаптере,
        // файл подключения остается, и после перезагрузки CHIMERION продолжает с того же сервера.
        var a = DnsSwitcher.GetActive();
        // Работающие инструменты, которые включила CHIMERION (своя копия прокси пользователя не в счет)
        var running = _tools.Where(t => t.State.Enabled && t.IsInstalled && t.IsRunning() && !(t is TgProxyTool tg && tg.ForeignCopyRunning()))
                            .Select(t => t is ZapretTool ? "обход zapret" : "ускорение Telegram").ToList();
        if (_sessionEnding || _closeConfirmed || (a == null && running.Count == 0)) { _cts?.Cancel(); return; }

        var r = ClosePrompt.Ask(a == null ? null : Loc.T(a.Name), running); // название вида «Нидерланды, Амстердам» на языке интерфейса
        if (r == MessageBoxResult.Cancel)
        {
            // Передумал: программа работает дальше, текущая проверка или поиск не прерываются
            e.Cancel = true;
            _exitRequested = false;
            return;
        }
        _cts?.Cancel();
        // «Оставить все работать»: DNS как есть, zapret и прокси не выключаются (как и при выходе из Windows)
        if (r == MessageBoxResult.No) { _keepToolsOnExit = true; return; }
        if (a == null) { _closeConfirmed = true; return; } // выключить нечего, кроме инструментов: их выключит OnClosed
        e.Cancel = true;
        // Выход с отключением: только возврат DNS, без проверок подключения
        if (!await DisconnectAsync("выход из программы", probe: false))
        {
            _exitRequested = false;
            DnexDialog.Show("Отключиться не удалось",
                "Программа не закрыта, иначе DNS остался бы измененным. Подробности в журнале. Попробуйте еще раз или выберите выход с сохранением DNS.",
                DialogKind.Warn, "Выход");
            return;
        }
        _closeConfirmed = true;
        // Закрываем после выхода из обработчика: если отключение завершилось сразу, окно еще в процессе закрытия,
        // и повторный Close() бросал InvalidOperationException (сбой из error.log от 23.10)
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private static readonly (Mode Mode, string Title, string Hint)[] Modes =
    {
        (Mode.AiOnly, "Только ИИ", "Рекомендуем для начала. Smart DNS для ChatGPT, Claude и Gemini. Telegram-прокси и zapret выключены"),
        (Mode.All, "Все сразу", "Smart DNS, ускорение Telegram и обход для YouTube и Discord"),
        (Mode.Games, "Игры", "zapret с обходом для игр (порты выше 1023). DNS и Telegram не меняются"),
        (Mode.Off, "Все выключить", "Отключить DNS, выключить Telegram-прокси и zapret"),
    };

    private static readonly string[] ProbeHosts = { "ya.ru", "vk.com", "google.com", "cloudflare.com" };


    private static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(8);

    /// <summary>Ctrl+Z во всем окне. В поле ввода работает его собственная отмена текста.</summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undo != null && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            await RunUndoAsync();
        }
    }
    private static readonly TimeSpan JunkTtl = TimeSpan.FromDays(14);


    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Storage.Dir) { UseShellExecute = true });

    /// <summary>Загружает настройки и сразу шифрует открытые Cookie, если пользователь вписал их вручную.</summary>
    private static AppSettings LoadSettings()
    {
        var st = Storage.LoadOrCreate(SettingsFile, () => new AppSettings());
        Catalog.Merge(st);
        // Проверка значений из файла: без отрицательных/огромных лимитов, только https для эталонного DNS.
        st.TimeoutSeconds = Math.Clamp(st.TimeoutSeconds, 2, 30);
        st.Parallelism = Math.Clamp(st.Parallelism, 1, 64);
        st.PingRefreshMinutes = Math.Clamp(st.PingRefreshMinutes, 1, 60);
        // До 0.1.41 трей проверял сервисы каждые 5 минут: это лишние автоматические запросы с адреса, где вы в аккаунте
        if (st.TrayCheckMinutes == 5 && !st.TrayIntervalMigrated) st.TrayCheckMinutes = 15;
        st.TrayIntervalMigrated = true;
        // settings.json до 0.1.22 хранил прежние лимиты поиска: поднимаем до новых, если они были ниже
        var defs = new AppSettings();
        if (st.CrawlDepth < defs.CrawlDepth) st.CrawlDepth = defs.CrawlDepth;
        if (st.CrawlMaxPages < defs.CrawlMaxPages) st.CrawlMaxPages = defs.CrawlMaxPages;
        if (st.DiscoveryResultsPerQuery < defs.DiscoveryResultsPerQuery) st.DiscoveryResultsPerQuery = defs.DiscoveryResultsPerQuery;
        if (st.CrawlParallelism < defs.CrawlParallelism) st.CrawlParallelism = defs.CrawlParallelism;
        foreach (var q in defs.DiscoveryQueries.Where(q => !st.DiscoveryQueries.Contains(q))) st.DiscoveryQueries.Add(q);
        foreach (var k in defs.LinkKeywords.Where(k => !st.LinkKeywords.Contains(k))) st.LinkKeywords.Add(k);
        foreach (var h in defs.BlockedHosts.Where(h => !st.BlockedHosts.Contains(h))) st.BlockedHosts.Add(h);
        st.CrawlDepth = Math.Clamp(st.CrawlDepth, 0, 5);
        st.CrawlMaxPages = Math.Clamp(st.CrawlMaxPages, 1, 5000);
        st.CrawlParallelism = Math.Clamp(st.CrawlParallelism, 1, 16);
        st.TelegramPages = Math.Clamp(st.TelegramPages, 0, 200);
        if (!SourceParser.IsValidDoh(st.ReferenceDoh, out _)) st.ReferenceDoh = new AppSettings().ReferenceDoh;
        if (!SourceParser.IsValidDoh(st.ReferenceDohFallback, out _)) st.ReferenceDohFallback = new AppSettings().ReferenceDohFallback;
        st.Sources.RemoveAll(x => !Uri.TryCreate(x.Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"));
        st.Targets.RemoveAll(t => Uri.CheckHostName(t.Host) != UriHostNameType.Dns || !t.Path.StartsWith('/'));
        // settings.json, созданный до появления OkStatusCodes: берем коды по умолчанию для известных сервисов
        var defaults = new AppSettings().Targets;
        foreach (var t in st.Targets.Where(x => x.OkStatusCodes.Count == 0))
            t.OkStatusCodes = defaults.FirstOrDefault(d => d.Name == t.Name)?.OkStatusCodes.ToList() ?? new();
        // settings.json без дополнительных хостов: добавляем хосты по умолчанию для известных сервисов
        foreach (var t in st.Targets.Where(x => x.ExtraHosts.Count == 0))
            t.ExtraHosts = defaults.FirstOrDefault(d => d.Name == t.Name)?.ExtraHosts.ToList() ?? new();
        st.Targets.ForEach(t => t.ExtraHosts.RemoveAll(h => Uri.CheckHostName(h) != UriHostNameType.Dns
                                                         || h == "console.anthropic.com")); // переехал на platform.claude.com
        // settings.json до версии 0.1.19: подпись настоящего ответа API, сайт для второй проверки, признак Gemini
        foreach (var t in st.Targets)
        {
            var d = defaults.FirstOrDefault(x => x.Name == t.Name);
            if (d == null) continue;
            if (t.OkMarkers.Count == 0) t.OkMarkers = d.OkMarkers.ToList();
            if (t.OwnerOrgs.Count == 0) t.OwnerOrgs = d.OwnerOrgs.ToList();
            t.WebCheck ??= d.WebCheck;
            t.RequiresProxy |= d.RequiresProxy;
        }
        bool changed = false;
        foreach (var k in st.HostCookies.Keys.ToList())
        {
            var enc = Storage.Protect(st.HostCookies[k]);
            if (enc != st.HostCookies[k]) { st.HostCookies[k] = enc; changed = true; }
        }
        if (changed) Storage.Save(SettingsFile, st);
        return st;
    }

    // Флаги декодируются один раз и переиспользуются: плитки и строки обновляются часто
    private static readonly Dictionary<string, ImageSource> FlagCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Настройки программы. После сохранения файл перечитывается.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var before = System.Text.Json.JsonSerializer.Serialize(_settings, Storage.Options); // для отмены
        var autoBefore = AutoStart.IsEnabled();
        var w = new SettingsWindow(_settings) { Owner = this };
        w.ShowDialog();
        if (w.RollbackRequested) { RollbackAndRestart(); return; }
        if (w.SplitChanged)
        {
            if (!w.Saved) _settings = LoadSettings(); // «Два DNS» сохраняются сразу, даже если настройки закрыли кнопкой «Отмена»
            _ = ApplySplitChangeAsync();
        }
        if (w.Saved)
        {
            _settings = LoadSettings();
            ApplyTimerSettings();
            ApplyCountryPin();
            _view?.Refresh();
            UpdateTopList();
            Log("Настройки сохранены");
            OfferUndo("Настройки сохранены", async () =>
            {
                Storage.SaveJson("settings.json", before);
                if (AutoStart.IsEnabled() != autoBefore && !await AutoStart.SetAsync(autoBefore))
                    Log("Автозапуск вернуть не удалось");
                _settings = LoadSettings();
                ApplyTimerSettings();
                ThemeColors.Apply(_settings.Colors, _settings.Theme); // отмена возвращает и прежние цвета
                // и режим скрытия данных с языком: раньше файл возвращался, а окна оставались скрытыми от записи
                PrivacyGuard.Set(_settings.PrivacyMode, _settings.PrivacyHideWindows);
                Loc.SetLanguage(_settings.Language);
                Log("Сохранение настроек отменено");
            });
        }
    }

    /// <summary>Интервалы из настроек: проверка сервисов в трее и сторож DNS. Значения ограничены разумными пределами.</summary>
    private void ApplyTimerSettings()
    {
        _trayTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.TrayCheckMinutes, 1, 60));
        _watchdog.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.WatchdogSeconds, 15, 600));
        _pingTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.PingRefreshMinutes, 1, 60));
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_initStarted && !_restarting) Updater.ConfirmStarted(); // дошли до штатного выхода: новая версия рабочая
        // Сначала убираем значок: пользователь видит, что программа закрылась, пока выключаются инструменты
        _updateResultTimer.Stop();
        _dashboardTimer.Stop();
        _trayTimer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        StopToolsOnExit();
        // Главное окно закрыто — программа завершается. Раньше завершение ждало закрытия последнего окна, и любое
        // созданное, но не закрытое окно (скрытое или открытое без владельца из трея) оставляло процесс без значка
        App.ExitNow();
    }
}
