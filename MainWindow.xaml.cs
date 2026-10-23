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

        // Завершение Windows (выключение, выход из сеанса) не должно прятать программу в трей
        // Выключение или перезагрузка Windows: выходим без вопроса и без отключения, чтобы DNS остался после перезагрузки
        Application.Current.SessionEnding += (_, _) => { _sessionEnding = true; _exitRequested = true; };
        AddPanel.RenderTransform = new TranslateTransform();
        LogPanel.RenderTransform = new TranslateTransform();
        _logView = CollectionViewSource.GetDefaultView(_log);
        _logView.Filter = FilterLog;
        LogList.ItemsSource = _logView;
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
        // Рабочие серверы наверху. Сортировка НЕ живая: строки не переставляются при обновлении статуса и пинга.
        // Порядок пересчитывается после пакетной проверки, при смене фильтра и при добавлении сервера.
        // Подключенный сервер всегда первый, затем рабочие наверху
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.IsConnected), ListSortDirection.Descending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Rank), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.LatencyMs), ListSortDirection.Ascending));
        // Клик по заголовку колонки заменяет правила сортировки: подключенный сервер закрепляем заново
        ((System.Collections.Specialized.INotifyCollectionChanged)_view.SortDescriptions).CollectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PinConnectedFirst));
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
        if (_settings.CheckUpdates) ScheduleUpdateCheck();
            if (DnsSwitcher.GetActive() is { } a)
                Log($"Активно подключение «{a.Name}» с {a.Since:dd.MM HH:mm}.");
            SelectBest();

            _ = ResolveLocationsAsync();

            // Автопроверка при запуске: все, что не проверялось или проверялось давно.
            var stale = _servers.Where(x => x.LastChecked == null || DateTime.Now - x.LastChecked > TimeSpan.FromMinutes(30)).ToList();
            if (stale.Count > 0) await RunChecksAsync(stale);
        };
        _watchdog.Tick += Watchdog_Tick;
        WatchVpn();
        _undoTimer.Tick += (_, _) => UndoTick();
        PreviewKeyDown += Window_PreviewKeyDown;
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
            6 => s.IsFavorite,
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

    // Темный заголовок окна Windows 10/11 под цвет программы
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.DarkTitle.Apply(this);
    }

    private void UpdateStats()
    {
        _statsTimer.Stop();
        if (_view == null) return;
        int shown = _view.Cast<object>().Count();
        StatsText.Text = $"{_servers.Count(s => s.Status == ServerStatus.Ok)} работают   ·   " +
                         $"{_servers.Count(s => s.Status == ServerStatus.Partial)} частично   ·   " +
                         $"показано {shown} из {_servers.Count}";
        UpdateTopList();
    }

    // ---------- Лучшие по стабильности ----------

    private string? _topSig;

    /// <summary>Лучший сервер категории и его стабильность по сервису.</summary>
    private sealed record TopPick(string Title, string Key, DnsServer? Best, int? Stability);

    /// <summary>
    /// Лучший сервер отдельно для каждого ИИ и для всех сразу: самая высокая стабильность по сервису,
    /// при равенстве больше проверок и ниже пинг. Кандидат должен работать с сервисом сейчас и не быть мусором.
    /// </summary>
    private List<TopPick> PickBest()
    {
        var names = _settings.Targets.Where(t => t.Enabled).Select(t => t.Name).ToList();
        var cats = names.Select(n => (Title: n, Key: n,
                Stab: (Func<DnsServer, int?>)(s => s.ServiceStability(n)),
                Works: (Func<DnsServer, bool>)(s => s.Chips.Any(c => c.Name == n && c.State == "ok"))))
            .ToList();
        if (names.Count > 1)
            cats.Add((names.Count == 3 ? "Все три" : "Все сразу", "*", s => s.AllStability, s => s.Status == ServerStatus.Ok));

        var picks = new List<TopPick>();
        foreach (var c in cats)
        {
            var best = _servers.Where(s => !s.IsJunk && s.ServiceChecks > 0 && c.Works(s) && c.Stab(s) is > 0)
                .OrderByDescending(s => c.Stab(s)).ThenByDescending(s => s.ServiceChecks).ThenBy(s => s.LatencyMs ?? long.MaxValue)
                .FirstOrDefault();
            picks.Add(new TopPick(c.Title, c.Key, best, best == null ? null : c.Stab(best)));
        }
        return picks;
    }

    /// <summary>Бейджи «лучший …» в таблице и блок «Лучшие по стабильности». Блок перестраивается, только если выбор изменился.</summary>
    private void UpdateTopList()
    {
        var picks = PickBest();

        // Лучший для всех важнее, чем лучший для одного сервиса
        foreach (var s in _servers)
        {
            var mine = picks.Where(p => p.Best == s).ToList();
            s.BestFor = mine.Count == 0 ? null
                      : mine.Any(p => p.Key == "*") ? "лучший для всех"
                      : "лучший: " + string.Join(", ", mine.Select(p => p.Title));
        }

        // Флаг грузится позже выбора: его появление тоже повод перестроить блок
        var sig = string.Join("|", picks.Select(p => $"{p.Key}:{p.Best?.Id}:{p.Stability}:{p.Best?.ServiceChecks}:{p.Best?.LatencyMs}:{p.Best?.IsConnected}:{p.Best?.Flag != null}"));
        if (sig == _topSig) return;
        _topSig = sig;
        BuildTopRows(picks);
    }

    /// <summary>
    /// Рамка «Лучшие по стабильности» справа от проверки подключения: заголовок и по строке на категорию.
    /// В строке: логотип, для кого и сервер с флагом; справа стабильность по сервису крупно, ниже пинг и число проверок.
    /// </summary>
    private void BuildTopRows(List<TopPick> picks)
    {
        Brush B(string k) => (Brush)FindResource(k);
        TopRows.Children.Clear();
        TopRows.Children.Add(new TextBlock { Text = "Лучшие по стабильности", Style = (Style)FindResource("H2") });
        TopRows.Children.Add(new TextBlock
        {
            Text = "отдельно для каждого сервиса · щелчок выбирает сервер", Foreground = B("Muted"), FontSize = 12,
            Margin = new Thickness(0, 2, 0, 8),
        });

        if (picks.All(p => p.Best == null))
        {
            TopRows.Children.Add(new TextBlock
            {
                Text = "Пока нет данных. Список заполнится после проверки серверов: кнопка «Перепроверить».",
                Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        for (int i = 0; i < picks.Count; i++)
        {
            var p = picks[i];
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Логотип сервиса; у «Все три» три маленьких логотипа
            var logos = (p.Key == "*" ? _settings.Targets.Where(t => t.Enabled).Select(t => AiLogo(t.Name)) : new[] { AiLogo(p.Key) })
                .Where(l => l != null).ToList();
            var logoPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                                             HorizontalAlignment = HorizontalAlignment.Center }; // одиночный и тройной по одной оси
            int size = logos.Count > 1 ? 12 : 20;
            foreach (var logo in logos)
                logoPanel.Children.Add(new Image { Source = logo, Width = size, Height = size, Margin = new Thickness(0, 0, logos.Count > 1 ? 2 : 0, 0), Stretch = Stretch.Uniform });
            g.Children.Add(logoPanel);

            // Для кого и сервер
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = p.Title, Foreground = B("Muted"), FontSize = 11 });
            var server = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            if (p.Best is { } s)
            {
                if (s.Flag != null)
                    server.Children.Add(new Image { Source = s.Flag, Width = 18, Height = 12, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
                server.Children.Add(new TextBlock { Text = s.ShortName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 270, VerticalAlignment = VerticalAlignment.Center });
                if (s.IsConnected) // точка, а не слово: в узкой рамке подпись обрезалась
                    server.Children.Add(new TextBlock { Text = "  ●", Foreground = B("Lime"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Сейчас подключен" });
            }
            else server.Children.Add(new TextBlock { Text = "нет рабочего сервера", Foreground = B("Muted") });
            mid.Children.Add(server);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            // Стабильность по сервису крупно, ниже пинг и число проверок
            if (p.Best is { } b)
            {
                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                right.Children.Add(new TextBlock
                {
                    Text = $"{p.Stability}%", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = p.Stability >= 90 ? B("Ok") : p.Stability >= 60 ? B("Warn") : B("Bad"),
                });
                right.Children.Add(new TextBlock
                {
                    Text = (b.LatencyMs is { } ms ? $"{ms} мс · " : "") + $"{b.ServiceChecks} пров.",
                    Foreground = B("Muted"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right,
                });
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
            }

            var row = new Border
            {
                Child = g, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent, Tag = p.Best, Cursor = p.Best != null ? Cursors.Hand : null,
                ToolTip = p.Best == null ? null
                    : $"{p.Best.DisplayName}\n{p.Best.Address}\nСтабильность по сервису {p.Stability}% за {p.Best.ServiceChecks} проверок\nЩелчок выбирает сервер в списке",
            };
            row.MouseEnter += (_, _) => row.Background = B("Surface2");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += TopRow_Click;
            // Тонкая линия между строками
            TopRows.Children.Add(i == 0 ? row : new Border
            {
                BorderBrush = B("Border"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 2, 0, 0),
                Margin = new Thickness(0, 2, 0, 0), Child = row,
            });
        }
    }

    private void TopRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DnsServer s }) return;
        ServerGrid.SelectedItem = s;
        ServerGrid.ScrollIntoView(s);
        ServerGrid.Focus();
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
        // Строка прогресса обновляется не на каждой странице: счетчик времени показывает, что поиск идет
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string lastProgress = "";
        var beat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        beat.Tick += (_, _) => { if (lastProgress.Length > 0) ProgressText.Text = $"{lastProgress} · {clock.Elapsed.ToString(@"m\:ss")}"; };
        beat.Start();
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
                lastProgress = $"Страниц {p.Pages}/{p.MaxPages} · в очереди {p.Queued} · найдено {p.Found} · новых {added.Count} · ошибок {p.Errors} · {p.Current}";
                ProgressText.Text = lastProgress;
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
        finally { beat.Stop(); EndWork(); }
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
        SaveServersNow();
        // Windows завершает сеанс: не спрашиваем и не отключаем. Статический DNS сохраняется на адаптере,
        // файл подключения остается, и после перезагрузки DNEX продолжает с того же сервера.
        if (_sessionEnding || _closeConfirmed || DnsSwitcher.GetActive() is not { } a) { _cts?.Cancel(); return; }

        var r = ClosePrompt.Ask(a.Name);
        if (r == MessageBoxResult.Cancel)
        {
            // Передумал: программа работает дальше, текущая проверка или поиск не прерываются
            e.Cancel = true;
            _exitRequested = false;
            return;
        }
        _cts?.Cancel();
        if (r == MessageBoxResult.No) return; // выход с сохранением DNS: без проверок
        e.Cancel = true;
        // Выход с отключением: только возврат DNS, без проверок подключения
        if (!await DisconnectAsync("выход из программы", probe: false))
        {
            _exitRequested = false;
            MessageBox.Show("Отключиться не удалось, поэтому программа не закрыта: иначе DNS остался бы измененным.\n" +
                            "Подробности в журнале. Попробуйте еще раз или выберите выход с сохранением DNS.",
                "DNEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
            var now = await ConnectionProbe.TakeAsync(_settings, leakTest: true);
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
            Log($"  {x.Name}: {x.Host} → {x.ResolvedIp} {(x.Proxied ? "(через Smart DNS)" : "(напрямую)")}, {x.Access}{(x.Note != null ? ". " + x.Note : "")}");
        foreach (var x in n.Hosts)
            Log($"  доп. {x.Name} → {x.ResolvedIp} {(x.Proxied ? "(через Smart DNS)" : "(напрямую)")}, {x.Access}");
        foreach (var r in n.Resolvers)
            Log($"  резолвер: {r.Ip} · {r.Org} · {r.Place}");
        if (DnsSwitcher.GetActive() != null && LeakTest.LooksLikeLeak(n.Resolvers))
            Log("  ⚠ Утечка DNS: запросы уходят к разным резолверам, часть идет мимо сервера");
    }

    /// <summary>
    /// Блок «До / После»: строка с внешним IP и DNS, ниже по плитке на каждый ИИ-сервис —
    /// крупно текущий доступ, мельче «было» и адрес, через который идет сервис.
    /// </summary>
    /// <summary>Логотип ИИ-сервиса по названию из настроек проверки. Null, если логотипа нет.</summary>
    private static ImageSource? AiLogo(string name) => name switch
    {
        "ChatGPT" => Application.Current.TryFindResource("LogoOpenAI") as ImageSource,
        "Claude" => Application.Current.TryFindResource("LogoClaude") as ImageSource,
        "Gemini" => Application.Current.TryFindResource("LogoGemini") as ImageSource,
        _ => null,
    };

    private string? _compareSig;
    /// <summary>Снимок, по которому построены плитки. Опоздавший результат поиска стран для старого снимка их не трогает.</summary>
    private NetSnapshot? _shownSnap;
    private readonly HashSet<string> _geoPending = new();
    /// <summary>Сервисы, у которых раскрыт список серверов. Сохраняется между перерисовками блока.</summary>
    private System.Windows.Controls.Primitives.Popup? _hostsPopup;
    private string? _hostsPopupFor;      // сервис, чьи адреса открыты
    private DateTime _hostsClosedAt;     // щелчок по той же кнопке закрывает панель, а не открывает заново
    private bool _compareDisconnected;

    private void ShowCompare(bool disconnected = false)
    {
        Brush B(string k) => (Brush)FindResource(k);
        var host = CompareGrid;
        // В компактном виде блок сам показывает последнюю проверку сервисов (фоновую или при подключении)
        var cur = _after ?? _before ?? (_compact ? _traySnap : null);
        if (cur != null)
        {
            // Пересобираем блок только при новом снимке: иначе плитки перерисовываются на каждое обновление
            var sig = $"{disconnected}|{cur.Taken:O}|{_before?.Taken:O}|{_after?.Taken:O}|{cur.SystemDns}|" +
                      string.Join(";", cur.Services.Select(x => $"{x.Name}{x.Access}{x.ResolvedIp}{x.Proxied}"));
            if (sig == _compareSig) return;
            _compareSig = sig;
        }
        host.Children.Clear();
        _tileGeo.Clear();
        _compareDisconnected = disconnected;
        _shownSnap = cur;
        if (_hostsPopup != null) _hostsPopup.IsOpen = false;
        if (cur == null)
        {
            host.Children.Add(new TextBlock
            {
                Text = _compact ? "Проверяю ИИ-сервисы…" : "Нажмите «Подключить», чтобы увидеть сравнение до и после.",
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
        if (!_compact) host.Children.Add(Line(
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
        if (!_compact) host.Children.Add(Line(dnsRuns.ToArray()));

        // Строка 3: резолверы, которые на самом деле дошли до тестового сервера (тест утечки). Только при подключении:
        // без него сравнивать не с чем, а провайдер и так отвечает через несколько своих серверов
        if (!_compact && cur.Resolvers.Count > 0 && !disconnected && DnsSwitcher.GetActive() != null)
        {
            static string Country(string place) => place.Split(',').Last().Trim();
            // Один владелец — одна запись: адреса его серверов в подсказке
            var groups = cur.Resolvers.GroupBy(r => LeakTest.CleanOrg(r.Org), StringComparer.OrdinalIgnoreCase).ToList();
            var runs = new List<(string, Brush, bool)> { ("Резолверы  ", B("Muted"), false) };
            runs.Add((string.Join("   ·   ", groups.Select(g =>
            {
                var name = g.Key.Length <= 32 ? g.Key : g.Key[..31] + "…";
                var place = g.First().Place;
                return place.Length > 0 ? $"{name} ({Country(place)})" : name;
            })), B("Text"), true));
            bool leak = groups.Count > 1;
            runs.Add((leak ? "   ⚠ часть запросов идет мимо сервера" : "   ✓ все запросы через сервер", leak ? B("Warn") : B("Ok"), true));
            var line = Line(runs.ToArray());
            line.TextTrimming = TextTrimming.CharacterEllipsis;
            line.TextWrapping = TextWrapping.NoWrap;
            line.ToolTip = string.Join("\n", cur.Resolvers.Select(r => $"{r.Ip} · {r.Org} · {r.Place}"));
            host.Children.Add(line);
        }

        // Плитки сервисов: статус доступа сверху, страна (флаг и название) под ним
        // Карточки в колонках, по верху: раскрытая карточка растет сама, соседние не вытягиваются
        var tiles = new Grid { Margin = new Thickness(-4, _compact ? 0 : 8, -4, 0), VerticalAlignment = VerticalAlignment.Top };
        foreach (var _ in cur.Services) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var svc in cur.Services)
        {
            var was = _after != null ? _before?.Services.FirstOrDefault(x => x.Name == svc.Name) : null;
            bool ok = svc.Access.StartsWith("✓");
            var card = new Border
            {
                Background = new SolidColorBrush(((SolidColorBrush)B("Surface2")).Color), CornerRadius = new CornerRadius(10), Padding = _compact ? new Thickness(8, 5, 8, 5) : new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = ok ? B("Ok") : B("Bad"),
            };
            var sp = new StackPanel();
            var head = new DockPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            int logoSize = _compact ? 15 : 18;
            if (AiLogo(svc.Name) is { } logo)
                title.Children.Add(new Image { Source = logo, Width = logoSize, Height = logoSize, Margin = new Thickness(0, 0, _compact ? 6 : 8, 0), Stretch = Stretch.Uniform });
            title.Children.Add(new TextBlock { Text = svc.Name, FontWeight = FontWeights.SemiBold, FontSize = _compact ? 13 : 14, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            var state = new TextBlock { Text = svc.Access.TrimStart('✓', '✗', ' '), Foreground = ok ? B("Ok") : B("Bad"),
                                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                        FontSize = _compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis,
                                        MaxWidth = _compact ? 120 : 150, Margin = new Thickness(_compact ? 0 : 8, 0, 0, 0),
                                        ToolTip = svc.Note };
            var geo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _tileGeo[svc.Name] = geo;
            if (_compact)
            {
                // Компактная карточка в две строки: название; флаг страны доступа и статус. Адрес в подсказке
                sp.Children.Add(head);
                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                row2.Children.Add(geo);
                state.HorizontalAlignment = HorizontalAlignment.Left;
                row2.Children.Add(state);
                sp.Children.Add(row2);
                card.ToolTip = $"{svc.Access.TrimStart('✓', '✗', ' ')}\n{svc.Host} → {svc.ResolvedIp}{(svc.Proxied ? " (через Smart DNS)" : " (напрямую)")}";
            }
            else
            {
                DockPanel.SetDock(state, Dock.Right);
                head.Children.Insert(0, state);
                sp.Children.Add(head);
                geo.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(geo);
                sp.Children.Add(Line(
                    (svc.Proxied ? "через Smart DNS  " : "напрямую  ", svc.Proxied ? B("Accent") : B("Muted"), false),
                    (svc.ResolvedIp, B("Muted"), false)));
                if (was != null)
                    sp.Children.Add(Line(("было: ", B("Muted"), false),
                        ($"{was.Access.TrimStart('✓', '✗', ' ')}, {(was.Proxied ? "через Smart DNS" : "напрямую")}", B("Muted"), false)));
            }
            // Кнопка «Серверы» открывает адреса сервиса всплывающей панелью под карточкой: карточка не растет
            // и таблица не уезжает. В компактном окне кнопки нет: там мало места
            if (!_compact)
            {
                int hostCount = 1 + cur.Hosts.Count(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal));
                var toggle = new TextBlock
                {
                    Text = $"Серверы ({hostCount}) ▾",
                    Foreground = B("Accent"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand,
                    Tag = svc, ToolTip = "Адреса сервиса и их статус",
                };
                toggle.MouseLeftButtonUp += ToggleHosts_Click;
                sp.Children.Add(toggle);
            }
            card.Child = sp;
            Grid.SetColumn(card, tiles.Children.Count);
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

    /// <summary>
    /// Адреса сервиса: основной и дополнительные. На адрес две строки: имя и плашка статуса, ниже IP
    /// (синий — адрес прокси Smart DNS, серый — напрямую). Причина статуса в подсказке.
    /// </summary>
    private StackPanel HostsPanel(ServiceState svc, NetSnapshot cur)
    {
        Brush B(string k) => (Brush)FindResource(k);
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var panel = new StackPanel();
        var entries = new List<ServiceState> { svc };
        entries.AddRange(cur.Hosts.Where(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal)));
        for (int i = 0; i < entries.Count; i++)
        {
            var h = entries[i];
            bool isOpen = h.Access.StartsWith("✓");
            bool isClosed = h.Access.Contains("закрыт");
            string pillColor = isOpen ? "#173A2A" : isClosed ? "#3A1C20" : "#3A2F17";
            string pillText = isOpen ? "доступен" : isClosed ? "закрыт" : h.Access.Contains("подтвержден") ? "не подтвержден" : "не отвечает";
            string pillFg = isOpen ? "Ok" : isClosed ? "Bad" : "Warn";

            var top = new DockPanel();
            var pill = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pillColor)),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = h.Note ?? h.Access,
                Child = new TextBlock { Text = pillText, FontSize = 11, Foreground = B(pillFg) },
            };
            DockPanel.SetDock(pill, Dock.Right);
            top.Children.Add(pill);
            top.Children.Add(new TextBlock
            {
                Text = h.Host, FontFamily = mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = h.Host,
            });

            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
            row.Children.Add(top);
            row.Children.Add(new TextBlock
            {
                Text = h.ResolvedIp + (h.Proxied ? "  · через Smart DNS" : "  · напрямую"), FontFamily = mono, FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0), Foreground = h.Proxied ? B("Accent") : B("Muted"),
            });
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// Открывает адреса сервиса всплывающей панелью под его карточкой, шириной в карточку. Щелчок мимо или Esc закрывает.
    /// Длинный список прокручивается внутри панели.
    /// </summary>
    private void ToggleHosts_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: ServiceState svc } toggle || _shownSnap is not { } cur) return;
        // Щелчок по кнопке открытой панели: панель уже закрылась от щелчка мимо, заново не открываем
        if (_hostsPopupFor == svc.Name && DateTime.Now - _hostsClosedAt < TimeSpan.FromMilliseconds(300)) return;
        if (_hostsPopup != null) _hostsPopup.IsOpen = false;
        if (toggle.Parent is not FrameworkElement { Parent: FrameworkElement card }) return;

        Brush B(string k) => (Brush)FindResource(k);
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = $"{svc.Name} · адреса и доступ", Foreground = B("Muted"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(new ScrollViewer
        {
            Content = HostsPanel(svc, cur), MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        });
        var frame = new Border
        {
            Child = content, Background = B("Surface2"), BorderBrush = B("Border"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(8, 0, 8, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = frame, PlacementTarget = card, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -8, VerticalOffset = 4, Width = card.ActualWidth + 16, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
        };
        popup.Closed += (_, _) =>
        {
            toggle.Text = toggle.Text.Replace("Скрыть серверы ▴", $"Серверы ({1 + cur.Hosts.Count(h => h.Name.StartsWith(svc.Name + ": ", StringComparison.Ordinal))}) ▾");
            _hostsClosedAt = DateTime.Now;
        };
        popup.KeyDown += (_, k) => { if (k.Key == Key.Escape) popup.IsOpen = false; };
        _hostsPopup = popup;
        _hostsPopupFor = svc.Name;
        toggle.Text = "Скрыть серверы ▴";
        popup.IsOpen = true;
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
        SelectionSource.Visibility = Visibility.Collapsed;
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
        // Название как в таблице (страна и город или имя из каталога), а не сайт, где сервер найден
        SelectionTitle.Text = s.ShortName;
        SelectionTitle.ToolTip = s.Name;
        ShowSelectionSource(s);
        var parts = new List<string> { s.Address };
        if (!string.IsNullOrEmpty(s.DohUrl) && !string.IsNullOrEmpty(s.Primary)) parts.Add(s.DohUrl!);
        parts.Add(s.StatusText.ToLowerInvariant());
        if (s.LatencyMs != null) parts.Add($"пинг {s.PingText} мс");
        if (s.ChecksTotal > 0) parts.Add($"стабильность {s.Stability}");
        SelectionDetails.Text = string.Join("   ·   ", parts);
    }

    /// <summary>
    /// Источник выбранного сервера «хлебными крошками»: «источник  github.com › MAECTRO › magicdns › commits › main».
    /// Сайт выделен цветом, путь светлее, разделители приглушены. Полная ссылка в подсказке, щелчок открывает страницу.
    /// </summary>
    private void ShowSelectionSource(DnsServer s)
    {
        Brush B(string k) => (Brush)FindResource(k);
        SelectionSource.Inlines.Clear();
        var parts = s.HasSourceUrl ? SourceLabel.Parts(s.SourceUrl) : new List<string>();
        SelectionSource.Inlines.Add(new System.Windows.Documents.Run("источник  ") { Foreground = B("Muted") });
        if (parts.Count == 0)
        {
            SelectionSource.Inlines.Add(new System.Windows.Documents.Run(s.Source) { Foreground = B("Text") });
            SelectionSource.Cursor = null;
            SelectionSource.ToolTip = null;
        }
        else
        {
            SelectionSource.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { Foreground = B("Accent") });
            foreach (var part in parts.Skip(1))
            {
                SelectionSource.Inlines.Add(new System.Windows.Documents.Run("  ›  ") { Foreground = B("Muted") });
                SelectionSource.Inlines.Add(new System.Windows.Documents.Run(part) { Foreground = B("Text") });
            }
            SelectionSource.Cursor = Cursors.Hand;
            SelectionSource.ToolTip = $"{s.SourceUrl}\nЩелчок открывает страницу, где найден сервер";
        }
        SelectionSource.Tag = s;
        SelectionSource.Visibility = Visibility.Visible;
    }

    private void SelectionSource_Click(object sender, MouseButtonEventArgs e)
    {
        if (SelectionSource.Tag is DnsServer { HasSourceUrl: true } s)
            Process.Start(new ProcessStartInfo(new Uri(s.SourceUrl!).AbsoluteUri) { UseShellExecute = true }); // только http/https — проверено в HasSourceUrl
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------- Компактный / полный режим ----------

    private bool _compact;
    private Rect _fullBounds;

    /// <summary>Проверка новой версии через 10 секунд после запуска, чтобы не мешать старту. Только уведомление.</summary>
    private void ScheduleUpdateCheck()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var info = await UpdateChecker.CheckAsync();
            if (info == null || _settings.SkippedUpdate == info.Version.ToString()) return;
            Log($"Доступна версия {info.Version}");
            OpenUpdateWindow(info);
        };
        timer.Start();
    }

    /// <summary>Окно с новой версией. «Пропустить версию» запоминает номер для фоновой проверки.</summary>
    private void OpenUpdateWindow(UpdateInfo info)
    {
        UpdateWindow.Show(this, info, () =>
        {
            var before = _settings.SkippedUpdate;
            _settings.SkippedUpdate = info.Version.ToString();
            try { Storage.Save("settings.json", _settings); } catch (Exception) { }
            OfferUndo($"Версия {info.Version} пропущена", () =>
            {
                _settings.SkippedUpdate = before;
                try { Storage.Save("settings.json", _settings); } catch (Exception) { }
                Log($"Версия {info.Version} снова будет предлагаться");
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>Обрезает содержимое таблицы по скруглению карточки, чтобы прямые углы заголовков не выходили за рамку.</summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border card || card.Child is not DockPanel content) return;
        // Радиус карточки 12, рамка 1 px: внутри скругление 11
        var w = Math.Max(0, card.ActualWidth - 2);
        var h = Math.Max(0, card.ActualHeight - 2);
        content.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 11, 11);
    }

    /// <summary>Кнопка в полосе заголовка: проверить сейчас и показать результат.</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Проверяю…";
        try
        {
            var r = await UpdateChecker.CheckDetailedAsync();
            switch (r.Status)
            {
                case UpdateStatus.Available:
                    Log($"Доступна версия {r.Info!.Version}");
                    OpenUpdateWindow(r.Info!);
                    break;
                case UpdateStatus.UpToDate:
                    ShowToast($"У вас последняя версия {UpdateChecker.CurrentVersion}");
                    break;
                default:
                    ShowToast("Не удалось проверить: нет связи с GitHub", "Warn");
                    break;
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateButton.Content = "Проверить обновление";
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Значок кнопки разворота: квадрат или два квадрата, когда окно развернуто.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxButton != null) MaxButton.Content = WindowState == WindowState.Maximized ? "❐" : "☐";
    }

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
        // В компактном виде блок ИИ-сервисов остается: три плитки с логотипом, флагом доступа и статусом, без заголовка
        VerdictCard.Visibility = Visibility.Visible;
        VerdictCard.Padding = compact ? new Thickness(6, 6, 6, 6) : new Thickness(16, 12, 16, 12);
        VerdictHeader.Visibility = v;
        CompareGrid.Visibility = Visibility.Visible;
        CompareGrid.Margin = compact ? new Thickness(0) : new Thickness(0, 12, 0, 0); // в компактном виде без шапки блока: без пустого отступа
        _compareSig = null; // плитки пересобираются с отступами текущего вида
        SelectionBar.Visibility = v;
        ColFav.Visibility = v;  // узкое окно: звездочка через меню строки
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
        ShowCompare();
        if (compact) _ = RefreshTrayServicesAsync(); // компактный вид сразу проверяет сервисы
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

        // Рамка «Лучшие по стабильности» справа от проверки подключения: только в полном виде и в широком окне
        TopCard.Visibility = full && w >= 1300 ? Visibility.Visible : Visibility.Collapsed;

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
        if (DnsSwitcher.GetActive() != null) { await DisconnectByUserAsync(); return; }
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

    /// <summary>Подключение к серверу. auto — вызывает сторож: без вопросов и окон, только журнал и уведомления.
    /// Возвращает true, если после смены DNS сайты открываются.</summary>
    private async Task<bool> ConnectToAsync(DnsServer s, bool auto = false)
    {
        if (_busyConn) return false;
        bool connected = false;
        // Для отмены: к какому серверу было подключение до этого (если было)
        var prevActive = DnsSwitcher.GetActive();
        var prevServer = prevActive == null ? null : _servers.FirstOrDefault(x => x.Id == prevActive.ServerId);
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
            if (s.Status == ServerStatus.Fail && (auto ||
                MessageBox.Show($"«{s.Name}» сейчас не работает:\n{s.LastError}\n\nВсе равно подключиться?", "DNEX",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes))
                return false;

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
                if (!auto)
                    MessageBox.Show("Сервер не отвечает системе: настройки DNS возвращены как были.", "DNEX",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                // Снимок «после» — по нему видно, действительно ли трафик ИИ пошел через Smart DNS.
                ProgressText.Text = "Проверяю результат подключения…";
                _after = await ConnectionProbe.TakeAsync(_settings, leakTest: true);
                ShowCompare();
                SetTraySnapshot(_after);
                LogSnapshot("После подключения", _after);
                ShowToast($"Подключено · {s.DisplayName}\n{RestartHint}");
                Log(RestartHint);
                connected = true;
            }
        }
        catch (Exception ex) { Log("Ошибка подключения: " + ex.Message); }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
        if (connected) ReportSystemIssues(auto);
        if (connected && !auto)
        {
            var target = s;
            if (prevServer != null && prevServer != s)
                OfferUndo($"Переключено на {s.DisplayName}", hint: RestartHint, undo: async () =>
                {
                    if (!StillConnectedTo(target)) return;
                    await ConnectToAsync(prevServer);
                });
            else if (prevActive == null)
                OfferUndo($"Подключено к {s.DisplayName}", hint: RestartHint, undo: async () =>
                {
                    if (!StillConnectedTo(target)) return;
                    await DisconnectByUserAsync();
                });
        }
        return connected;
    }

    /// <summary>
    /// После подключения: что в системе мешает DNS из DNEX (VPN, безопасный DNS браузера, прокси, hosts,
    /// чужие DNS на других адаптерах). Пишет в журнал и показывает, что отключить. Сторожу окно не показывается.
    /// </summary>
    private void ReportSystemIssues(bool quiet)
    {
        var addrs = DnsSwitcher.GetActive()?.Addresses ?? new List<string>();
        var issues = SystemCheck.Run(addrs);
        if (issues.Count == 0) { Log("Помех в системе не найдено"); return; }
        foreach (var i in issues) Log($"Помеха: {i.Title}. {i.Fix}");
        if (quiet) { ShowToast($"Найдено помех: {issues.Count}, подробности в журнале", "Warn"); return; }
        MessageBox.Show(
            "Подключение работает, но в системе есть то, что может ему мешать. Для стабильной работы:\n\n" +
            string.Join("\n\n", issues.Select(i => $"• {i.Title}\n{i.Fix}")),
            "DNEX: что отключить", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await DisconnectByUserAsync();

    /// <summary>Отключение по действию пользователя: после него можно отменить и подключиться обратно.</summary>
    private async Task DisconnectByUserAsync()
    {
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        if (await DisconnectAsync("по запросу") && srv != null)
            OfferUndo($"Отключено от {srv.DisplayName}", hint: RestartHint, undo: async () =>
            {
                if (DnsSwitcher.GetActive() != null) { Log("Отмена не выполнена: подключение уже изменилось"); return; }
                await ConnectToAsync(srv);
            });
    }

    /// <summary>Отмена подключения безопасна, только пока подключение то же самое: его мог сменить сторож или пользователь.</summary>
    private bool StillConnectedTo(DnsServer s)
    {
        if (DnsSwitcher.GetActive()?.ServerId == s.Id) return true;
        Log("Отмена не выполнена: подключение уже изменилось");
        return false;
    }

    /// <summary>
    /// Отключение: сначала возвращаются исходные DNS, проверки идут потом. probe=false при выходе из программы:
    /// только возврат DNS, без проверок. Возвращает true, если исходные DNS восстановлены.
    /// </summary>
    private async Task<bool> DisconnectAsync(string reason, bool probe = true)
    {
        if (_busyConn) return false;
        _busyConn = true;
        bool restored = false;
        BeginWork();
        try
        {
            SetConnectContent("Отключаю…");
            ProgressText.Text = "Отключаю…";
            // «До» — последняя проверка в подключенном виде. Новая шла бы через DNS, который мог уже умереть,
            // и задержала бы возврат интернета на десятки секунд
            _before = _after ?? _traySnap;
            _after = null;
            await DnsSwitcher.RestoreAsync(Log);
            restored = true;
            Log($"Отключено ({reason})");
            ShowToast($"Отключено\n{RestartHint}", "Idle");
            Log(RestartHint);
            if (probe)
            {
                _ = DnsSwitcher.ReportStateAsync(Log); // что осталось в системе: в журнал, уведомление его не ждет
                await Task.Delay(500);
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    _after = await ConnectionProbe.TakeAsync(_settings, limit.Token, leakTest: true);
                    ShowCompare(disconnected: true);
                    LogSnapshot("После отключения", _after);
                }
                catch (OperationCanceledException) { Log("Проверка после отключения не уложилась в 20 секунд"); }
            }
        }
        catch (Exception ex) { Log("Ошибка отключения: " + ex.Message); }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
        return restored;
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
        foreach (var srv in _servers) srv.IsConnected = on && srv.Id == active!.ServerId;
        _view?.Refresh(); // подключенный сервер поднимается наверх
        UpdateTrayServices();
        UpdateHeaderStats();
        ConnPill.BorderBrush = (System.Windows.Media.Brush)FindResource(on ? "Ok" : "Border");
        ConnPill.Background = (System.Windows.Media.Brush)FindResource(on ? "OkSoft" : "Surface");
        _watchdogFails = 0;
        if (on) _watchdog.Start(); else _watchdog.Stop();
        UpdateConnectionLocation();
        UpdateTopList(); // флаги и страны появились: блок «Лучшие по стабильности» перерисуется с ними
        UpdateStatusVisuals();
    }

    private bool _watchNoNet;

    /// <summary>
    /// Сторож, пока подключено:
    /// 1) сети нет вообще (сон, загрузка, смена Wi-Fi) — ждет, неудачей это не считается;
    /// 2) DNS системы сбросила другая программа или Windows — возвращает подключение;
    /// 3) DNS не отвечает 3 раза подряд — переключает на следующий лучший по стабильности сервер.
    ///    Отключается и возвращает исходные DNS, только если замена не нашлась.
    /// </summary>
    private async void Watchdog_Tick(object? sender, EventArgs e)
    {
        if (Watchdog.IsChecked != true || _busyConn) return;
        var active = DnsSwitcher.GetActive();
        if (active == null) { _watchdog.Stop(); return; }

        if (!await SystemCheck.NetworkUpAsync())
        {
            if (!_watchNoNet) Log("Сторож: нет сети, жду. Подключение не трогаю");
            _watchNoNet = true;
            _watchdogFails = 0;
            return;
        }
        _watchNoNet = false;

        // Защита от сброса: на адаптерах должны стоять адреса подключения
        var current = await DnsSwitcher.GetCurrentAddressesAsync();
        var mine = active.Addresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();
        if (mine.Count > 0 && !mine.Any(current.Contains))
        {
            Log($"Сторож: DNS системы сброшен ({string.Join(", ", current)}). Возвращаю «{active.Name}»");
            HideUndo();
            if (_servers.FirstOrDefault(x => x.Id == active.ServerId) is { } same && await ConnectToAsync(same, auto: true))
                ShowToast($"DNS был сброшен. Подключение «{same.DisplayName}» возвращено", "Warn");
            else
                ShowToast("DNS был сброшен, вернуть подключение не удалось: подробности в журнале", "Bad");
            return;
        }

        bool ok = await DnsSwitcher.SystemDnsWorksAsync(ProbeHosts, TimeSpan.FromSeconds(8));
        if (ok) { _watchdogFails = 0; return; }
        _watchdogFails++;
        Log($"Сторож: DNS не отвечает ({_watchdogFails}/3)");
        if (_watchdogFails < 3) return;
        _watchdogFails = 0;
        await SwitchToNextBestAsync(active);
    }

    /// <summary>Сервер умер: пробуем до трех следующих по стабильности (при равенстве избранные и с меньшим пингом).</summary>
    private async Task SwitchToNextBestAsync(DnsSwitcher.ActiveConnection active)
    {
        HideUndo(); // подключение меняется без пользователя: прежняя отмена больше не про то же состояние
        var next = _servers
            .Where(s => s.Id != active.ServerId && !s.IsJunk && s.Status == ServerStatus.Ok)
            .OrderByDescending(s => s.AllStability ?? (s.ChecksTotal == 0 ? 0 : s.ChecksOk * 100 / s.ChecksTotal))
            .ThenByDescending(s => s.IsFavorite)
            .ThenBy(s => s.LatencyMs ?? long.MaxValue)
            .Take(3)
            .ToList();
        foreach (var s in next)
        {
            Log($"Сторож: «{active.Name}» не отвечает. Переключаюсь на следующий по стабильности: «{s.DisplayName}»");
            if (await ConnectToAsync(s, auto: true))
            {
                ShowToast($"Сервер не отвечал. Переключено на {s.DisplayName}", "Warn");
                return;
            }
        }
        Log("Сторож: замена не нашлась. Отключаюсь и возвращаю исходные DNS");
        await DisconnectAsync("сервер перестал отвечать, замена не нашлась");
        MessageBox.Show($"Сервер «{active.Name}» перестал отвечать, и рабочей замены не нашлось. DNEX отключилась и вернула исходные настройки DNS.",
            "DNEX", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- VPN ----------

    private HashSet<string> _vpnSeen = new();

    /// <summary>Следит за сетью: если при подключенном DNEX включили VPN, предупреждает.</summary>
    private void WatchVpn()
    {
        _vpnSeen = SystemCheck.ActiveVpns().ToHashSet();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(OnNetworkChanged);
    }

    private void OnNetworkChanged()
    {
        var now = SystemCheck.ActiveVpns().ToHashSet();
        var added = now.Except(_vpnSeen).ToList();
        _vpnSeen = now;
        if (added.Count == 0 || DnsSwitcher.GetActive() == null) return;
        var text = $"Включен VPN: {string.Join(", ", added)}. Пока он работает, DNS из DNEX может не действовать";
        Log(text);
        ShowToast(text, "Warn");
        // Окно может быть спрятано в трей: уведомление Windows видно всегда
        _tray?.ShowBalloonTip(8000, "DNEX: включен VPN", "Пока VPN работает, DNS из DNEX может не действовать. Отключите VPN или DNEX.",
            System.Windows.Forms.ToolTipIcon.Warning);
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

    // ---------- Отмена действий ----------

    private static readonly TimeSpan UndoTime = TimeSpan.FromSeconds(8);
    private Func<Task>? _undo;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _undoDeadline;
    private TimeSpan _undoLeft;
    private bool _undoPaused;

    /// <summary>
    /// Панель «Отменить» на 8 секунд: что сделано, вторая строка с пояснением, полоска и секунды до исчезновения.
    /// Отменить можно кнопкой или Ctrl+Z. Новое действие заменяет предыдущее. Уведомление справа внизу при этом
    /// не показывается, чтобы внизу не висели две панели.
    /// </summary>
    private void OfferUndo(string text, Func<Task> undo, string? hint = null)
    {
        _undo = undo;
        UndoText.Text = text;
        SetUndoHint(hint);
        HideToast();
        UndoBar.Visibility = Visibility.Visible;
        UndoBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(180)));
        _undoPaused = false;
        StartUndoCountdown(UndoTime);
    }

    private void SetUndoHint(string? hint)
    {
        UndoHint.Text = hint ?? "";
        UndoHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartUndoCountdown(TimeSpan left)
    {
        _undoDeadline = DateTime.Now + left;
        UndoTimeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(left.TotalSeconds / UndoTime.TotalSeconds, 0, new Duration(left)));
        UndoSeconds.Text = $"{Math.Ceiling(left.TotalSeconds)} с";
        _undoTimer.Start();
    }

    private void UndoTick()
    {
        var left = _undoDeadline - DateTime.Now;
        if (left <= TimeSpan.Zero) { HideUndo(); return; }
        UndoSeconds.Text = $"{Math.Ceiling(left.TotalSeconds)} с";
    }

    // Под мышью отсчет стоит: можно спокойно прочитать и нажать
    private void UndoBar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_undo == null || _undoPaused) return;
        _undoPaused = true;
        _undoLeft = _undoDeadline - DateTime.Now;
        _undoTimer.Stop();
        var now = UndoTimeScale.ScaleX;
        UndoTimeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        UndoTimeScale.ScaleX = now;
    }

    private void UndoBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_undo == null || !_undoPaused) return;
        _undoPaused = false;
        StartUndoCountdown(_undoLeft > TimeSpan.FromSeconds(1.5) ? _undoLeft : TimeSpan.FromSeconds(1.5));
    }

    private void HideUndo()
    {
        _undo = null;
        _undoPaused = false;
        _undoTimer.Stop();
        if (UndoBar.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, Motion(160));
        fade.Completed += (_, _) => { if (_undo == null) UndoBar.Visibility = Visibility.Collapsed; };
        UndoBar.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private async Task RunUndoAsync()
    {
        // Во время подключения или отключения отмена подождет: иначе две смены DNS пошли бы одновременно
        if (_busyConn) { Log("Отменить можно после окончания подключения или отключения"); return; }
        var undo = _undo;
        HideUndo();
        if (undo == null) return;
        try { await undo(); }
        catch (Exception ex) { Log("Отменить не удалось: " + ex.Message); }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunUndoAsync();
    private void UndoClose_Click(object sender, RoutedEventArgs e) => HideUndo();

    /// <summary>Ctrl+Z во всем окне. В поле ввода работает его собственная отмена текста.</summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _undo != null && Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
            await RunUndoAsync();
        }
    }

    /// <summary>Звездочка в строке: добавить сервер в избранное или убрать.</summary>
    private void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DnsServer s }) ToggleFavorite(new[] { s });
        e.Handled = true;
    }

    private static string Names(IReadOnlyList<DnsServer> list) =>
        list.Count <= 3 ? string.Join(", ", list.Select(s => s.DisplayName)) : $"{list.Count} серверов";

    private void MenuFav_Click(object sender, RoutedEventArgs e) =>
        ToggleFavorite(ServerGrid.SelectedItems.Cast<DnsServer>().ToList());

    /// <summary>Если среди выбранных есть не избранные — все добавляются, иначе все убираются.</summary>
    private void ToggleFavorite(IReadOnlyList<DnsServer> list)
    {
        if (list.Count == 0) return;
        var was = list.Select(s => (s, s.IsFavorite)).ToList();
        bool add = list.Any(s => !s.IsFavorite);
        foreach (var s in list) s.IsFavorite = add;
        OfferUndo(add ? $"В избранное: {Names(list)}" : $"Убрано из избранного: {Names(list)}", () =>
        {
            foreach (var (srv, fav) in was) srv.IsFavorite = fav;
            SaveServers();
            if (_filterIndex == 6) _view?.Refresh();
            Log("Отменено изменение избранного");
            return Task.CompletedTask;
        });
        Log(add ? $"В избранное: {string.Join(", ", list.Select(s => s.DisplayName))}"
                : $"Убрано из избранного: {string.Join(", ", list.Select(s => s.DisplayName))}");
        SaveServers();
        if (_filterIndex == 6) _view?.Refresh();
        UpdateStats();
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var picked = ServerGrid.SelectedItems.Cast<DnsServer>().ToList();
        MenuFav.Header = picked.Count > 0 && picked.All(s => s.IsFavorite) ? "Убрать из избранного" : "В избранное";
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
        Log($"Добавлен сервер: {s.DisplayName}");
        OfferUndo($"Добавлен сервер {s.DisplayName}", () =>
        {
            if (DnsSwitcher.GetActive()?.ServerId == s.Id) { Log("Отмена не выполнена: к этому серверу уже подключено"); return Task.CompletedTask; }
            _servers.Remove(s);
            _known.Remove(s.Key);
            SaveServers();
            UpdateStats();
            Log($"Добавление отменено: {s.DisplayName}");
            return Task.CompletedTask;
        });
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

    /// <summary>Подключенный сервер всегда первый в таблице, какой бы колонкой ни была отсортирована.</summary>
    private void PinConnectedFirst()
    {
        if (_view == null) return;
        var sd = _view.SortDescriptions;
        if (sd.Count > 0 && sd[0].PropertyName == nameof(DnsServer.IsConnected)) return;
        for (int i = sd.Count - 1; i >= 0; i--)
            if (sd[i].PropertyName == nameof(DnsServer.IsConnected)) sd.RemoveAt(i);
        sd.Insert(0, new SortDescription(nameof(DnsServer.IsConnected), ListSortDirection.Descending));
    }

    // ---------- Строка и пинг ----------

    /// <summary>Кнопка в строке: полная проверка этого сервера (сервисы и пинг).</summary>
    private async void RowRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DnsServer s) await CheckOneAsync(s, null);
    }

    /// <summary>Обновить подключенный сервер: кнопка в шапке, значок крутится, пока идет проверка.</summary>
    private async void ActiveRefresh_Click(object sender, RoutedEventArgs e)
    {
        var active = DnsSwitcher.GetActive();
        var s = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        if (s != null) await CheckOneAsync(s, ActiveRefreshIcon);
    }

    /// <summary>
    /// Проверка одного сервера. Только эта строка: без общего прогресса сверху и без пересортировки списка.
    /// Анимация в самой строке (статус Checking). Если передана иконка, она тоже крутится.
    /// </summary>
    private async Task CheckOneAsync(DnsServer s, System.Windows.Shapes.Path? icon)
    {
        if (s.Status == ServerStatus.Checking) return;
        SpinIcon(icon, true);
        try
        {
            await new ServerChecker(_settings).CheckAsync(s, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"Обновление «{s.Name}» не удалось: {ex.Message}");
        }
        finally
        {
            SpinIcon(icon, false);
        }
        SaveServers();
        UpdateHeaderStats();
        Grid_SelectionChanged(this, null!);
    }

    private static void SpinIcon(System.Windows.Shapes.Path? icon, bool on)
    {
        if (icon?.RenderTransform is not RotateTransform rot) return;
        rot.BeginAnimation(RotateTransform.AngleProperty, on
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
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
    /// <summary>Серверы, возвращенные отменой автоочистки: до перезапуска программы они не убираются снова.</summary>
    private readonly HashSet<Guid> _purgeKeep = new();

    private int AutoPurge(bool silent = false)
    {
        var activeId = DnsSwitcher.GetActive()?.ServerId;
        var junk = _servers.Where(s => s.Id != activeId && s.Status != ServerStatus.Checking && !_purgeKeep.Contains(s.Id) &&
                                       (SourceParser.IsNoise(s) || s.IsJunk)).ToList();
        if (junk.Count == 0) return 0;
        // Для отмены: места в списке и прежние отметки в мусоре
        var undoInfo = junk.Select(s => (Server: s, Index: _servers.IndexOf(s),
                                         Junk: _junk.TryGetValue(s.Key, out var t) ? t : (DateTime?)null))
                           .OrderBy(r => r.Index).ToList();
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
        if (!silent)
        {
            Log($"Автоочистка: убрано нерабочих {junk.Count}, осталось {_servers.Count - junk.Count}");
            OfferUndo($"Убрано нерабочих серверов: {junk.Count}", () =>
            {
                foreach (var r in undoInfo)
                {
                    r.Server.Fading = false;                 // строка, которая еще гаснет, остается в списке
                    _purgeKeep.Add(r.Server.Id);             // возвращенные не убираются снова до перезапуска
                    if (!_servers.Contains(r.Server)) _servers.Insert(Math.Clamp(r.Index, 0, _servers.Count), r.Server);
                    _known.Add(r.Server.Key);
                    if (r.Junk is { } t) _junk[r.Server.Key] = t; else _junk.Remove(r.Server.Key);
                }
                try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
                _purgeTotal -= undoInfo.Count;
                SaveServers();
                UpdateStats();
                Log($"Автоочистка отменена: возвращено {undoInfo.Count}");
                return Task.CompletedTask;
            });
        }
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
        // Для отмены: место в списке и прежняя отметка в мусоре (удаленный сервер поиск больше не добавляет)
        var removed = sel.Select(s => (Server: s, Index: _servers.IndexOf(s),
                                       Junk: _junk.TryGetValue(s.Key, out var t) ? t : (DateTime?)null))
                         .OrderBy(r => r.Index).ToList();
        foreach (var s in sel) { _servers.Remove(s); _known.Remove(s.Key); _junk[s.Key] = DateTime.Now; }
        try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
        SaveServers();
        Log($"Удалено: {Names(sel)}");
        OfferUndo(sel.Count == 1 ? $"Удален сервер {sel[0].DisplayName}" : $"Удалено серверов: {sel.Count}", () =>
        {
            foreach (var r in removed)
            {
                if (_servers.Contains(r.Server)) continue;
                _servers.Insert(Math.Clamp(r.Index, 0, _servers.Count), r.Server);
                _known.Add(r.Server.Key);
                if (r.Junk is { } t) _junk[r.Server.Key] = t; else _junk.Remove(r.Server.Key);
            }
            try { Storage.Save(JunkFile, _junk); } catch (Exception) { }
            SaveServers();
            UpdateStats();
            Log($"Удаление отменено: {Names(removed.Select(r => r.Server).ToList())}");
            return Task.CompletedTask;
        });
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
            row.ToolTip = warn ? name + "\nБазы определяют страну по-разному" : name;
            if (_compact) continue; // компактная плитка: только флаг, название страны в подсказке
            row.Children.Add(new TextBlock { Text = warn ? name + " ⚠" : name, Foreground = muted, FontWeight = FontWeights.SemiBold });
        }
    }

    private string? CodeOfIp(string ip) =>
        _ipCountry.TryGetValue(ip, out var r) ? r.First ?? r.Second : null;

    private string CountryOfIp(string ip)
    {
        if (!GeoLocation.IsPublicIp(ip)) return "локальный адрес";
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
            if (!s.Proxied || !GeoLocation.IsPublicIp(s.ResolvedIp) || _ipCountry.ContainsKey(s.ResolvedIp)) continue;
            if (!_geoPending.Add(s.ResolvedIp)) continue; // этот адрес уже определяется другим вызовом
            // Страна голосованием баз; вторая страна в паре только когда большинства нет (тогда плитка покажет обе)
            try
            {
                var r = await GeoLocation.ResolveAsync(s.ResolvedIp, CancellationToken.None);
                if (r == null) continue;                             // сеть или лимит: определим при следующей перерисовке
                _ipCountry[s.ResolvedIp] = (r.Country, r.Disputed ? r.Second : r.Country);
            }
            finally { _geoPending.Remove(s.ResolvedIp); }
        }
        // Флаги нужных стран: скачиваем один раз, потом берем из кэша
        foreach (var code in _ipCountry.Values.Select(r => r.First ?? r.Second).Where(c => c != null).Distinct())
            await GeoLocation.FlagPathAsync(code!, CancellationToken.None);
        if (ReferenceEquals(cur, _shownSnap)) FillServiceGeo(cur);
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
                                       || (s.Country != null && s.Country2 == null)
                                       || (s.Country != null && s.GeoVotes == null)).ToList(); // данные до сверки тремя базами
        // Прокси Smart DNS, которому еще не определили страну (адрес появляется после проверки)
        var proxyTodo = _servers.Where(s => !string.IsNullOrEmpty(s.ProxyIp) && s.ProxyCountry == null).ToList();

        await Task.WhenAll(todo.Select(ResolveOneAsync).Concat(proxyTodo.Select(ResolveProxyAsync)));
        if (todo.Count > 0 || proxyTodo.Count > 0) SaveServers();   // страны должны пережить перезапуск
        UpdateConnectionLocation();
        UpdateTopList(); // флаги и страны появились: блок «Лучшие по стабильности» перерисуется с ними
    }

    /// <summary>Страна адреса прокси Smart DNS — той локации, через которую на самом деле идет доступ к ИИ.</summary>
    private async Task ResolveProxyAsync(DnsServer s)
    {
        var ip = s.ProxyIp!;
        if (!GeoLocation.IsPublicIp(ip)) { s.SetProxyLocation(null, null); return; }
        var r = await GeoLocation.ResolveAsync(ip, CancellationToken.None);
        if (r == null) return;                                     // сеть или лимит — попробуем позже
        s.SetProxyLocation(r.Country, r.Second);
    }

    private async Task ResolveOneAsync(DnsServer s)
    {
        var ip = s.Primary;
        if (string.IsNullOrEmpty(ip) && Uri.TryCreate(s.DohUrl, UriKind.Absolute, out var u))
            ip = await HostIpAsync(u.Host);
        if (string.IsNullOrEmpty(ip)) return;                        // адрес не узнали — попробуем в следующий раз
        if (!GeoLocation.IsPublicIp(ip)) { s.SetLocation(null, null); return; }

        var r = await GeoLocation.ResolveAsync(ip, CancellationToken.None);
        if (r?.Country == null) return;                              // сеть или лимит сервиса — попробуем позже
        s.SetLocation(r);
        if (await GeoLocation.FlagPathAsync(r.Country, CancellationToken.None) is { } path)
            s.Flag = LoadFlag(path);
    }

    /// <summary>IPv4 хоста для геолокации. Сначала через эталонный DoH: системный DNS может быть подменен
    /// подключенным Smart DNS или провайдером. Если эталон недоступен, через системный.</summary>
    private async Task<string?> HostIpAsync(string host)
    {
        try
        {
            var (ips, _) = await DnsWire.ResolveDohAsync(_settings.ReferenceDoh, host, TimeSpan.FromSeconds(6), CancellationToken.None);
            if (ips.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) is { } ip) return ip.ToString();
        }
        catch (Exception) { }
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
        }
        catch (Exception) { return null; }
    }

    // Флаги декодируются один раз и переиспользуются: плитки и строки обновляются часто
    private static readonly Dictionary<string, ImageSource> FlagCache = new(StringComparer.OrdinalIgnoreCase);

    private static ImageSource? LoadFlag(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (FlagCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            FlagCache[path] = img;
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
    private bool _sessionEnding;
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
        if (snap != null)
        {
            _traySnap = snap;
            SyncActiveSummary(snap);
        }
        UpdateTrayServices();
        UpdateHeaderStats();
        if (snap != null)
        {
            bool connected = DnsSwitcher.GetActive() != null;
            // Пока подключено, проверка каждые 5 минут обновляет и блок «Проверка подключения» в полном виде
            if (connected && !_compact && !_busyConn) _after = snap;
            if (_compact || connected) ShowCompare();
        }
        _trayTimer.Stop();
        _trayTimer.Start();
    }

    /// <summary>
    /// Результат проверки подключения пишем в строку активного сервера: таблица показывает то, что видит пользователь
    /// при подключении, а не старую проверку сервера. Остальные серверы не меняются.
    /// </summary>
    private void SyncActiveSummary(NetSnapshot snap)
    {
        if (snap.Services.Count == 0) return;
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        if (srv == null) return;
        string Mark(ServiceState x) => x.Access.StartsWith("✓") ? "✓" : x.Access.Contains("закрыт") ? "✗ регион" : "✗";
        srv.Summary = string.Join(" · ", snap.Services.Select(x => $"{x.Name} {Mark(x)}"));
        srv.Status = snap.Services.All(x => x.Access.StartsWith("✓")) ? ServerStatus.Ok
                   : snap.Services.Any(x => x.Access.StartsWith("✓")) ? ServerStatus.Partial
                   : ServerStatus.Fail;
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
        _trayTimer.Tick += async (_, _) =>
        {
            // Проверка по таймеру (по умолчанию каждые 5 минут) нужна, пока DNEX подключена. Без подключения
            // таймер обновляет только компактный блок, если он открыт. После выхода из программы проверок нет.
            if (DnsSwitcher.GetActive() == null && !(_compact && IsVisible)) return;
            await RefreshTrayServicesAsync();
        };
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
        var before = System.Text.Json.JsonSerializer.Serialize(_settings, Storage.Options); // для отмены
        var w = new SettingsWindow(_settings) { Owner = this };
        w.ShowDialog();
        if (w.Saved)
        {
            _settings = LoadSettings();
            ApplyTimerSettings();
            Log("Настройки сохранены");
            OfferUndo("Настройки сохранены", () =>
            {
                Storage.SaveJson("settings.json", before);
                _settings = LoadSettings();
                ApplyTimerSettings();
                Log("Сохранение настроек отменено");
                return Task.CompletedTask;
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

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_compact) _ = RefreshTrayServicesAsync(); // открыли окно — сервисы проверяем заново
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
    /// <summary>
    /// Приложения Claude и ChatGPT (Electron) держат открытые соединения и свой кэш адресов, поэтому после смены DNS
    /// могут продолжать ходить по старым адресам. Программа их не трогает: закрытие чужих соединений обрывало бы
    /// и другие программы. Пользователь перезапускает приложения сам.
    /// </summary>
    private const string RestartHint = "Если Claude или ChatGPT открыты, перезапустите их: приложения помнят старые адреса";

    private void ShowToast(string text, string colorKey = "Ok")
    {
        // Внизу одна панель: если видна отмена, уведомление становится ее второй строкой
        if (_undo != null && UndoBar.Visibility == Visibility.Visible)
        {
            SetUndoHint(text.Replace("\n", " · "));
            return;
        }
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

    /// <summary>Убирает уведомление справа внизу сразу: его место заняла панель отмены.</summary>
    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.BeginAnimation(UIElement.OpacityProperty, null);
        Toast.Opacity = 0;
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
    private void LogToggle_Changed(object sender, RoutedEventArgs e) => Reveal(LogPanel, LogToggle.IsChecked == true);

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
        foreach (var s in list) if (s.Fading) _servers.Remove(s); // отмена успела вернуть строку: ее не трогаем
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

    private int _workDepth;

    /// <summary>Начало долгой работы. Вложенная работа (подключение во время проверки или поиска) получает
    /// токен внешней: «Стоп» по-прежнему останавливает проверку, а кнопки включаются только после конца обеих.</summary>
    private CancellationToken BeginWork()
    {
        if (_workDepth++ > 0 && _cts != null) return _cts.Token;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _working = true;
        ListActions.IsEnabled = ApplyActions.IsEnabled = false;
        StopButton.Visibility = Visibility.Visible;
        UpdateStatusVisuals();
        return _cts.Token;
    }

    private void EndWork()
    {
        if (_workDepth > 0) _workDepth--;
        if (_workDepth > 0) return; // внешняя работа еще идет
        _working = false;
        ListActions.IsEnabled = ApplyActions.IsEnabled = true;
        StopButton.Visibility = Visibility.Collapsed;
        ProgressText.Text = "";
        WorkBar.Visibility = Visibility.Collapsed;
        UpdateStatusVisuals();
        UpdateStats();
    }

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _savePending;

    /// <summary>Сохранение в фоне: снимок списка берется в UI-потоке, запись на диск — вне его.
    /// Частые вызовы схлопываются в одну запись.</summary>
    private string? _lastServersJson;

    private async void SaveServers()
    {
        if (_savePending) return;
        _savePending = true;
        await Task.Delay(300);
        _savePending = false;
        // Сериализация в UI-потоке: серверы меняются только в нем, поэтому фоновый поток получает готовую строку,
        // а не живые объекты, которые в этот момент могут меняться (иначе «Collection was modified»)
        string json;
        try { json = System.Text.Json.JsonSerializer.Serialize(_servers.ToList(), Storage.Options); }
        catch (Exception ex) { Log("Не удалось сохранить список: " + ex.Message); return; }
        // ConfigureAwait(false): продолжение не ждет UI-поток. Иначе SaveServersNow при закрытии окна
        // держит UI-поток в Wait() на замке, который отпускается только в этом продолжении — взаимная блокировка.
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (json != _lastServersJson) // содержимое не менялось: на диск не пишем
            {
                await Task.Run(() => Storage.SaveJson(ServersFile, json)).ConfigureAwait(false);
                _lastServersJson = json;
            }
        }
        catch (Exception ex) { Log("Не удалось сохранить список: " + ex.Message); }
        finally { _saveLock.Release(); }
    }

    private void SaveServersNow()
    {
        _saveLock.Wait();
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_servers.ToList(), Storage.Options);
            if (json != _lastServersJson) { Storage.SaveJson(ServersFile, json); _lastServersJson = json; }
        }
        catch (Exception) { }
        finally { _saveLock.Release(); }
    }

    // ---------- Журнал ----------

    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private ICollectionView? _logView;
    private int _logFilter; // 0 — все, 1 — предупреждения и ошибки, 2 — только ошибки
    private DispatcherTimer? _logTimer;
    private const int MaxLogEntries = 500;

    private bool FilterLog(object o)
    {
        if (o is not LogEntry e) return false;
        return _logFilter switch
        {
            1 => e.Level is LogLevel.Warn or LogLevel.Error,
            2 => e.Level == LogLevel.Error,
            _ => true,
        };
    }

    /// <summary>Журнал копится в очереди и выводится пачкой раз в 200 мс — сотни строк от парсера не тормозят интерфейс.</summary>
    private void Log(string msg)
    {
        _logQueue.Enqueue(new LogEntry(DateTime.Now, msg));
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
        while (_logQueue.TryDequeue(out var entry)) _log.Add(entry);
        while (_log.Count > MaxLogEntries) _log.RemoveAt(0);
        UpdateLogCount();
        if (_logView?.Cast<object>().LastOrDefault() is { } last) LogList.ScrollIntoView(last);
    }

    private void UpdateLogCount()
    {
        if (_logView == null) return;
        var shown = _logView.Cast<object>().Count();
        LogCount.Text = shown == _log.Count ? $"{_log.Count} записей" : $"{shown} из {_log.Count}";
    }

    private void LogFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out var i)) _logFilter = i;
        _logView?.Refresh();
        UpdateLogCount();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var saved = _log.ToList();
        _log.Clear();
        _logQueue.Clear();
        Log("Журнал очищен");
        OfferUndo($"Журнал очищен: {saved.Count} строк", () =>
        {
            // Записи, появившиеся после очистки, остаются в конце
            var since = _log.ToList();
            _log.Clear();
            foreach (var entry in saved.Concat(since)) _log.Add(entry);
            return Task.CompletedTask;
        });
    }

    /// <summary>Выбранные строки, которые сейчас видны, в порядке журнала.</summary>
    private List<LogEntry> SelectedLog() =>
        _logView == null ? new() : _logView.Cast<LogEntry>().Where(x => LogList.SelectedItems.Contains(x)).ToList();

    private void CopyEntries(IEnumerable<LogEntry> items, string done)
    {
        var text = string.Join(Environment.NewLine, items.Select(x => $"[{x.TimeText}] {x.Text}"));
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            ShowToast(done);
        }
        catch (Exception) { ShowToast("Не удалось скопировать"); }
    }

    /// <summary>Кнопка «Копировать»: выбранные строки, а если их нет, все видимые.</summary>
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logView == null) return;
        var sel = SelectedLog();
        if (sel.Count > 0) CopyEntries(sel, sel.Count == 1 ? "Строка скопирована" : $"Скопировано строк: {sel.Count}");
        else CopyEntries(_logView.Cast<LogEntry>(), "Журнал скопирован");
    }

    /// <summary>Контекстное меню и Ctrl+C: выбранные строки, а если ничего не выбрано, текущая строка.</summary>
    private void CopyLine_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedLog();
        if (sel.Count == 0 && LogList.SelectedItem is LogEntry one) sel.Add(one);
        if (sel.Count > 0) CopyEntries(sel, sel.Count == 1 ? "Строка скопирована" : $"Скопировано строк: {sel.Count}");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_logView != null) CopyEntries(_logView.Cast<LogEntry>(), "Журнал скопирован");
    }

    /// <summary>Двойной щелчок по строке копирует ее одну.</summary>
    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry one) CopyEntries(new[] { one }, "Строка скопирована");
    }

    private void LogList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyLine_Click(sender, e);
            e.Handled = true;
        }
    }
}
