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

/// <summary>Проверка серверов, лучшие по стабильности, выделение по состоянию, пинг и автоочистка</summary>
public partial class MainWindow
{
    private readonly HashSet<string> _known = new();
    private int _purgeTotal;                // сколько нерабочих убрано автоочисткой с запуска


    private string? _topSig;

    /// <summary>Лучший сервер для каждого ИИ и для всех сразу (см. ServerRanking).</summary>
    private List<ServerRanking.TopPick> PickBest()
    {
        var picks = ServerRanking.PickBest(_servers, _settings.Targets.Where(t => t.Enabled).Select(t => t.Name).ToList());
        if (ProviderPick() is { } net) picks.Add(net); // общая карта: лучший у пользователей той же сети провайдера
        return picks;
    }

    /// <summary>Бейджи «лучший …» в таблице и блок «Лучшие по стабильности». Блок перестраивается, только если выбор изменился.</summary>
    private void UpdateTopList()
    {
        var picks = PickBest();

        // Лучший для всех важнее, чем лучший для одного сервиса
        foreach (var s in _servers)
        {
            var mine = picks.Where(p => p.Best == s && p.Key != "net").ToList();
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
    private void BuildTopRows(List<ServerRanking.TopPick> picks)
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
            var logos = (p.Key is "*" or "net" ? _settings.Targets.Where(t => t.Enabled).Select(t => AiLogo(t.Name)) : new[] { AiLogo(p.Key) })
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
                    Text = p.Sub ?? (b.LatencyMs is { } ms ? $"{ms} мс · " : "") + $"{b.ServiceChecks} пров.",
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
                    : p.Key == "net" ? $"{p.Best.DisplayName}\n{p.Best.Address}\nУ пользователей CHIMERION в вашей сети провайдера работает в {p.Stability}% проверок за сутки ({p.Sub})\nЩелчок выбирает сервер в списке"
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


    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        // Источники правят в settings.json руками: перечитываем файл, чтобы правка подхватилась без перезапуска
        if (!_settings.Sources.Any(s => s.Enabled)) _settings = LoadSettings();
        if (!_settings.Sources.Any(s => s.Enabled))
        {
            Log("Нет включенных источников. Откройте «Данные» → settings.json, добавьте адреса в sources с enabled: true, сохраните файл и нажмите «Найти новые серверы» еще раз.");
            ShowToast("Нет включенных источников: как добавить, написано в журнале", "Warn");
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
                Ui.SetProgress(WorkBar, p.Pages);
                lastProgress = $"Страниц {p.Pages}/{p.MaxPages} · в очереди {p.Queued} · найдено {p.Found} · новых {added.Count} · ошибок {p.Errors} · {p.Current}";
                ProgressText.Text = lastProgress;
            });
            // Найденные серверы проверяются сразу, пока поиск продолжается, а не после всего обхода.
            // Одновременно не больше трети обычного числа проверок: обход тоже занимает сеть, а перегрузка дала бы
            // ложные таймауты
            var checker = new ServerChecker(_settings);
            using var checkGate = new SemaphoreSlim(Math.Clamp(_settings.Parallelism / 3, 4, 12));
            var checks = new List<Task>();
            int checkedCount = 0;
            async Task CheckFoundAsync(DnsServer srv)
            {
                try
                {
                    await checkGate.WaitAsync(ct);
                    try { await checker.CheckAsync(srv, ct); }
                    finally { checkGate.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"Проверка {srv.DisplayName}: {ex.GetBaseException().Message}"); }
                finally { Interlocked.Increment(ref checkedCount); }
            }

            // Найденные адреса появляются в таблице сразу.
            void OnFound(DnsServer s) => Dispatcher.InvokeAsync(() =>
            {
                // Сервер из мусора, найденный снова через сутки и позже, проверяем заново: мог не пройти проверку
                // из-за сбоя сети. Свежий мусор (меньше суток) пропускаем
                if (_junk.TryGetValue(s.Key, out var junkAt) && DateTime.Now - junkAt < TimeSpan.FromHours(24)) return;
                if (!_known.Add(s.Key)) return;
                _junk.Remove(s.Key);
                s.JustAdded = true;
                _servers.Add(s);
                _ = ClearJustAddedLaterAsync(s);
                added.Add(s);
                checks.Add(CheckFoundAsync(s));
            });

            var found = await SourceParser.FetchAllAsync(_settings, Log, progress, OnFound, ct);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); // дождаться добавления
            Log($"Готово: найдено {found.Count}, новых {added.Count}");
            SaveServers();

            if (checks.Count > 0)
            {
                // Обход закончен: ждем проверки, которые еще идут
                var all = Task.WhenAll(checks.ToArray());
                while (!all.IsCompleted)
                {
                    lastProgress = $"Проверка найденных: {Volatile.Read(ref checkedCount)} из {checks.Count}";
                    ProgressText.Text = lastProgress;
                    await Task.WhenAny(all, Task.Delay(500));
                }
                await FinishChecksAsync(added, before: null);
            }
            _ = ResolveLocationsAsync();
            int purged = _purgeTotal - purgeStart;
            if (added.Count > 0 || purged > 0)
                ShowToast($"Найдено {added.Count} новых, убрано {purged} нерабочих");
        }
        catch (OperationCanceledException) { Log("Поиск остановлен"); }
        finally { beat.Stop(); EndWork(); }
    }


    private async void CheckAll_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(_view!.Cast<DnsServer>().ToList());

    private async void CheckSelected_Click(object sender, RoutedEventArgs e) =>
        await RunChecksAsync(ServerGrid.SelectedItems.Cast<DnsServer>().ToList());

    /// <summary>Меню у «Перепроверить»: полная, глубокая или только один сервис.</summary>
    /// <summary>Выделить в таблице серверы по состоянию. Нерабочие скрыты фильтром «Рабочие», поэтому показываем все.</summary>
    private void SelectWhere(Func<DnsServer, bool> match, string what)
    {
        var list = _servers.Where(s => s.Status != ServerStatus.Checking && match(s)).ToList();
        if (list.Count == 0) { ShowToast($"{what}: таких серверов нет", "Warn"); return; }
        if (AllTab.IsChecked != true) { AllTab.IsChecked = true; SearchBox.Clear(); _view?.Refresh(); }
        ServerGrid.SelectedItems.Clear();
        foreach (var s in list) ServerGrid.SelectedItems.Add(s);
        ServerGrid.ScrollIntoView(list[0]);
        ServerGrid.Focus();
        ShowToast($"{what}: выделено {list.Count}. Del удаляет, «Проверить» в меню перепроверяет");
    }

    private void SelectBad_Click(object sender, RoutedEventArgs e) =>
        SelectWhere(s => s.Status == ServerStatus.Fail && DnsSwitcher.GetActive()?.ServerId != s.Id, "Нерабочие");
    private void SelectPartial_Click(object sender, RoutedEventArgs e) => SelectWhere(s => s.Status == ServerStatus.Partial, "Частично рабочие");
    private void SelectOk_Click(object sender, RoutedEventArgs e) => SelectWhere(s => s.Status == ServerStatus.Ok, "Рабочие");
    private void SelectAllRows_Click(object sender, RoutedEventArgs e) => ServerGrid.SelectAll();
    private void SelectNone_Click(object sender, RoutedEventArgs e) => ServerGrid.UnselectAll();

    private void RecheckMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecheckMenuButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, string hint, ServerChecker.CheckOptions opt)
        {
            var mi = new MenuItem { Header = header, ToolTip = hint };
            mi.Click += async (_, _) => await RunChecksAsync(_view!.Cast<DnsServer>().ToList(), opt);
            return mi;
        }
        menu.Items.Add(Item("Полная проверка", "API и сайт каждого сервиса, один проход", ServerChecker.CheckOptions.Full));
        menu.Items.Add(Item("Глубокая проверка", "Три прохода подряд; у каждого сервиса проверяются все адреса: сайт, вход, CDN",
            new ServerChecker.CheckOptions(Deep: true)));
        menu.Items.Add(new Separator());
        foreach (var t in _settings.Targets.Where(t => t.Enabled))
        {
            var mi = Item($"Только {t.Name}", $"Проверить только {t.Name}. Статусы остальных сервисов остаются, стабильность не меняется",
                new ServerChecker.CheckOptions(Only: new[] { t.Name }));
            if (AiLogo(t.Name) is { } logo) mi.Icon = new Image { Source = logo, Width = 14, Height = 14 };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private async Task RunChecksAsync(List<DnsServer> list, ServerChecker.CheckOptions? options = null)
    {
        var opt = options ?? ServerChecker.CheckOptions.Full;
        if (list.Count == 0) { Log("Нечего проверять"); return; }
        if (CurrentDns.Text.Contains("127.0.0.1") || CurrentDns.Text.Contains("::1"))
            Log("ВНИМАНИЕ: системный DNS указывает на 127.0.0.1, похоже, включен VPN или прокси-клиент. " +
                "ИИ может открываться и без Smart DNS, поэтому результаты проверки будут завышены. Отключите VPN на время проверки.");
        var ct = BeginWork();
        var before = SnapshotCounters(list);
        var checker = new ServerChecker(_settings, opt);
        WorkBar.IsIndeterminate = false;
        WorkBar.Visibility = Visibility.Visible;
        WorkBar.Maximum = list.Count;
        Ui.SetProgress(WorkBar, 0);
        var progress = new Progress<int>(n =>
        {
            Ui.SetProgress(WorkBar, n);
            ProgressText.Text = $"Проверено {n} из {list.Count} · работают {list.Count(s => s.Status == ServerStatus.Ok)}";
            _statsTimer.Start();
        });
        var mode = opt.Deep ? "глубокая проверка" : opt.Only != null ? "только " + string.Join(", ", opt.Only) : "проверка";
        Log($"{char.ToUpper(mode[0]) + mode[1..]} {list.Count} серверов…");
        try
        {
            for (int round = 1; round <= opt.Rounds; round++)
            {
                if (opt.Rounds > 1) Log($"Проход {round} из {opt.Rounds}");
                await checker.CheckManyAsync(list, progress, ct);
            }
        }
        catch (OperationCanceledException) { Log("Проверка остановлена"); }
        finally { EndWork(); }
        // Проверка одного сервиса выстраивает список по нему, полная и глубокая — для всех сервисов
        var focus = opt.Only is { Count: 1 } only ? only.First() : null;
        if (focus != _rankFor) SetRankFocus(focus);
        await FinishChecksAsync(list, before);
        if (focus != null)
        {
            // Наверх списка — лучший для этого сервиса
            var top = _view?.Cast<DnsServer>().FirstOrDefault(s => !s.IsConnected && s.Chips.Any(c => c.Name == focus && c.State == "ok"));
            if (top != null) { ServerGrid.SelectedItem = top; ServerGrid.ScrollIntoView(top); }
        }
    }

    /// <summary>Счетчики стабильности до проверки: если проверку испортил сбой сети, они возвращаются.</summary>
    private sealed record CheckCounters(ServerStatus Status, int Total, int Ok, int FailStreak, int ServiceChecks, int AllOk, Dictionary<string, int> ServiceOk, int History);

    private static Dictionary<DnsServer, CheckCounters> SnapshotCounters(IEnumerable<DnsServer> list) =>
        list.ToDictionary(s => s, s => new CheckCounters(s.Status, s.ChecksTotal, s.ChecksOk, s.FailStreak, s.ServiceChecks, s.AllOk, new(s.ServiceOk), s.History.Count));

    /// <summary>
    /// После пакетной проверки: сохранение, итог, автоочистка. Защита от ложного карантина: если сети нет
    /// или разом перестала отвечать большая часть серверов, которые до проверки работали (от 5 штук и от 60%),
    /// это сбой сети, а не серверов. Тогда автоочистка пропускается, а счетчики стабильности этой проверки
    /// откатываются, чтобы следующая проверка не отправила рабочие серверы в мусор.
    /// </summary>
    private async Task FinishChecksAsync(List<DnsServer> list, Dictionary<DnsServer, CheckCounters>? before)
    {
        bool networkUp = await SystemCheck.NetworkUpAsync();
        var wereWorking = before?.Where(kv => kv.Value.Status is ServerStatus.Ok or ServerStatus.Partial).Select(kv => kv.Key).ToList() ?? new();
        int broke = wereWorking.Count(s => s.Status == ServerStatus.Fail);
        bool outage = !networkUp || (wereWorking.Count >= 5 && broke * 100 / wereWorking.Count >= 60);
        // Работающий VPN ведет запросы проверки через себя: часть VPN блокирует обращения к чужим DNS (защита от утечек),
        // и все серверы выглядели бы нерабочими. Такую проверку не засчитываем и мусор по ней не убираем
        var vpns = SystemCheck.ActiveVpns();
        bool vpnSkewed = vpns.Count > 0 && !outage;
        outage |= vpnSkewed;

        if (outage)
        {
            foreach (var (s, c) in before ?? new())
            {
                s.ChecksTotal = c.Total; s.ChecksOk = c.Ok; s.FailStreak = c.FailStreak;
                s.ServiceChecks = c.ServiceChecks; s.AllOk = c.AllOk; s.ServiceOk = new(c.ServiceOk);
                if (s.History.Count > c.History) s.History.RemoveRange(c.History, s.History.Count - c.History); // сбой сети не пишем в историю
                s.NotifyStability();
            }
            if (vpnSkewed)
            {
                Log($"Во время проверки работал VPN ({string.Join(", ", vpns)}): запросы шли через него, и результат может не совпадать с тем, " +
                    "что будет без VPN. Автоочистка пропущена, стабильность не изменена. Выключите VPN и нажмите «Перепроверить»");
                ShowToast("Работал VPN: проверка не засчитана, серверы не отправлены в мусор", "Warn");
            }
            else Log(networkUp
                ? $"Похоже на сбой сети: из {wereWorking.Count} работавших серверов разом перестали отвечать {broke}. Автоочистка пропущена, стабильность не изменена. Проверьте сеть и нажмите «Перепроверить»"
                : "Сеть пропала во время проверки. Автоочистка пропущена, стабильность не изменена. Когда сеть вернется, нажмите «Перепроверить»");
            if (!vpnSkewed) ShowToast("Похоже на сбой сети: серверы не отправлены в мусор", "Warn");
        }

        SaveServers();
        if (!outage) CrowdCollect(list.Where(s => s.LastChecked > DateTime.Now.AddMinutes(-30))); // в общую карту, если согласились
        _ = ResolveLocationsAsync();      // после проверки появился адрес прокси — определим его страну
        ApplyFocusRanks();                // порядок «лучшие для сервиса» по свежим результатам
        _view?.Refresh();
        Log($"Итог: работают {list.Count(s => s.Status == ServerStatus.Ok)}, " +
            $"частично {list.Count(s => s.Status == ServerStatus.Partial)}, " +
            $"не работают {list.Count(s => s.Status == ServerStatus.Fail)}");
        // Сразу после входа в Windows сеть и DNS поднимаются не мгновенно: неудача первой проверки еще ничего
        // не значит, а серверов в ней мало для распознавания сбоя. Мусор уберет следующая проверка
        if (!outage && AutoStart.StartedHidden && DateTime.Now - _startedAt < TimeSpan.FromMinutes(2))
            Log("Автоочистка отложена: проверка сразу после запуска Windows");
        else if (!outage) AutoPurge();
        SelectBest();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>Стартовый список CHIMERION с GitHub (подписанный): новые серверы добавляются и проверяются вместе с остальными.</summary>
    private async Task MergeRemoteCatalogAsync()
    {
        try
        {
            if (await RemoteCatalog.LoadAsync() is not { } cat) return;
            int added = 0;
            foreach (var c in RemoteCatalog.Servers(cat))
                if (!_junk.ContainsKey(c.Key) && _known.Add(c.Key)) { _servers.Add(c); added++; }
            if (added == 0) return;
            Log($"Список CHIMERION от {cat.Updated}: добавлено серверов {added}, сейчас проверю");
            SaveServers();
        }
        catch (Exception ex) { Log("Список CHIMERION не загрузился: " + ex.Message); }
    }


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
            CheckFavorites(results.Select(r => r.Server).ToHashSet());
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


    private const string JunkFile = "junk.json";
    /// <summary>Удаленные серверы (ключ → когда). 14 дней их не добавляем снова — ни из каталога, ни из парсера.</summary>
    private Dictionary<string, DateTime> _junk = new();

    /// <summary>
    /// Убирает мусор без участия пользователя: обычные DNS и заглушки сразу; серверы, которые не подменяют
    /// адреса ИИ (это обычный DNS) — после первой проверки; нерабочие — после 2 неудач подряд.
    /// Текущий подключенный сервер не трогаем никогда.
    /// </summary>
    /// <summary>Серверы, возвращенные отменой автоочистки: до перезапуска программы они не убираются снова.</summary>
    private readonly HashSet<Guid> _purgeKeep = new();
    private readonly DateTime _startedAt = DateTime.Now;

    private int AutoPurge(bool silent = false)
    {
        var activeId = DnsSwitcher.GetActive()?.ServerId;
        var junk = _servers.Where(s => s.Id != activeId && s.Status != ServerStatus.Checking && !_purgeKeep.Contains(s.Id) && !s.Manual &&
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
        if (silent) using (_view?.DeferRefresh()) foreach (var s in junk) _servers.Remove(s);
        else _ = FadeOutAndRemoveAsync(junk);
        SaveServers();
        if (!silent)
        {
            Log($"Автоочистка: убрано нерабочих {junk.Count}, осталось {_servers.Count - junk.Count}");
            OfferUndo($"Убрано нерабочих серверов: {junk.Count}", () =>
            {
                // Сотни строк возвращаются одним пакетом; таблица пересчитывается один раз после цикла
                using (_view?.DeferRefresh())
                {
                    foreach (var r in undoInfo)
                    {
                        r.Server.Fading = false;             // строка, которая еще гаснет, остается в списке
                        _purgeKeep.Add(r.Server.Id);         // возвращенные не убираются снова до перезапуска
                        if (!_servers.Contains(r.Server)) _servers.Insert(Math.Clamp(r.Index, 0, _servers.Count), r.Server);
                        _known.Add(r.Server.Key);
                        if (r.Junk is { } t) _junk[r.Server.Key] = t; else _junk.Remove(r.Server.Key);
                    }
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

    /// <summary>«сервер», «сервера», «серверов» по числу.</summary>
    private static string ServersWord(int n) =>
        (n % 100 is >= 11 and <= 14) ? "серверов" : (n % 10) switch { 1 => "сервер", 2 or 3 or 4 => "сервера", _ => "серверов" };
}
