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

namespace Dnex;

public partial class MainWindow : Window
{
    private const string ServersFile = "servers.json";
    private const string SettingsFile = "settings.json";

    private readonly ObservableCollection<DnsServer> _servers;
    private ICollectionView? _view;
    private AppSettings _settings;
    private CancellationTokenSource? _cts;
    private int _filterIndex = 5; // «Рабочие»
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<string> _known = new();
    private bool _working;                  // идет долгая операция (проверка, подключение, парсинг)
    private int _purgeTotal;                // сколько нерабочих убрано автоочисткой с запуска
    private string _dotMode = "";           // режим точки-индикатора: busy / on / idle
    private bool _locating, _locatingAgain; // идет определение локаций серверов
    private SolidColorBrush? _connBrush;    // собственная заливка кнопки «Подключить», чтобы менять цвет плавно
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsive();

        BusySpinner.Content = MakeSpinner(14, (Brush)FindResource("Accent"));
        // Завершение Windows (выключение, выход из сеанса) не должно прятать программу в трей
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;
        AddPanel.RenderTransform = new TranslateTransform();
        LogBox.RenderTransform = new TranslateTransform();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, Motion(300)));
        };

        _settings = LoadSettings();
        SetupTray(); // после настроек: пункты сервисов берутся из списка проверок
        ApplyTimerSettings();
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
        // Рабочие серверы всплывают наверх прямо во время проверки.
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Rank), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.LatencyMs), ListSortDirection.Ascending));
        if (_view is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            live.LiveSortingProperties.Add(nameof(DnsServer.Rank));
            live.LiveSortingProperties.Add(nameof(DnsServer.LatencyMs));
            live.IsLiveFiltering = true;
            live.LiveFilteringProperties.Add(nameof(DnsServer.Status));
            live.IsLiveSorting = true;
        }
        ServerGrid.ItemsSource = _view;

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _view?.Refresh(); UpdateStats(); };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _servers.CollectionChanged += (_, _) => _statsTimer.Start();

        Loaded += async (_, _) =>
        {
            Log($"Данные: {Storage.Dir}. Серверов в списке: {_servers.Count}");
            UpdateStats();
            ShowCompare();
            try { if (Storage.LoadOrCreate("ui.json", () => new Dictionary<string, bool>()).GetValueOrDefault("compact")) SetCompact(true, animate: false); }
            catch (Exception) { }
            await RefreshConnectionAsync();
            _ = RefreshTrayServicesAsync(); // первая проверка сервисов для трея сразу после запуска
            if (DnsSwitcher.GetActive() is { } a)
                Log($"Активно подключение «{a.Name}» с {a.Since:dd.MM HH:mm}.");
            SelectBest();

            _ = ResolveLocationsAsync();

            // Автопроверка при запуске: все, что не проверялось или проверялось давно.
            var stale = _servers.Where(x => x.LastChecked == null || DateTime.Now - x.LastChecked > TimeSpan.FromMinutes(30)).ToList();
            if (stale.Count > 0) await RunChecksAsync(stale);
        };
        _watchdog.Tick += Watchdog_Tick;
        Closing += OnClosing;
    }

    // ---------- Поиск и фильтр ----------

    private bool FilterServer(object o)
    {
        if (o is not DnsServer s) return false;
        var q = SearchBox?.Text.Trim() ?? "";
        if (q.Length > 0)
        {
            bool Has(string? v) => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
            if (!(Has(s.Name) || Has(s.Primary) || Has(s.Secondary) || Has(s.DohUrl) || Has(s.Source) || Has(s.Summary)))
                return false;
        }
        return _filterIndex switch
        {
            1 => s.Status == ServerStatus.Ok,
            2 => s.Status == ServerStatus.Partial,
            3 => s.Status == ServerStatus.Fail,
            4 => s.Status == ServerStatus.Unknown,
            5 => s.Status is ServerStatus.Ok or ServerStatus.Partial or ServerStatus.Checking,
            _ => true,
        };
    }

    // Поиск с задержкой 250 мс: список не пересчитывается на каждую букву.
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void FilterTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out var i)) _filterIndex = i;
        Filter_Changed(sender, e);
    }

    // Темный заголовок окна Windows 10/11 под темную тему.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        int caption = 0x001B1614; // COLORREF (BGR) = #14161B
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    private void UpdateStats()
    {
        _statsTimer.Stop();
        if (_view == null) return;
        int shown = _view.Cast<object>().Count();
        StatsText.Text = $"{_servers.Count(s => s.Status == ServerStatus.Ok)} работают   ·   " +
                         $"{_servers.Count(s => s.Status == ServerStatus.Partial)} частично   ·   " +
                         $"показано {shown} из {_servers.Count}";
    }

    // ---------- Источники ----------

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.Sources.Any(s => s.Enabled))
        {
            Log("Нет включенных источников. Откройте «Данные» → settings.json, добавьте адреса в sources, поставьте enabled: true и нажмите «Перечитать настройки».");
            return;
        }
        var ct = BeginWork();
        int purgeStart = _purgeTotal;
        try
        {
            Log("Ищу серверы в источниках…");
            var added = new List<DnsServer>();
            WorkBar.Visibility = Visibility.Visible;
            WorkBar.IsIndeterminate = false;
            var progress = new Progress<SourceParser.CrawlProgress>(p =>
            {
                WorkBar.Maximum = Math.Max(1, p.Pages + p.Queued);
                WorkBar.Value = p.Pages;
                ProgressText.Text = $"Страниц {p.Pages}/{p.MaxPages} · в очереди {p.Queued} · найдено {p.Found} · новых {added.Count} · ошибок {p.Errors} · {p.Current}";
            });
            // Найденные адреса появляются в таблице сразу.
            void OnFound(DnsServer s) => Dispatcher.InvokeAsync(() =>
            {
                if (_junk.ContainsKey(s.Key) || !_known.Add(s.Key)) return;
                s.JustAdded = true;
                _servers.Add(s);
                _ = ClearJustAddedLaterAsync(s);
                added.Add(s);
            });

            var found = await SourceParser.FetchAllAsync(_settings, Log, progress, OnFound, ct);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); // дождаться добавления
            Log($"Готово: найдено {found.Count}, новых {added.Count}");
            SaveServers();

            if (added.Count > 0)
            {
                EndWork();
                await RunChecksAsync(added);
            }
            _ = ResolveLocationsAsync();
            int purged = _purgeTotal - purgeStart;
            if (added.Count > 0 || purged > 0)
                ShowToast($"Найдено {added.Count} новых, убрано {purged} нерабочих");
        }
        catch (OperationCanceledException) { Log("Поиск остановлен"); }
        finally { EndWork(); }
    }

    // ---------- Проверка ----------

    private async void CheckAll_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(_view!.Cast<DnsServer>().ToList());

    private async void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ServerGrid.SelectedItems.Cast<DnsServer>().ToList());

    private async Task RunChecksAsync(List<DnsServer> list)
    {
        if (list.Count == 0) { Log("Нечего проверять"); return; }
        if (CurrentDns.Text.Contains("127.0.0.1") || CurrentDns.Text.Contains("::1"))
            Log("ВНИМАНИЕ: системный DNS указывает на 127.0.0.1, похоже, включен VPN или прокси-клиент. " +
                "ИИ может открываться и без Smart DNS, поэтому результаты проверки будут завышены. Отключите VPN на время проверки.");
        var ct = BeginWork();
        var checker = new ServerChecker(_settings);
        WorkBar.Visibility = Visibility.Visible;
        WorkBar.Maximum = list.Count;
        WorkBar.Value = 0;
        var progress = new Progress<int>(n =>
        {
            WorkBar.Value = n;
            ProgressText.Text = $"Проверено {n} из {list.Count} · работают {list.Count(s => s.Status == ServerStatus.Ok)}";
            _statsTimer.Start();
        });
        Log($"Проверка {list.Count} серверов…");
        try
        {
            await checker.CheckManyAsync(list, progress, ct);
        }
        catch (OperationCanceledException) { Log("Проверка остановлена"); }
        finally
        {
            EndWork();
            SaveServers();
            _ = ResolveLocationsAsync();      // после проверки появился адрес прокси — определим его страну
            _view?.Refresh();
            Log($"Итог: работают {list.Count(s => s.Status == ServerStatus.Ok)}, " +
                $"частично {list.Count(s => s.Status == ServerStatus.Partial)}, " +
                $"не работают {list.Count(s => s.Status == ServerStatus.Fail)}");
            AutoPurge();
            SelectBest();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- Применение DNS ----------

    private bool _closeConfirmed;

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
        _cts?.Cancel();
        SaveServersNow();
        if (_closeConfirmed || DnsSwitcher.GetActive() is not { } a) return;

        var r = ClosePrompt.Ask(a.Name);
        if (r == MessageBoxResult.No) return;
        e.Cancel = true;
        if (r == MessageBoxResult.Cancel) return;
        await DisconnectAsync("выход из программы");
        _closeConfirmed = true;
        Close();
    }

    // ---------- Подключение / отключение ----------

    private NetSnapshot? _before, _after;

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        VerdictText.Text = "проверяю…";
        try
        {
            var now = await ConnectionProbe.TakeAsync(_settings);
            if (_after == null && _before == null) _before = now; else _after = now;
            ShowCompare();
            SetTraySnapshot(now);
        }
        catch (Exception ex) { Log("Ошибка проверки подключения: " + ex.Message); }
        finally { ProbeButton.IsEnabled = true; }
    }

    private void CompareToggle_Click(object sender, RoutedEventArgs e)
    {
        CompareGrid.Visibility = CompareGrid.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        CompareToggle.Content = CompareGrid.Visibility == Visibility.Visible ? "Скрыть" : "Показать";
    }

    private void LogSnapshot(string title, NetSnapshot n)
    {
        Log($"{title}: IP {n.PublicIp} ({n.Location}, {n.Provider}), DNS {n.SystemDns}");
        foreach (var x in n.Services)
            Log($"  {x.Name}: {x.Host} → {x.ResolvedIp} {(x.Proxied ? "(через Smart DNS)" : "(напрямую)")}, {x.Access}");
    }

    /// <summary>
    /// Блок «До / После»: строка с внешним IP и DNS, ниже по плитке на каждый ИИ-сервис —
    /// крупно текущий доступ, мельче «было» и адрес, через который идет сервис.
    /// </summary>
    private void ShowCompare(bool disconnected = false)
    {
        Brush B(string k) => (Brush)FindResource(k);
        var host = CompareGrid;
        host.Children.Clear();
        _tileGeo.Clear();

        var cur = _after ?? _before;
        if (cur == null)
        {
            host.Children.Add(new TextBlock
            {
                Text = "Нажмите «Подключить», чтобы увидеть сравнение до и после.",
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap,
            });
            VerdictText.Text = "еще не проверялось";
            VerdictText.Foreground = B("Muted");
            return;
        }

        TextBlock Line(params (string text, Brush brush, bool bold)[] runs)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var (t, br, bold) in runs)
                tb.Inlines.Add(new System.Windows.Documents.Run(t) { Foreground = br, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
            return tb;
        }

        // Строка 1: внешний IP (при Smart DNS не меняется — так и должно быть)
        host.Children.Add(Line(
            ("Внешний IP  ", B("Muted"), false), (cur.PublicIp, B("Text"), true),
            ($"   ·   {cur.Location}   ·   {cur.Provider}", B("Muted"), false),
            (_before != null && _after != null && _before.PublicIp == _after.PublicIp ? "   ·   не меняется" : "", B("Muted"), false)));

        // Строка 2: DNS системы до → после
        var dnsRuns = new List<(string, Brush, bool)> { ("DNS системы  ", B("Muted"), false) };
        if (_before != null && _after != null && _before.SystemDns != _after.SystemDns)
        {
            dnsRuns.Add((_before.SystemDns, B("Muted"), false));
            dnsRuns.Add(("  →  ", B("Accent"), true));
            dnsRuns.Add((_after.SystemDns, B("Text"), true));
        }
        else dnsRuns.Add((cur.SystemDns, B("Text"), true));
        host.Children.Add(Line(dnsRuns.ToArray()));

        // Плитки сервисов: статус доступа сверху, страна (флаг и название) под ним
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(-4, 8, -4, 0) };
        foreach (var svc in cur.Services)
        {
            var was = _after != null ? _before?.Services.FirstOrDefault(x => x.Name == svc.Name) : null;
            bool ok = svc.Access.StartsWith("✓");
            var card = new Border
            {
                Background = new SolidColorBrush(((SolidColorBrush)B("Surface2")).Color), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = ok ? B("Ok") : B("Bad"),
            };
            var sp = new StackPanel();
            var head = new DockPanel();
            head.Children.Add(new TextBlock { Text = svc.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var state = new TextBlock { Text = svc.Access.TrimStart('✓', '✗', ' '), Foreground = ok ? B("Ok") : B("Bad"),
                                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(state, Dock.Right);
            head.Children.Insert(0, state);
            sp.Children.Add(head);
            var geo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _tileGeo[svc.Name] = geo;
            sp.Children.Add(geo);
            sp.Children.Add(Line(
                (svc.Proxied ? "через Smart DNS  " : "напрямую  ", svc.Proxied ? B("Accent") : B("Muted"), false),
                (svc.ResolvedIp, B("Muted"), false)));
            if (was != null)
                sp.Children.Add(Line(("было: ", B("Muted"), false),
                    ($"{was.Access.TrimStart('✓', '✗', ' ')}, {(was.Proxied ? "через Smart DNS" : "напрямую")}", B("Muted"), false)));
            card.Child = sp;
            AnimateTile(card, (SolidColorBrush)card.Background, tiles.Children.Count,
                changed: was != null && (was.Access != svc.Access || was.Proxied != svc.Proxied));
            tiles.Children.Add(card);
        }
        host.Children.Add(tiles);
        FillServiceGeo(cur);
        _ = ResolveServiceGeoAsync(cur);

        // Итог
        int proxied = cur.Services.Count(x => x.Proxied);
        int open = cur.Services.Count(x => x.Access.StartsWith("✓"));
        int total = cur.Services.Count;
        (string text, string color) v =
            disconnected ? (proxied == 0 ? "отключено, сервисы идут напрямую" : "отключено, но адреса еще подменены: перезапустите браузер", proxied == 0 ? "Ok" : "Warn")
            : DnsSwitcher.GetActive() == null ? ($"не подключено · открываются {open} из {total}", "Muted")
            : proxied > 0 && open == total ? ($"работает · все {total} сервиса доступны", "Ok")
            : proxied > 0 ? ($"частично · доступно {open} из {total}", "Warn")
            : ("DNS сменился, но адреса ИИ не подменяются", "Bad");
        VerdictText.Text = v.text;
        VerdictText.Foreground = B(v.color);
    }

    // ---------- Выбранный сервер ----------

    private void UpdateSwitchButton()
    {
        var active = DnsSwitcher.GetActive();
        SwitchButton.Visibility = active != null && ServerGrid.SelectedItem is DnsServer s && s.Id != active.ServerId
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSwitchButton();
        var sel = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        Brush B(string k) => (Brush)FindResource(k);
        if (sel.Count == 0)
        {
            SelectionIcon.Fill = B("Idle");
            SelectionTitle.Text = "Сервер не выбран";
            SelectionDetails.Text = "Щелкните строку, чтобы выбрать. Двойной щелчок подключает к серверу.";
            return;
        }
        if (sel.Count > 1)
        {
            SelectionIcon.Fill = B("Accent");
            SelectionTitle.Text = $"Выбрано: {sel.Count}";
            SelectionDetails.Text = "Можно проверить или удалить выбранные";
            return;
        }
        var s = sel[0];
        SelectionIcon.Fill = B(s.Status switch { ServerStatus.Ok => "Ok", ServerStatus.Partial => "Warn", ServerStatus.Fail => "Bad", _ => "Accent" });
        SelectionTitle.Text = s.DisplayName;
        var parts = new List<string> { s.Address };
        if (!string.IsNullOrEmpty(s.DohUrl) && !string.IsNullOrEmpty(s.Primary)) parts.Add(s.DohUrl!);
        parts.Add(s.StatusText.ToLowerInvariant());
        if (s.LatencyMs != null) parts.Add($"пинг {s.PingText} мс");
        if (s.ChecksTotal > 0) parts.Add($"стабильность {s.Stability}");
        SelectionDetails.Text = string.Join("   ·   ", parts);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------- Компактный / полный режим ----------

    private bool _compact;
    private Rect _fullBounds;

    private void Mode_Click(object sender, RoutedEventArgs e) => SetCompact(!_compact);

    private void SetCompact(bool compact, bool animate = true)
    {
        _compact = compact;
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        ToolbarCard.Visibility = v;
        BottomPanel.Visibility = v;
        StatsText.Visibility = v;
        SettingsButton.Visibility = v;
        DataButton.Visibility = v;
        Watchdog.Visibility = v;
        // Компактный вид: одна кнопка подключения, плашки сервисов, таблица только со статусом, именем и пингом
        var cv = compact ? Visibility.Visible : Visibility.Collapsed;
        VerdictCard.Visibility = v;
        SelectionBar.Visibility = v;
        ApplyActions.Visibility = v;
        HeaderSep.Visibility = v;
        Subtitle.Visibility = v;
        AppTitle.Visibility = v;
        DnsLine.Visibility = v;
        CompactConnect.Visibility = cv;
        ConnText.MaxWidth = compact ? 200 : 300;
        ConnPill.Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(28, 0, 0, 0);
        RootGrid.Margin = compact ? new Thickness(12) : new Thickness(20, 16, 20, 16);
        ColStatus.Width = compact ? new DataGridLength(40) : new DataGridLength(128);
        ColStatus.Header = compact ? "" : "СТАТУС"; // в узкой колонке заголовок обрезается, точка и так понятна
        ColPing.Width = compact ? new DataGridLength(130) : new DataGridLength(140);
        ServerGrid.RowStyle = compact ? (Style)FindResource("RowCompact") : null;
        ServerGrid.Tag = compact ? "compact" : null; // по нему строки прячут текст статуса (см. XAML)
        ModeButton.Content = compact ? "Полный вид" : "Компактно";
        if (compact)
        {
            // В компактном виде — только рабочие серверы.
            _filterIndex = 5;
            _view?.Refresh();
            _fullBounds = new Rect(Left, Top, Width, Height);
            MinWidth = 360; MinHeight = 420;
            WindowState = WindowState.Normal;
            // Статистика — отдельной строкой под кнопкой подключения: в узкой шапке ей не хватает места
            Grid.SetRow(HeaderStats, 2); Grid.SetColumn(HeaderStats, 0); Grid.SetColumnSpan(HeaderStats, 3);
            HeaderStats.Margin = new Thickness(0, 10, 0, 0);
            HeaderStats.HorizontalAlignment = HorizontalAlignment.Center; // в компактном виде статистика по центру
            ResizeTo(485, 775, animate);
        }
        else
        {
            MinWidth = 1000; MinHeight = 560;
            Grid.SetRow(HeaderStats, 0); Grid.SetColumn(HeaderStats, 1); Grid.SetColumnSpan(HeaderStats, 1);
            HeaderStats.Margin = new Thickness(18, 0, 0, 0);
            HeaderStats.HorizontalAlignment = HorizontalAlignment.Left;
            if (_fullBounds.Width > 0) ResizeTo(_fullBounds.Width, _fullBounds.Height, animate);
        }
        ApplyResponsive();
        try { Storage.Save("ui.json", new Dictionary<string, bool> { ["compact"] = compact }); } catch (Exception) { }
    }

    // ---------- Адаптивная раскладка ----------

    /// <summary>
    /// Ширина окна решает, что переносится и что скрывается. Приоритет: сначала уходят второстепенные колонки таблицы,
    /// потом шапка переносит статистику и кнопки действий на вторую строку. В компактном виде раскладку задает SetCompact.
    /// </summary>
    private void ApplyResponsive()
    {
        if (ApplyActions == null || HeaderGrid == null) return;
        var w = ActualWidth;
        bool full = !_compact;

        // Таблица: по мере сужения окна уходят источник, стабильность и оценка
        ColLocation.Visibility = ColServices.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        // Ширины колонок (см. XAML): без источника и без стабильности и оценки таблица нужна от ~1100 px,
        // с ними от ~1360 px, со всеми колонками от ~1540 px окна.
        ColSource.Visibility = full && w >= 1540 ? Visibility.Visible : Visibility.Collapsed;
        ColStab.Visibility = ColScore.Visibility = full && w >= 1360 ? Visibility.Visible : Visibility.Collapsed;

        if (!full) return; // компактный вид шапку раскладывает сам

        // Шапка: ниже 1480 кнопки действий и статистика уходят на вторую строку.
        // На 1350 статистика в одной строке сжималась в столбик и ломала сетку, поэтому порог выше.
        bool narrow = w < 1480;
        _navPanel ??= ApplyActions.Parent as StackPanel;
        if (narrow && ApplyActions.Parent == _navPanel)
        {
            _navPanel?.Children.Remove(ApplyActions);
            HeaderGrid.Children.Add(ApplyActions);
            Grid.SetRow(ApplyActions, 1);
            Grid.SetColumn(ApplyActions, 2);
            ApplyActions.HorizontalAlignment = HorizontalAlignment.Right;
            ApplyActions.Margin = new Thickness(0, 10, 0, 0);
        }
        else if (!narrow && ApplyActions.Parent != _navPanel && _navPanel != null)
        {
            (ApplyActions.Parent as Panel)?.Children.Remove(ApplyActions);
            _navPanel.Children.Add(ApplyActions);
            ApplyActions.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyActions.Margin = new Thickness(0);
        }

        Grid.SetRow(HeaderStats, narrow ? 1 : 0);
        Grid.SetColumn(HeaderStats, narrow ? 0 : 1);
        HeaderStats.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(18, 0, 0, 0);
        HeaderSep.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private StackPanel? _navPanel; // исходный контейнер кнопок действий в шапке

    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(60) };
    private int _watchdogFails;
    private bool _busyConn;

    private static readonly string[] ProbeHosts = { "ya.ru", "vk.com", "google.com", "cloudflare.com" };

    /// <summary>Главная кнопка: не подключено → подключить к выбранному (или к лучшему); подключено → отключить.</summary>
    private async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (DnsSwitcher.GetActive() != null) { await DisconnectAsync("по запросу"); return; }
        SelectBest();
        if (ServerGrid.SelectedItem is DnsServer s) await ConnectToAsync(s);
        else Log("Рабочих серверов нет. Нажмите «Найти новые серверы».");
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer s) await ConnectToAsync(s);
    }

    /// <summary>Двойной щелчок / Enter / меню: подключиться или переключиться на выбранный.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer s && DnsSwitcher.GetActive()?.ServerId != s.Id) await ConnectToAsync(s);
    }

    private async Task ConnectToAsync(DnsServer s)
    {
        if (_busyConn) return;
        _busyConn = true;
        BeginWork();
        SetConnectContent("Подключаю…");
        try
        {
            // 1. Проверка перед подключением — если последняя была давно или неудачной (свежую не повторяем, это экономит время).
            if (s.Status is not (ServerStatus.Ok or ServerStatus.Partial) || s.LastChecked == null ||
                DateTime.Now - s.LastChecked > TimeSpan.FromMinutes(10))
            {
                ProgressText.Text = $"Проверяю «{s.DisplayName}»…";
                await new ServerChecker(_settings).CheckAsync(s, CancellationToken.None);
            }
            if (s.Status == ServerStatus.Fail &&
                MessageBox.Show($"«{s.Name}» сейчас не работает:\n{s.LastError}\n\nВсе равно подключиться?", "DNEX",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            // Снимок «до»: внешний IP, DNS, куда сейчас резолвятся ИИ-сервисы и открываются ли они.
            ProgressText.Text = "Запоминаю состояние до подключения…";
            _after = null;
            _before = await ConnectionProbe.TakeAsync(_settings);
            ShowCompare();

            // 2. Смена DNS (UAC).
            ProgressText.Text = "Меняю DNS…";
            await DnsSwitcher.ApplyAsync(s, _settings, Log);

            // 3. Проверка, что интернет после смены жив. Если нет — автоматический откат.
            ProgressText.Text = "Проверяю интернет…";
            await Task.Delay(800);
            if (!await DnsSwitcher.SystemDnsWorksAsync(ProbeHosts, TimeSpan.FromSeconds(8)))
            {
                Log("После подключения сайты не открываются. Откатываю настройки.");
                await DnsSwitcher.RestoreAsync(Log);
                ShowToast("Откат: сервер не отвечает", "Bad");
                MessageBox.Show("Сервер не отвечает системе: настройки DNS возвращены как были.", "DNEX",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // Снимок «после» — по нему видно, действительно ли трафик ИИ пошел через Smart DNS.
                ProgressText.Text = "Проверяю результат подключения…";
                _after = await ConnectionProbe.TakeAsync(_settings);
                ShowCompare();
                SetTraySnapshot(_after);
                LogSnapshot("После подключения", _after);
                ShowToast($"Подключено · {s.DisplayName}");
            }
        }
        catch (Exception ex) { Log("Ошибка подключения: " + ex.Message); }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await DisconnectAsync("по запросу");

    private async Task DisconnectAsync(string reason)
    {
        if (_busyConn) return;
        _busyConn = true;
        BeginWork();
        try
        {
            SetConnectContent("Отключаю…");
            ProgressText.Text = "Отключаю…";
            _after = null;
            _before = await ConnectionProbe.TakeAsync(_settings); // «до» = состояние в подключенном виде
            await DnsSwitcher.RestoreAsync(Log);
            Log($"Отключено ({reason})");
            ShowToast("Отключено", "Idle");
            await Task.Delay(500);
            _after = await ConnectionProbe.TakeAsync(_settings);
            ShowCompare(disconnected: true);
            LogSnapshot("После отключения", _after);
        }
        catch (Exception ex) { Log("Ошибка отключения: " + ex.Message); }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
    }

    /// <summary>Обновляет плашку и кнопки по фактическому состоянию (файл подключения + системный DNS).</summary>
    private async Task RefreshConnectionAsync()
    {
        await RefreshCurrentDnsAsync();
        var active = DnsSwitcher.GetActive();
        bool on = active != null;
        // Стиль кнопки не меняем: цвет плавно переходит от синего к красному (см. AnimateConnectColor).
        var connText = on ? "Отключить" : "Подключить";
        ConnectButton.Content = connText;
        CompactConnect.Content = connText;
        ConnectButton.ToolTip = on ? "Вернуть исходные настройки DNS" : "Подключиться к выбранному серверу. По умолчанию выбирается лучший";
        AnimateConnectColor(on ? "BadButton" : "AccentButton");
        UpdateSwitchButton();
        var activeName = on ? (_servers.FirstOrDefault(x => x.Id == active!.ServerId)?.ShortName ?? DnsServer.Clip(active!.Name)) : null;
        ConnText.Text = on ? $"Подключено · {activeName}" : "Не подключено";
        UpdateTray(on ? _servers.FirstOrDefault(x => x.Id == active!.ServerId) : null, activeName);
        UpdateTrayServices();
        UpdateHeaderStats();
        ConnPill.BorderBrush = (System.Windows.Media.Brush)FindResource(on ? "Ok" : "Border");
        ConnPill.Background = (System.Windows.Media.Brush)FindResource(on ? "OkSoft" : "Surface");
        _watchdogFails = 0;
        if (on) _watchdog.Start(); else _watchdog.Stop();
        UpdateConnectionLocation();
        UpdateStatusVisuals();
    }

    /// <summary>Сторож: пока подключено, проверяет сервер. 3 неудачи подряд — безопасное отключение,
    /// чтобы компьютер не остался без интернета из-за умершего сервера.</summary>
    private async void Watchdog_Tick(object? sender, EventArgs e)
    {
        if (Watchdog.IsChecked != true || _busyConn) return;
        var active = DnsSwitcher.GetActive();
        if (active == null) { _watchdog.Stop(); return; }

        bool ok = await DnsSwitcher.SystemDnsWorksAsync(ProbeHosts, TimeSpan.FromSeconds(8));
        if (ok) { _watchdogFails = 0; return; }
        _watchdogFails++;
        Log($"Сторож: DNS не отвечает ({_watchdogFails}/3)");
        if (_watchdogFails >= 3)
        {
            await DisconnectAsync("сервер перестал отвечать");
            MessageBox.Show($"Сервер «{active.Name}» перестал отвечать. DNEX отключилась и вернула исходные настройки DNS.",
                "DNEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной клик по нередактируемой колонке (статус, сервисы и т.п.) = применить.
        // Только по строке (не по заголовку и не по кнопке «Открыть»).
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(ServerGrid, d) is DataGridRow { Item: DnsServer } &&
            FindParent<Button>(d) == null)
            Connect_Click(sender, e);
    }

    private void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Connect_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { MenuCopy_Click(sender, e); e.Handled = true; }
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var s = ServerGrid.SelectedItem as DnsServer;
        bool isActive = s != null && DnsSwitcher.GetActive()?.ServerId == s.Id;
        MenuConnect.Header = DnsSwitcher.GetActive() == null ? "Подключить" : "Переключить сюда";
        MenuConnect.IsEnabled = s != null && !isActive;
        MenuSource.IsEnabled = s?.HasSourceUrl == true;
    }

    private void MenuConnect_Click(object sender, RoutedEventArgs e) => Connect_Click(sender, e);

    private void MenuSource_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer { HasSourceUrl: true } s)
            Process.Start(new ProcessStartInfo(new Uri(s.SourceUrl!).AbsoluteUri) { UseShellExecute = true });
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, ServerGrid.SelectedItems.Cast<DnsServer>()
            .Select(s => string.Join(" ", new[] { s.Primary, s.Secondary, s.DohUrl }.Where(x => !string.IsNullOrWhiteSpace(x)))));
        if (text.Length > 0) { try { Clipboard.SetText(text); Log("Адрес скопирован"); } catch (Exception) { } }
    }

    private async Task RefreshCurrentDnsAsync()
    {
        try { CurrentDns.Text = await DnsSwitcher.GetCurrentAsync(); }
        catch (Exception ex) { CurrentDns.Text = "ошибка: " + ex.Message; }
    }

    // ---------- Редактирование списка ----------

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var primary = AddPrimary.Text.Trim();
        var secondary = AddSecondary.Text.Trim();
        var doh = AddDoh.Text.Trim();

        bool hasIp = IPAddress.TryParse(primary, out _);
        bool hasDoh = Uri.TryCreate(doh, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        if (!hasIp && !hasDoh) { Log("Укажите корректный основной IP или DoH-адрес (https://…)"); return; }
        if (secondary.Length > 0 && !IPAddress.TryParse(secondary, out _)) { Log("Некорректный резервный IP"); return; }

        var s = new DnsServer
        {
            Name = string.IsNullOrWhiteSpace(AddName.Text) ? (hasIp ? primary : u!.Host) : AddName.Text.Trim(),
            Primary = hasIp ? primary : "",
            Secondary = secondary.Length > 0 ? secondary : null,
            DohUrl = hasDoh ? doh : null,
        };
        if (_known.Contains(s.Key)) { Log("Такой сервер уже есть"); return; }

        _known.Add(s.Key);
        s.JustAdded = true;
        _servers.Add(s);
        _ = ClearJustAddedLaterAsync(s);
        SaveServers();
        AddName.Clear(); AddPrimary.Clear(); AddSecondary.Clear(); AddDoh.Clear();
        // Новый сервер еще не проверен, фильтр «Рабочие» его скрыл бы: показываем все и выбираем добавленный
        AllTab.IsChecked = true;
        SearchBox.Clear();
        _view?.Refresh();
        ServerGrid.SelectedItem = s;
        ServerGrid.ScrollIntoView(s);
        Log($"Добавлен «{s.Name}», проверяю");
        _ = RunChecksAsync(new List<DnsServer> { s });
        _ = ResolveLocationsAsync();
    }

    // ---------- Строка и пинг ----------

    /// <summary>Кнопка в строке: полная проверка этого сервера (сервисы и пинг).</summary>
    private async void RowRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DnsServer s) await RunChecksAsync(new List<DnsServer> { s });
    }

    /// <summary>
    /// Клик без Ctrl и Shift выбирает одну строку. Протаскивание мышью список не выделяет:
    /// событие перехватывается до того, как таблица начнет выделение диапазоном.
    /// Кнопки в строке (обновить, открыть источник) работают как обычно.
    /// </summary>
    private void ServerGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || e.ClickCount > 1) return;
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not DnsServer s) return;
        e.Handled = true;
        ServerGrid.SelectedItems.Clear();
        ServerGrid.SelectedItem = s;
        ServerGrid.Focus();
    }

    /// <summary>Пинг всех серверов в таблице: тот же DNS-запрос, что и при проверке. Раз в N минут из настроек.</summary>
    private bool _pinging;
    private readonly DispatcherTimer _pingTimer = new() { Interval = TimeSpan.FromMinutes(10) };

    private async Task RefreshPingsAsync()
    {
        if (_pinging) return;
        var list = _servers.Where(s => s.Status != ServerStatus.Checking).ToList();
        if (list.Count == 0) return;
        _pinging = true;
        var checker = new ServerChecker(_settings);
        var results = new ConcurrentBag<(DnsServer Server, (long Median, long? Jitter) Ping)>();
        try
        {
            await Parallel.ForEachAsync(list, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (s, ct) =>
            {
                var ping = await checker.MeasurePingAsync(s, ct).ConfigureAwait(false);
                if (ping is { } p) results.Add((s, p));
            });
            // После await мы снова в потоке интерфейса: свойства трогаем без диспетчера
            foreach (var (s, p) in results)
            {
                s.LatencyMs = p.Median;
                s.JitterMs = p.Jitter;
            }
            UpdateHeaderStats();
            Grid_SelectionChanged(this, null!); // строка выбранного сервера под таблицей тоже показывает пинг
            var active = DnsSwitcher.GetActive();
            if (active != null)
            {
                var srv = _servers.FirstOrDefault(x => x.Id == active.ServerId);
                UpdateTray(srv, srv?.ShortName ?? DnsServer.Clip(active.Name));
            }
        }
        catch (Exception ex)
        {
            Log("Обновление пинга не удалось: " + ex.Message);
        }
        finally
        {
            _pinging = false;
        }
    }

    // ---------- Автоочистка ----------

    private const string JunkFile = "junk.json";
    private static readonly TimeSpan JunkTtl = TimeSpan.FromDays(14);
    /// <summary>Удаленные серверы (ключ → когда). 14 дней их не добавляем снова — ни из каталога, ни из парсера.</summary>
    private Dictionary<string, DateTime> _junk = new();

    /// <summary>
    /// Убирает мусор без участия пользователя: обычные DNS и заглушки сразу; серверы, которые не подменяют
    /// адреса ИИ (это обычный DNS) — после первой проверки; нерабочие — после 2 неудач подряд.
    /// Текущий подключенный сервер не трогаем никогда.
    /// </summary>
    private int AutoPurge(bool silent = false)
    {
        var activeId = DnsSwitcher.GetActive()?.ServerId;
        var junk = _servers.Where(s => s.Id != activeId && s.Status != ServerStatus.Checking &&
                                       (SourceParser.IsNoise(s) || s.IsJunk)).ToList();
        if (junk.Count == 0) return 0;
        foreach (var s in junk)
        {
            _known.Remove(s.Key);
            _junk[s.Key] = DateTime.Now;
        }
        try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
        _purgeTotal += junk.Count;
        if (silent) foreach (var s in junk) _servers.Remove(s);
        else _ = FadeOutAndRemoveAsync(junk);
        SaveServers();
        if (!silent) Log($"Автоочистка: убрано нерабочих {junk.Count}, осталось {_servers.Count - junk.Count}");
        return junk.Count;
    }

    /// <summary>Если ничего не выбрано — выделяем лучший сервер (верх отсортированного списка).</summary>
    private void SelectBest()
    {
        if (ServerGrid.SelectedItem != null || _view == null) return;
        var best = _view.Cast<DnsServer>().FirstOrDefault(s => s.Status is ServerStatus.Ok or ServerStatus.Partial);
        if (best == null) return;
        ServerGrid.SelectedItem = best;
        ServerGrid.ScrollIntoView(best);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        if (sel.Count == 0) return;
        if (sel.Count > 1 && MessageBox.Show($"Удалить {sel.Count} серверов?", "DNEX", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var s in sel) { _servers.Remove(s); _known.Remove(s.Key); _junk[s.Key] = DateTime.Now; }
        try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
        SaveServers();
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.InvokeAsync(SaveServers);

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DnsServer { HasSourceUrl: true } s)
            Process.Start(new ProcessStartInfo(new Uri(s.SourceUrl!).AbsoluteUri) { UseShellExecute = true }); // только http/https — проверено в HasSourceUrl
    }

    // ---------- Настройки ----------

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Storage.Dir) { UseShellExecute = true });

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _settings = LoadSettings();
        Log($"Настройки перечитаны: источников {_settings.Sources.Count(s => s.Enabled)}, сервисов для проверки {_settings.Targets.Count(t => t.Enabled)}");
    }

    /// <summary>Загружает настройки и сразу шифрует открытые Cookie, если пользователь вписал их вручную.</summary>
    private static AppSettings LoadSettings()
    {
        var st = Storage.LoadOrCreate(SettingsFile, () => new AppSettings());
        Catalog.Merge(st);
        // Проверка значений из файла: без отрицательных/огромных лимитов, только https для эталонного DNS.
        st.TimeoutSeconds = Math.Clamp(st.TimeoutSeconds, 2, 30);
        st.Parallelism = Math.Clamp(st.Parallelism, 1, 64);
        st.PingRefreshMinutes = Math.Clamp(st.PingRefreshMinutes, 1, 60);
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
        bool changed = false;
        foreach (var k in st.HostCookies.Keys.ToList())
        {
            var enc = Storage.Protect(st.HostCookies[k]);
            if (enc != st.HostCookies[k]) { st.HostCookies[k] = enc; changed = true; }
        }
        if (changed) Storage.Save(SettingsFile, st);
        return st;
    }

    // ---------- Страны ИИ-адресов ----------

    // IP → коды стран по двум базам (ipinfo, ipwho.is); null, если база не ответила
    private readonly Dictionary<string, (string? First, string? Second)> _ipCountry = new();
    private readonly Dictionary<string, StackPanel> _tileGeo = new(); // строка страны в каждой плитке сервиса

    // Через Smart DNS сервис отдает адрес прокси в другой стране — его и показываем флагом.
    // Напрямую адрес настоящий (часто anycast Cloudflare), база его локации не отражает — не геолокируем.
    private void FillServiceGeo(NetSnapshot cur)
    {
        var muted = (Brush)FindResource("Muted");
        foreach (var s in cur.Services)
        {
            if (!_tileGeo.TryGetValue(s.Name, out var row)) continue;
            row.Children.Clear();
            row.Visibility = s.Proxied ? Visibility.Visible : Visibility.Collapsed; // напрямую страну не показываем
            if (!s.Proxied) continue;

            var code = CodeOfIp(s.ResolvedIp);
            if (code != null && LoadFlag(GeoLocation.FlagPath(code) ?? "") is { } flag)
                row.Children.Add(new Image { Source = flag, Width = 20, Height = 14, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform });
            var name = CountryOfIp(s.ResolvedIp);
            var warn = _ipCountry.TryGetValue(s.ResolvedIp, out var r) && r.First != null && r.Second != null && r.First != r.Second;
            row.Children.Add(new TextBlock { Text = warn ? name + " ⚠" : name, Foreground = muted, FontWeight = FontWeights.SemiBold,
                                            ToolTip = warn ? "Базы определяют страну по-разному" : null });
        }
    }

    private string? CodeOfIp(string ip) =>
        _ipCountry.TryGetValue(ip, out var r) ? r.First ?? r.Second : null;

    private string CountryOfIp(string ip)
    {
        if (!GeoLocation.IsPublicIPv4(ip)) return "локальный адрес";
        if (!_ipCountry.TryGetValue(ip, out var r)) return "определяется…";
        if (r.First == null && r.Second == null) return "неизвестно";
        if (r.First != null && r.Second != null && r.First != r.Second)
            return $"{GeoLocation.CountryName(r.First)} / {GeoLocation.CountryName(r.Second)}, базы расходятся";
        return GeoLocation.CountryName((r.First ?? r.Second)!);
    }

    private async Task ResolveServiceGeoAsync(NetSnapshot cur)
    {
        foreach (var s in cur.Services)
        {
            if (!s.Proxied || !GeoLocation.IsPublicIPv4(s.ResolvedIp) || _ipCountry.ContainsKey(s.ResolvedIp)) continue;
            var (first, _) = await GeoLocation.LookupAsync(s.ResolvedIp, CancellationToken.None);
            var second = await GeoLocation.LookupSecondAsync(s.ResolvedIp, CancellationToken.None);
            _ipCountry[s.ResolvedIp] = (first, second);
        }
        // Флаги нужных стран: скачиваем один раз, потом берем из кэша
        foreach (var code in _ipCountry.Values.Select(r => r.First ?? r.Second).Where(c => c != null).Distinct())
            await GeoLocation.FlagPathAsync(code!, CancellationToken.None);
        FillServiceGeo(cur);
    }

    // ---------- Локация DNS-сервера ----------

    /// <summary>Определяет страну и город серверов без данных или с устаревшими данными (старше 30 дней).</summary>
    private async Task ResolveLocationsAsync()
    {
        if (_locating) { _locatingAgain = true; return; } // уже идет — повторим после окончания
        _locating = true;
        try
        {
            do
            {
                _locatingAgain = false;
                await ResolveLocationsPassAsync();
            } while (_locatingAgain);
        }
        finally { _locating = false; }
    }

    private async Task ResolveLocationsPassAsync()
    {
        // Флаги уже известных стран берем с диска, без сети
        foreach (var s in _servers.ToList())
            if (s.Flag == null && !string.IsNullOrEmpty(s.Country) && GeoLocation.FlagPath(s.Country) is { } fp)
                s.Flag = LoadFlag(fp);

        // Нет данных, данные старше 30 дней или вторая база еще не сверяла (повторим при следующем запуске)
        var todo = _servers.Where(s => s.GeoCheckedAt == null || DateTime.Now - s.GeoCheckedAt > TimeSpan.FromDays(30)
                                       || (s.Country != null && s.Country2 == null)).ToList();
        // Прокси Smart DNS, которому еще не определили страну (адрес появляется после проверки)
        var proxyTodo = _servers.Where(s => !string.IsNullOrEmpty(s.ProxyIp) && s.ProxyCountry == null).ToList();

        await Task.WhenAll(todo.Select(ResolveOneAsync).Concat(proxyTodo.Select(ResolveProxyAsync)));
        if (todo.Count > 0 || proxyTodo.Count > 0) SaveServers();   // страны должны пережить перезапуск
        UpdateConnectionLocation();
    }

    /// <summary>Страна адреса прокси Smart DNS — той локации, через которую на самом деле идет доступ к ИИ.</summary>
    private async Task ResolveProxyAsync(DnsServer s)
    {
        var ip = s.ProxyIp!;
        if (!GeoLocation.IsPublicIPv4(ip)) { s.SetProxyLocation(null, null); return; }
        var (c1, _) = await GeoLocation.LookupAsync(ip, CancellationToken.None);
        if (c1 == null) return;                                    // сеть или лимит — попробуем позже
        var c2 = await GeoLocation.LookupSecondAsync(ip, CancellationToken.None);
        s.SetProxyLocation(c1, c2);
    }

    private async Task ResolveOneAsync(DnsServer s)
    {
        var ip = s.Primary;
        if (string.IsNullOrEmpty(ip) && Uri.TryCreate(s.DohUrl, UriKind.Absolute, out var u))
            ip = await HostIpAsync(u.Host);
        if (string.IsNullOrEmpty(ip)) return;                        // адрес не узнали — попробуем в следующий раз
        if (!GeoLocation.IsPublicIPv4(ip)) { s.SetLocation(null, null); return; }

        var (country, city) = await GeoLocation.LookupAsync(ip, CancellationToken.None);
        if (country == null) return;                                 // сеть или лимит сервиса — попробуем позже
        var country2 = await GeoLocation.LookupSecondAsync(ip, CancellationToken.None);
        s.SetLocation(country, city, country2);
        if (await GeoLocation.FlagPathAsync(country, CancellationToken.None) is { } path)
            s.Flag = LoadFlag(path);
    }

    private static async Task<string?> HostIpAsync(string host)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception) { return null; }
    }

    private static ImageSource? LoadFlag(string path)
    {
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Флаг и место текущего подключения в шапке.</summary>
    private void UpdateConnectionLocation()
    {
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        ConnFlag.Source = srv?.Flag;
        ConnFlag.Visibility = srv?.Flag != null ? Visibility.Visible : Visibility.Collapsed;
        var text = srv == null ? "" : srv.LocationCell;
        ConnLocation.Text = text;
        ConnLocation.ToolTip = srv?.LocationTooltip;
        ConnLocation.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Трей ----------

    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripLabel? _trayStatus;
    private bool _exitRequested;
    private TrayPingStrip? _trayPing;
    private System.Windows.Forms.ToolStripLabel? _trayStab;
    private TrayServiceStrip? _traySvc;
    private NetSnapshot? _traySnap;
    private bool _trayBusy;

    /// <summary>Фоновая проверка сервисов для трея: раз в 5 минут. Таймер перезапускается после каждой проверки.</summary>
    private readonly DispatcherTimer _trayTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    /// <summary>Запоминает свежий снимок для трея и перезапускает отсчет до следующей проверки.</summary>
    private void SetTraySnapshot(NetSnapshot? snap)
    {
        if (snap != null) _traySnap = snap;
        UpdateTrayServices();
        UpdateHeaderStats();
        _trayTimer.Stop();
        _trayTimer.Start();
    }

    /// <summary>Строки статистики в шапке справа от карточки подключения: пинг, стабильность, сервисы.</summary>
    private void UpdateHeaderStats()
    {
        var active = DnsSwitcher.GetActive();
        HeaderStats.Visibility = active == null ? Visibility.Collapsed : Visibility.Visible;
        if (active == null) return;
        var srv = _servers.FirstOrDefault(x => x.Id == active.ServerId);
        StatPing.Text = srv?.LatencyMs == null ? "—" : $"{srv.PingText} мс";
        StatPingBars.Latency = srv?.LatencyMs;
        StatStab.Text = srv?.Stability ?? "—";
        var snap = _traySnap;
        StatServices.Text = snap == null ? "—" : $"{snap.Services.Count(x => x.Access.StartsWith("✓"))} из {snap.Services.Count}";
    }

    /// <summary>Проверяет сервисы в фоне для трея. Повторный вызов во время проверки пропускается.</summary>
    private async Task RefreshTrayServicesAsync()
    {
        if (_trayBusy) return;
        _trayBusy = true;
        try
        {
            SetTraySnapshot(await ConnectionProbe.TakeAsync(_settings));
        }
        catch (Exception ex)
        {
            Log("Трей: проверка сервисов не удалась: " + ex.Message);
        }
        finally
        {
            _trayBusy = false;
            if (!_trayTimer.IsEnabled) _trayTimer.Start(); // после ошибки отсчет все равно идет
        }
    }

    private void SetupTray()
    {
        // Меню узкое и высокое: ИИ-сервисы — ряд значков в одну строку, пинг и стабильность — отдельными строками.
        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
            BackColor = System.Drawing.ColorTranslator.FromHtml("#1C1F26"),
            ForeColor = System.Drawing.ColorTranslator.FromHtml("#E7E9EE"),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new System.Windows.Forms.Padding(4),
        };
        var pad = new System.Windows.Forms.Padding(10, 8, 10, 8);
        // Строки статуса не выделяются при наведении: это надписи, а не пункты.
        _trayStatus = new System.Windows.Forms.ToolStripLabel("○ Не подключено") { Padding = pad };
        menu.Items.Add(_trayStatus);
        _trayPing = new TrayPingStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripControlHost(_trayPing)
        {
            AutoSize = false,
            Size = _trayPing.Size,
            Margin = System.Windows.Forms.Padding.Empty,
            Padding = System.Windows.Forms.Padding.Empty,
            BackColor = System.Drawing.ColorTranslator.FromHtml("#1C1F26"),
        });
        _trayStab = new System.Windows.Forms.ToolStripLabel("Стабильность: —") { Padding = pad };
        menu.Items.Add(_trayStab);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _traySvc = new TrayServiceStrip(_settings.Targets.Where(x => x.Enabled).Select(x => x.Name));
        menu.Items.Add(new System.Windows.Forms.ToolStripControlHost(_traySvc)
        {
            AutoSize = false,
            Size = _traySvc.Size,
            Margin = System.Windows.Forms.Padding.Empty,
            Padding = System.Windows.Forms.Padding.Empty,
            BackColor = System.Drawing.ColorTranslator.FromHtml("#1C1F26"),
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem("Открыть", () => ShowFromTray()));
        menu.Items.Add(TrayItem("Переключить на лучший", () => _ = SwitchToBestAsync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem("Выход", ExitFromTray));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "DNEX: не подключено",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _trayTimer.Tick += async (_, _) => await RefreshTrayServicesAsync();
        UpdateTrayServices();
    }

    private static System.Windows.Forms.ToolStripMenuItem TrayItem(string text, Action onClick)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text) { Padding = new System.Windows.Forms.Padding(10, 8, 10, 8) };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Статус, пинг и стабильность в меню трея и подсказка при наведении.</summary>
    private void UpdateTray(DnsServer? srv, string? name)
    {
        if (_trayStatus == null || _tray == null) return;
        _trayStatus.Text = name == null ? "○ Не подключено" : $"● {DnsServer.Clip(name, 22)}";
        _trayStatus.ForeColor = System.Drawing.ColorTranslator.FromHtml(name == null ? "#8C93A3" : "#3DD68C");
        if (_trayPing != null)
            _trayPing.SetPing(srv?.LatencyMs, srv?.LatencyMs == null ? "Пинг: —" : $"Пинг: {srv.PingText} мс");
        if (_trayStab != null)
            _trayStab.Text = srv == null ? "Стабильность: —" : $"Стабильность: {srv.Stability}";
        var tip = name == null ? "DNEX: не подключено" : $"DNEX: подключено, {name}";
        _tray.Text = tip.Length <= 63 ? tip : tip[..63]; // подсказка трея не длиннее 63 символов
    }

    /// <summary>Строки ИИ-сервисов: доступен или нет по последней проверке подключения.</summary>
    private void UpdateTrayServices()
    {
        if (_traySvc == null) return;
        var snap = _traySnap;
        foreach (var name in _traySvc.Names)
        {
            var svc = snap?.Services.FirstOrDefault(x => x.Name == name);
            // 0 — не проверено, 1 — доступен, 2 — недоступен
            var state = svc == null ? 0 : svc.Access.StartsWith("✓") ? 1 : 2;
            _traySvc.SetState(name, state);
        }
    }

    /// <summary>Переключает на самый надежный из рабочих серверов, кроме текущего.</summary>
    private async Task SwitchToBestAsync()
    {
        if (_busyConn) { ShowToast("Подождите: идет подключение", "Warn"); return; }
        var active = DnsSwitcher.GetActive();
        var best = _servers
            .Where(s => (s.Status == ServerStatus.Ok || s.Status == ServerStatus.Partial) && s.Id != active?.ServerId)
            .OrderBy(s => s.Rank)
            .FirstOrDefault();
        if (best == null)
        {
            ShowToast("Другого рабочего сервера нет. Проверьте список.", "Warn");
            return;
        }
        Log($"Переключение на лучший сервер: {best.DisplayName}");
        await ConnectToAsync(best);
    }

    /// <summary>Настройки программы. После сохранения файл перечитывается.</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(_settings) { Owner = this };
        w.ShowDialog();
        if (w.Saved)
        {
            _settings = LoadSettings();
            ApplyTimerSettings();
            Log("Настройки сохранены");
        }
    }

    /// <summary>Интервалы из настроек: проверка сервисов в трее и сторож DNS. Значения ограничены разумными пределами.</summary>
    private void ApplyTimerSettings()
    {
        _trayTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.TrayCheckMinutes, 1, 60));
        _watchdog.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.WatchdogSeconds, 15, 600));
        _pingTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.PingRefreshMinutes, 1, 60));
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _trayTimer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    // ---------- Анимации и уведомления ----------

    /// <summary>Длительность анимации. Если в Windows выключены анимации (Специальные возможности), переход мгновенный.</summary>
    private static TimeSpan Motion(int ms) => SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    /// <summary>Крутящийся значок: штриховой круг, вращается бесконечно.</summary>
    private static System.Windows.Shapes.Ellipse MakeSpinner(double size, Brush brush)
    {
        var rot = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, StrokeThickness = 2, Stroke = brush,
            StrokeDashArray = new DoubleCollection { 2.2, 1.6 },
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return ring;
    }

    /// <summary>На кнопке «Подключить» — значок и текст текущего шага.</summary>
    private void SetConnectContent(string text)
    {
        ConnectButton.Content = BuildConnectContent(text);
        CompactConnect.Content = BuildConnectContent(text);
    }

    /// <summary>Содержимое кнопки подключения: значок и текст текущего шага.</summary>
    private static StackPanel BuildConnectContent(string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(MakeSpinner(13, Brushes.White));
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(9, 0, 0, 0), FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        return sp;
    }

    /// <summary>Плавная смена цвета кнопки: синий «Подключить» → красный «Отключить» и обратно.</summary>
    private void AnimateConnectColor(string key)
    {
        var target = ((SolidColorBrush)FindResource(key)).Color;
        if (_connBrush == null)
        {
            _connBrush = new SolidColorBrush(target);
            ConnectButton.Background = _connBrush;
            CompactConnect.Background = _connBrush;
            return;
        }
        var anim = new ColorAnimation(target, Motion(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            _connBrush!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _connBrush.Color = target;
        };
        _connBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>Точка рядом с плашкой: синяя мигает во время работы, зеленая пульсирует при подключении.</summary>
    private void UpdateStatusVisuals()
    {
        bool on = DnsSwitcher.GetActive() != null;
        string mode = _working ? "busy" : on ? "on" : "idle";
        if (mode == _dotMode) return;
        _dotMode = mode;

        ConnDot.BeginAnimation(UIElement.OpacityProperty, null);
        ConnDot.Opacity = 1;
        ConnDot.Fill = (Brush)FindResource(mode switch { "busy" => "Accent", "on" => "Ok", _ => "Idle" });
        // Пульсация и мигание точки отключаются вместе с анимациями Windows
        if (mode == "idle" || !SystemParameters.ClientAreaAnimation) return;

        bool busy = mode == "busy";
        ConnDot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, busy ? 0.25 : 0.4, TimeSpan.FromSeconds(busy ? 0.5 : 1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase(),
        });
    }

    /// <summary>Короткое уведомление в углу; само гаснет через пару секунд.</summary>
    private void ShowToast(string text, string colorKey = "Ok")
    {
        ToastText.Text = text;
        ToastDot.Fill = (Brush)FindResource(colorKey);
        Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(200)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, null);
        ToastShift.Y = 12;
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Motion(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Плавный переход размера окна; при запуске — сразу.</summary>
    private void ResizeTo(double w, double h, bool animate)
    {
        if (!animate || !IsLoaded) { Width = w; Height = h; return; }
        var d = Motion(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var wa = new DoubleAnimation(w, d) { EasingFunction = ease };
        wa.Completed += (_, _) => { BeginAnimation(WidthProperty, null); Width = w; };
        var ha = new DoubleAnimation(h, d) { EasingFunction = ease };
        ha.Completed += (_, _) => { BeginAnimation(HeightProperty, null); Height = h; };
        BeginAnimation(WidthProperty, wa);
        BeginAnimation(HeightProperty, ha);
        if (Content is UIElement root)
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 1, Motion(260)));
    }

    private void AddToggle_Changed(object sender, RoutedEventArgs e) => Reveal(AddPanel, AddToggle.IsChecked == true);
    private void LogToggle_Changed(object sender, RoutedEventArgs e) => Reveal(LogBox, LogToggle.IsChecked == true);

    /// <summary>Раскрытие блока: появляется с легким сдвигом; при сворачивании гаснет и выпадает из верстки.</summary>
    private static void Reveal(FrameworkElement el, bool show)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (show)
        {
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(220)) { EasingFunction = ease });
            if (el.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, Motion(220)) { EasingFunction = ease });
        }
        else
        {
            // Если блок снова открыли до конца анимации, Completed не сработает — блок останется видимым.
            var fade = new DoubleAnimation(1, 0, Motion(160)) { EasingFunction = ease };
            fade.Completed += (_, _) => el.Visibility = Visibility.Collapsed;
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    /// <summary>Новая строка проявляется один раз. Через секунду флаг сбрасывается, чтобы при пересортировке строки не мигали.</summary>
    private static async Task ClearJustAddedLaterAsync(DnsServer s)
    {
        await Task.Delay(1000);
        s.JustAdded = false;
    }

    /// <summary>Строки плавно гаснут, потом исчезают из списка.</summary>
    private async Task FadeOutAndRemoveAsync(IEnumerable<DnsServer> items)
    {
        var list = items.ToList();
        foreach (var s in list) s.Fading = true;
        await Task.Delay(320);
        foreach (var s in list) _servers.Remove(s);
        SaveServers();
    }

    /// <summary>Плитка въезжает по очереди; если значение изменилось — на мгновение вспыхивает.</summary>
    private static void AnimateTile(Border card, SolidColorBrush bg, int index, bool changed)
    {
        var delay = Motion(90 * index);
        var shift = new TranslateTransform(0, 10);
        card.RenderTransform = shift;
        card.Opacity = 0;
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(320)) { BeginTime = delay });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Motion(320))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (changed)
            bg.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromRgb(0x2F, 0x5C, 0xA8), bg.Color, Motion(1200))
                {
                    BeginTime = delay + TimeSpan.FromMilliseconds(250),
                });
    }

    // ---------- Служебное ----------

    private CancellationToken BeginWork()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _working = true;
        ListActions.IsEnabled = ApplyActions.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        BusyPanel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible; // в компактном виде шаг показывает кнопка
        UpdateStatusVisuals();
        return _cts.Token;
    }

    private void EndWork()
    {
        _working = false;
        ListActions.IsEnabled = ApplyActions.IsEnabled = true;
        StopButton.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ProgressText.Text = "";
        WorkBar.Visibility = Visibility.Collapsed;
        UpdateStatusVisuals();
        UpdateStats();
    }

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _savePending;

    /// <summary>Сохранение в фоне: снимок списка берется в UI-потоке, запись на диск — вне его.
    /// Частые вызовы схлопываются в одну запись.</summary>
    private async void SaveServers()
    {
        if (_savePending) return;
        _savePending = true;
        await Task.Delay(300);
        _savePending = false;
        var snapshot = _servers.ToList();
        // ConfigureAwait(false): продолжение не ждет UI-поток. Иначе SaveServersNow при закрытии окна
        // держит UI-поток в Wait() на замке, который отпускается только в этом продолжении — взаимная блокировка.
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => Storage.Save(ServersFile, snapshot)).ConfigureAwait(false); }
        catch (Exception ex) { Log("Не удалось сохранить список: " + ex.Message); }
        finally { _saveLock.Release(); }
    }

    private void SaveServersNow()
    {
        _saveLock.Wait();
        try { Storage.Save(ServersFile, _servers.ToList()); }
        catch (Exception) { }
        finally { _saveLock.Release(); }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private DispatcherTimer? _logTimer;
    private const int MaxLogChars = 200_000;

    /// <summary>Журнал копится в очереди и выводится пачкой раз в 200 мс — сотни строк от парсера не тормозят интерфейс.</summary>
    private void Log(string msg)
    {
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");
        if (Dispatcher.CheckAccess()) EnsureLogTimer(); else Dispatcher.InvokeAsync(EnsureLogTimer);
    }

    private void EnsureLogTimer()
    {
        if (_logTimer == null)
        {
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _logTimer.Tick += (_, _) => FlushLog();
        }
        if (!_logTimer.IsEnabled) _logTimer.Start();
    }

    private void FlushLog()
    {
        _logTimer?.Stop();
        if (_logQueue.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        while (_logQueue.TryDequeue(out var line)) sb.AppendLine(line);
        LogBox.AppendText(sb.ToString());
        if (LogBox.Text.Length > MaxLogChars) LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
        LogBox.ScrollToEnd();
    }
}
