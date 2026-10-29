using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Telegram и YouTube»: две карточки (TG WS Proxy и zapret) с большим переключателем, состоянием, подсказкой
/// «что дальше» и ходом работы прямо в карточке; ниже настройки обхода и итоги подбора стратегии.
/// Элементы создаются один раз и обновляются на месте (Render), поэтому переключатели и смена текста анимируются,
/// а не перерисовываются заново на каждом шаге скачивания.
/// </summary>
internal sealed class ToolsWindow
{
    private readonly Window _w;
    private readonly IReadOnlyList<Tool> _tools;
    private readonly Action<string> _log;
    private readonly Action _changed;
    private readonly Action _relaunchElevated;
    private List<ZapretTool.StrategyResult>? _pick;     // итоги последнего подбора стратегии
    private CancellationTokenSource? _pickCts;          // идет подбор: можно остановить
    private readonly Dictionary<string, string> _status = new();   // последняя строка хода работы по инструменту
    private readonly HashSet<string> _errors = new();
    private bool _busy;
    private Tool? _busyTool;
    private string _verb = "Загрузка";
    private bool _syncing; // переключатели ставятся из кода: действия не запускать

    private readonly List<ToolCard> _cards = new();
    private readonly StackPanel _settingsHost = new();
    private readonly StackPanel _pickHost = new();
    private ZapretSettings? _settings;
    private string? _pickSignature;

    private static ToolsWindow? _open;

    /// <summary>Отправить домен в Smart DNS («Два DNS» → свои домены). Задает главное окно.</summary>
    private static Func<string, string>? _toSmartDns;

    /// <summary>Карточка программы: элементы, которые обновляет Render.</summary>
    private sealed class ToolCard
    {
        public Tool Tool = null!;
        public Border Badge = null!;
        public Controls.Icon BadgeIcon = null!;
        public Controls.Switch Toggle = null!;
        public Ellipse Dot = null!;
        public TextBlock State = null!, Next = null!, Version = null!, Message = null!, Stage = null!;
        public WrapPanel Actions = null!;
        public StackPanel Progress = null!;
        public ProgressBar Bar = null!;
        /// <summary>Куда человек перевел переключатель, пока это выполняется.</summary>
        public bool? Target;
    }

    /// <summary>Строки настроек zapret: создаются один раз, значения обновляются на месте.</summary>
    private sealed class ZapretSettings
    {
        public Border Card = null!;
        public Button Strategy = null!, Sites = null!, Source = null!;
        public TextBlock StrategyText = null!, StrategyHint = null!, SitesText = null!, SourceText = null!;
        public Controls.Switch Auto = null!, IpSet = null!, Games = null!;
        public FrameworkElement SourceRow = null!;
    }

    public static void Show(IReadOnlyList<Tool> tools, Action<string> log, Action changed, Action relaunchElevated, Func<string, string>? toSmartDns = null)
    {
        _toSmartDns = toSmartDns;
        if (_open != null) { _open._w.Activate(); return; }
        _open = new ToolsWindow(tools, log, changed, relaunchElevated);
        _open._w.Show();
        _ = _open.CheckUpdatesAsync(quiet: true);
    }

    private ToolsWindow(IReadOnlyList<Tool> tools, Action<string> log, Action changed, Action relaunchElevated)
    {
        _relaunchElevated = relaunchElevated;
        _tools = tools;
        _log = log;
        _changed = changed;
        _w = DnexDialog.CreateWindow("Telegram и YouTube", 820);
        _w.Closed += (_, _) => { _pickCts?.Cancel(); _open = null; };
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Telegram и YouTube",
            "Ускорение Telegram и обход замедления YouTube и Discord одним переключателем", glyph: "zap"));

        // Две карточки рядом: у каждой свой переключатель, состояние и следующий шаг
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int col = 0;
        foreach (var t in tools.OrderBy(t => t is TgProxyTool ? 0 : 1))
        {
            var card = BuildCard(t);
            Grid.SetColumn(card.Badge.Tag as Border ?? throw new InvalidOperationException(), col);
            grid.Children.Add((Border)card.Badge.Tag);
            col += 2;
        }
        root.Children.Add(grid);
        root.Children.Add(_settingsHost);
        root.Children.Add(_pickHost);

        var credits = Ui.Hint("Открытый код: TG WS Proxy (Flowseal), zapret (bol-van, Flowseal), лицензия MIT", 0)
            .With(t => { t.FontSize = 11; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.VerticalAlignment = VerticalAlignment.Center; });
        credits.ToolTip = string.Join("\n", tools.Select(t => t.Authors)) + "\nCHIMERION ставит официальные релизы с GitHub после проверки контрольной суммы";
        root.Children.Add(Ui.Footer(credits, Ui.Action("Проверить обновления", "Ghost", () => _ = CheckUpdatesAsync(quiet: false))));
        _w.Content = Ui.WindowContent(root);
        Render();
    }

    private async Task CheckUpdatesAsync(bool quiet)
    {
        foreach (var t in _tools.Where(t => t.IsInstalled))
        {
            try
            {
                var r = await t.CheckAsync();
                if (!quiet) SetStatus(t, t.UpdateAvailable ? $"Доступна версия {r.Version}" : "Установлена последняя версия", false);
            }
            catch (ToolException e) { if (!quiet) SetStatus(t, e.Message, true); }
        }
        Render();
    }

    private void SetStatus(Tool t, string text, bool error)
    {
        _status[t.Id] = text;
        if (error) _errors.Add(t.Id); else _errors.Remove(t.Id);
        Render();
    }

    /// <summary>Действие с инструментом: кнопки блокируются, ход работы пишется в карточку, итог в журнал.</summary>
    private async Task RunAsync(Tool t, string done, Func<IProgress<string>, Task> action, string verb = "Загрузка")
    {
        if (_busy) return;
        _busy = true;
        _busyTool = t;
        _verb = verb;
        _status.Remove(t.Id);
        Render();
        var progress = new Progress<string>(s => SetStatus(t, s, false));
        try
        {
            await action(progress);
            SetStatus(t, done, false);
            _log($"{t.Title}: {done.ToLowerInvariant()}");
        }
        catch (ToolException e) { SetStatus(t, e.Message, true); _log($"{t.Title}: {e.Message}"); }
        catch (Exception e) { SetStatus(t, "Ошибка: " + e.Message, true); _log($"{t.Title}: ошибка {e.Message}"); }
        finally
        {
            _busy = false;
            _busyTool = null;
            foreach (var c in _cards) c.Target = null;
            Render();
            _changed();
        }
    }

    /// <summary>Изменение настройки zapret: сохранить и, если обход работает, перезапустить его с новой настройкой.</summary>
    private void ChangeZapret(ZapretTool z, string done, Action change, Func<IProgress<string>, Task>? before = null) =>
        _ = RunAsync(z, done, async p =>
        {
            var running = z.IsRunning();
            change();
            z.SaveState();
            if (before != null) await before(p);
            if (running) { p.Report("Перезапускаю обход…"); await z.StopAsync(); await z.StartAsync(); }
        }, "Настройка");

    // ---------- Карточки программ ----------

    private ToolCard BuildCard(Tool t)
    {
        var c = new ToolCard { Tool = t };
        bool tg = t is TgProxyTool;
        c.BadgeIcon = new Controls.Icon { Kind = tg ? "send" : "zap", Size = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        c.Badge = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Child = c.BadgeIcon, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
        c.Toggle = new Controls.Switch { Width = 56, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 4, 0, 0),
                                         ToolTip = tg ? "Включить или выключить TG WS Proxy" : "Включить или выключить обход zapret" };
        System.Windows.Automation.AutomationProperties.SetName(c.Toggle, tg ? "Telegram" : "YouTube и Discord");
        c.Toggle.Click += (_, _) => Toggle_Click(c);

        var head = new DockPanel();
        DockPanel.SetDock(c.Badge, Dock.Left); head.Children.Add(c.Badge);
        DockPanel.SetDock(c.Toggle, Dock.Right); head.Children.Add(c.Toggle);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = tg ? "Telegram" : "YouTube и Discord", FontSize = 17, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Ui.Hint(tg ? "TG WS Proxy: ускорение Telegram Desktop" : "zapret: обход замедления", 2).With(h => h.FontSize = 12));
        head.Children.Add(titles);

        c.Dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        c.State = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        state.Children.Add(c.Dot); state.Children.Add(c.State);

        var about = Ui.Hint(tg ? "Telegram грузится быстрее и не обрывается. У прокси свой значок в трее"
                               : "Ускоряет YouTube, Discord и сайты из вашего списка. Нужны права администратора", 6);
        c.Next = Ui.Steps("", 10);
        c.Actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        c.Bar = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0) };
        c.Stage = Ui.Hint("", 6);
        c.Progress = new StackPanel { Visibility = Visibility.Collapsed };
        c.Progress.Children.Add(c.Bar); c.Progress.Children.Add(c.Stage);
        c.Message = Ui.Hint("", 10);
        c.Version = Ui.Hint("", 0).With(h => h.FontSize = 11);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 14, 0, 0), Child = c.Version }
            .Ref(Border.BorderBrushProperty, "Border");
        if (!tg) footer.ToolTip = "Антивирус может ругаться на драйвер WinDivert из сборки zapret: так бывает со всеми программами обхода";

        var body = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var top = new StackPanel();
        foreach (var e in new UIElement[] { head, state, about, c.Next, c.Actions, c.Progress, c.Message }) top.Children.Add(e);
        body.Children.Add(top);
        var card = Ui.Card(body, bottom: 0);
        card.Padding = new Thickness(18, 16, 18, 14);
        c.Badge.Tag = card;
        _cards.Add(c);
        return c;
    }

    private void Toggle_Click(ToolCard c)
    {
        if (_syncing) return;
        var t = c.Tool;
        if (_busy) { Render(); return; } // занято другим действием: переключатель вернется на место
        bool on = c.Toggle.IsChecked == true;
        c.Target = on;
        _ = on ? RunAsync(t, "Включено", p => t.EnableAsync(p, CancellationToken.None), "Включение")
               : RunAsync(t, "Выключено", _ => t.DisableAsync(), "Выключение");
    }

    /// <summary>Обновляет обе карточки, настройки и итоги подбора по текущему состоянию.</summary>
    private void Render()
    {
        foreach (var c in _cards) RenderCard(c);
        RenderSettings();
        RenderPick();
    }

    private void RenderCard(ToolCard c)
    {
        var t = c.Tool;
        bool installed = t.IsInstalled, running = installed && t.IsRunning();
        // Своя копия прокси у пользователя: CHIMERION ее не выключает, но ссылку для Telegram дает (настройки общие)
        bool foreign = t is TgProxyTool tg && tg.ForeignCopyRunning();
        if (foreign) running = true;
        bool busyHere = _busy && _busyTool == t;
        bool want = c.Target ?? running;

        string state, color;
        if (busyHere) (state, color) = (_verb switch { "Включение" => "Включаю…", "Выключение" => "Выключаю…", "Обновление" => "Обновляю…", "Подбор" => "Подбираю стратегию…", "Настройка" => "Применяю настройку…", _ => "Работаю…" }, "Accent");
        else if (foreign) (state, color) = ("Работает ваша копия", "Ok");
        else if (running) (state, color) = ("Работает", "Ok");
        else if (installed) (state, color) = ("Выключено", "Muted");
        else (state, color) = ("Не установлено", "Muted");
        Ui.SwapText(c.State, state, 4);
        Ui.FadeBrush(c.State, TextBlock.ForegroundProperty, color == "Muted" ? "Text" : color);
        Ui.FadeBrush(c.Dot, Shape.FillProperty, color);
        Ui.FadeBrush(c.Badge, Border.BackgroundProperty, running && !busyHere ? "Accent" : busyHere ? "AccentSoft" : "Surface2", 400);
        c.BadgeIcon.SetResourceReference(Controls.Icon.ForegroundProperty, running && !busyHere ? "OnToggle" : busyHere ? "Accent" : "Muted");

        _syncing = true;
        c.Toggle.IsChecked = want;
        c.Toggle.IsEnabled = !foreign;
        c.Toggle.IsHitTestVisible = !_busy;
        _syncing = false;
        c.Toggle.ToolTip = foreign ? "Запущена ваша копия, не из CHIMERION: выключается через ее значок в трее" : c.Toggle.ToolTip;

        // Что дальше: один короткий шаг для того, кто открыл окно впервые
        string next = busyHere || _errors.Contains(t.Id) ? ""
            : t is TgProxyTool ? (foreign ? "Ссылка для Telegram берется из настроек вашей копии"
                                 : running ? "Telegram не подключился сам: нажмите «Подключить Telegram»"
                                 : "Включите, затем нажмите «Подключить Telegram»")
            : running ? (_pick == null ? "Сайт не открывается: нажмите «Подобрать стратегию»" : "")
            : installed ? "Включите, чтобы ускорить YouTube и Discord" : "Включите: программа скачает zapret и запустит обход";
        c.Next.Text = next;
        c.Next.Visibility = next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Кнопки
        c.Actions.Children.Clear();
        if (t is TgProxyTool && running)
            c.Actions.Children.Add(Ui.Action("Подключить Telegram", "Primary", ConnectTelegram).With(b => b.IsEnabled = !_busy));
        if (t is ZapretTool z && installed)
        {
            if (_pickCts != null) c.Actions.Children.Add(Ui.Action("Остановить подбор", "Strong", () => _pickCts?.Cancel()));
            else c.Actions.Children.Add(Ui.Action("Подобрать стратегию", running ? "Primary" : "Strong", () => _ = PickAsync(z)).With(b => b.IsEnabled = !_busy));
        }
        if (t.UpdateAvailable)
        {
            var v = t.Latest!.Version;
            var up = Ui.Action($"Обновить до {v}", "Ghost", () => _ = RunAsync(t, $"Обновлено до {v}", p => t.InstallAsync(p, CancellationToken.None), "Обновление"));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Controls.Icon { Kind = "download", Size = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"Обновить до {v}", VerticalAlignment = VerticalAlignment.Center });
            up.Content = row; up.IsEnabled = !_busy;
            c.Actions.Children.Add(up);
        }
        c.Actions.Visibility = c.Actions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Ход работы или итог последнего действия
        var st = _status.GetValueOrDefault(t.Id, "");
        if (busyHere)
        {
            c.Progress.Visibility = Visibility.Visible;
            var pct = System.Text.RegularExpressions.Regex.Match(st, @"(\d{1,3})%");
            if (pct.Success) { c.Bar.IsIndeterminate = false; Ui.SetProgress(c.Bar, int.Parse(pct.Groups[1].Value)); }
            else c.Bar.IsIndeterminate = true;
            c.Stage.Text = st.Length > 0 ? st : $"{_verb}…";
            c.Message.Visibility = Visibility.Collapsed;
        }
        else
        {
            c.Progress.Visibility = Visibility.Collapsed;
            c.Bar.IsIndeterminate = false;
            c.Message.Text = st;
            c.Message.SetResourceReference(TextBlock.ForegroundProperty, _errors.Contains(t.Id) ? "Bad" : "Muted");
            c.Message.Visibility = st.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        var ver = foreign ? "Запущена ваша копия, не из CHIMERION" : installed ? (t.InstalledVersion is { } iv ? $"Версия {iv}" : "Установлено") : "Скачается с GitHub при первом включении";
        if (t.UpdateAvailable) ver += $" · доступна {t.Latest!.Version}";
        c.Version.Text = ver;
        c.Version.SetResourceReference(TextBlock.ForegroundProperty, t.UpdateAvailable ? "Accent" : "Muted");
    }

    // ---------- Настройки обхода ----------

    private void RenderSettings()
    {
        var z = _tools.OfType<ZapretTool>().FirstOrDefault();
        if (z == null || !z.IsInstalled) { _settingsHost.Children.Clear(); _settings = null; return; }
        if (_settings == null) { _settings = BuildSettings(z); _settingsHost.Children.Add(_settings.Card); }
        var s = _settings;
        var current = z.State.Strategy ?? ZapretTool.DefaultStrategy;
        s.StrategyText.Text = ZapretTool.StrategyTitle(current);
        var best = _pick?.FirstOrDefault(r => r.File != null && r.Score > 0);
        s.StrategyHint.Text = best == null ? "Лучшую найдет «Подобрать стратегию»"
            : best.File == current ? $"Включена лучшая по подбору ({best.Score} из {best.MaxScore})" : $"Лучшая по подбору: {best.Title}";
        s.SitesText.Text = $"{z.SitesCount(BypassCatalog.LoadCached())} доменов";
        s.SourceText.Text = z.State.IpSetSource is { } id && RemoteLists.Get(id) is { } rl ? rl.Title : "Сборка zapret";
        _syncing = true;
        s.Auto.IsChecked = z.State.AutoApplyBest;
        s.IpSet.IsChecked = z.State.IpSet;
        s.Games.IsChecked = z.State.Games;
        _syncing = false;
        s.SourceRow.Visibility = z.State.IpSet ? Visibility.Visible : Visibility.Collapsed;
        // Во время другого действия изменение молча не применилось бы
        foreach (var e in new UIElement[] { s.Strategy, s.Sites, s.Source, s.IpSet, s.Games }) e.IsHitTestVisible = !_busy;
        foreach (var b in new[] { s.Strategy, s.Sites, s.Source }) b.Opacity = _busy ? .6 : 1;
    }

    private ZapretSettings BuildSettings(ZapretTool z)
    {
        var s = new ZapretSettings();
        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new Controls.Icon { Kind = "sliders-horizontal", Size = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(Controls.Icon.ForegroundProperty, "Accent"));
        head.Children.Add(new TextBlock { Text = "Настройки обхода", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);

        (s.Strategy, s.StrategyText) = PickerButton(b => Menu(b, z.Strategies().Select(f =>
            (ZapretTool.StrategyTitle(f), (string?)null, f == (z.State.Strategy ?? ZapretTool.DefaultStrategy),
             (Action)(() => ChangeZapret(z, $"Стратегия {ZapretTool.StrategyTitle(f)}", () => z.State.Strategy = f))))));
        s.StrategyHint = Ui.Hint("", 2).With(h => h.FontSize = 11);
        body.Children.Add(SettingRow("settings", "Стратегия", s.StrategyHint, s.Strategy, first: true));

        (s.Sites, s.SitesText) = PickerButton(b => _ = EditSitesAsync(z));
        body.Children.Add(SettingRow("globe", "Сайты для обхода", Ui.Hint("Готовые наборы, большие списки и свои сайты", 2).With(h => h.FontSize = 11), s.Sites));

        s.Auto = SettingSwitch(on => { z.State.AutoApplyBest = on; z.SaveState(); });
        body.Children.Add(SettingRow("star", "Сразу включать лучшую после подбора", Ui.Hint("Иначе подбор покажет итоги, а выберете вы", 2).With(h => h.FontSize = 11), s.Auto));

        s.IpSet = SettingSwitch(on => ChangeZapret(z, on ? "Обход по адресам включен" : "Обход по адресам выключен", () => z.State.IpSet = on,
            async p => { if (z.State.IpSet && z.State.IpSetSource is { } id && RemoteLists.Get(id) is { } rl) { p.Report("Скачиваю список адресов…"); await RemoteLists.EnsureAsync(rl); } }));
        body.Children.Add(SettingRow("link", "Обход по адресам (IPSet)", Ui.Hint("Включайте, если сайты на Cloudflare не догружаются", 2).With(h => h.FontSize = 11), s.IpSet));

        IEnumerable<(string? Id, string Title, string Note)> sources = new (string? Id, string Title, string Note)[] { (null, "Сборка zapret", "Около 33 тысяч адресов, обновляется вместе со сборкой") }
            .Concat(RemoteLists.All.Where(l => l.IsIp).Select(l => ((string?)l.Id, l.Title, l.Note)));
        (s.Source, s.SourceText) = PickerButton(b => Menu(b, sources.Select(x => (x.Title, (string?)x.Note, z.State.IpSetSource == x.Id,
            (Action)(() => ChangeZapret(z, $"Адреса для обхода: {x.Title}", () => z.State.IpSetSource = x.Id,
                async p => { if (x.Id != null && RemoteLists.Get(x.Id) is { } rl) { p.Report("Скачиваю список адресов…"); await RemoteLists.EnsureAsync(rl); } }))))));
        s.SourceRow = SettingRow("download", "Источник адресов", Ui.Hint("Рекомендуем «CDN и хостинги»: он меньше задевает лишнее", 2).With(h => h.FontSize = 11), s.Source);
        body.Children.Add(s.SourceRow);

        s.Games = SettingSwitch(on => ChangeZapret(z, on ? "Обход для игр включен" : "Обход для игр выключен", () => z.State.Games = on));
        body.Children.Add(SettingRow("activity", "Обход и для игр", Ui.Hint("Только если тормозят игры (порты выше 1023)", 2).With(h => h.FontSize = 11), s.Games));

        s.Card = Ui.Card(body, bottom: 0);
        s.Card.Padding = new Thickness(18, 16, 18, 8);
        s.Card.Margin = new Thickness(0, 12, 0, 0);
        return s;
    }

    private Controls.Switch SettingSwitch(Action<bool> changed)
    {
        var sw = new Controls.Switch { Width = 44 };
        sw.Click += (_, _) => { if (_syncing) return; if (_busy) { Render(); return; } changed(sw.IsChecked == true); };
        return sw;
    }

    /// <summary>Кнопка выбора значения: текст обновляется на месте, справа стрелка.</summary>
    private static (Button Button, TextBlock Text) PickerButton(Action<Button> open, string icon = "chevron-down")
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(new Controls.Icon { Kind = icon, Size = 14, Margin = new Thickness(8, 1, 0, 0), Opacity = 0.8 });
        var b = new Button { Content = row, Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Margin = new Thickness(0), MaxWidth = 280 };
        b.Click += (_, _) => open(b);
        return (b, text);
    }

    /// <summary>Строка настройки: иконка, название и короткое пояснение слева, элемент справа, линия сверху.</summary>
    private static FrameworkElement SettingRow(string icon, string title, TextBlock hint, FrameworkElement control, bool first = false)
    {
        var g = new Grid { MinHeight = 54 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                                 Child = new Controls.Icon { Kind = icon, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, "Muted") }
            .Ref(Border.BackgroundProperty, "Surface2");
        g.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(hint);
        Grid.SetColumn(text, 1); g.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2); g.Children.Add(control);
        return new Border { Child = g, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) }.Ref(Border.BorderBrushProperty, "Border");
    }

    /// <summary>Меню вариантов под кнопкой выбора (темный стиль меню как у «Перепроверить»).</summary>
    private static void Menu(Button anchor, IEnumerable<(string Title, string? Hint, bool Checked, Action Click)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (title, hint, on, click) in items)
        {
            var mi = new MenuItem { Header = title, ToolTip = hint, IsCheckable = true, IsChecked = on };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ---------- Подбор стратегии ----------

    /// <summary>
    /// Подбор стратегии: каждая запускается на пару секунд и проверяется на YouTube и Discord. Без прав администратора
    /// каждая смена спрашивала бы UAC, поэтому сначала предлагаем перезапустить CHIMERION с правами.
    /// </summary>
    private async Task PickAsync(ZapretTool z)
    {
        if (_busy) return;
        if (!DnsSwitcher.IsAdmin())
        {
            if (DnexDialog.Ask("Нужны права администратора",
                    "Подбор перебирает около 20 стратегий, и без прав Windows спрашивала бы разрешение на каждую. " +
                    "Перезапустить CHIMERION с правами администратора? Подключение DNS сохранится.", "Перезапустить", kind: DialogKind.Info, windowTitle: "Подбор стратегии"))
                _relaunchElevated();
            return;
        }
        _busy = true;
        _busyTool = z;
        _verb = "Подбор";
        _status.Remove(z.Id);
        _pickCts = new CancellationTokenSource();
        _pick = null;
        _log("zapret: подбор стратегии");
        try
        {
            _pick = await z.PickAsync(new Progress<string>(s => SetStatus(z, s, false)), _pickCts.Token);
            var best = _pick.FirstOrDefault(r => r.File != null && r.Score > 0);
            var current = z.State.Strategy ?? ZapretTool.DefaultStrategy;
            var apply = best != null && z.State.AutoApplyBest && best.File != current;
            if (apply)
            {
                // Включено «сразу включать лучшую»: перезапуск с ней, выбор в списке ниже остается
                SetStatus(z, $"Включаю лучшую: {best!.Title}…", false);
                z.State.Strategy = best.File;
                z.SaveState();
                if (z.IsRunning()) await z.StopAsync();
                await z.EnableAsync(new Progress<string>(s => SetStatus(z, s, false)), CancellationToken.None);
            }
            var text = best == null ? "Ни одна стратегия не открыла ни YouTube, ни Discord, ни ваши сайты"
                : apply ? $"Включена лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}"
                : best.File == current ? $"Лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}. Она уже включена"
                : $"Лучшая: {best.Title}, открылось {best.Score} из {best.MaxScore}. Включите ее в итогах ниже";
            SetStatus(z, text, best == null);
            _log("zapret: подбор завершен. " + text);
        }
        catch (OperationCanceledException) { SetStatus(z, "Подбор остановлен, прежняя стратегия возвращена", false); }
        catch (ToolException e) { SetStatus(z, e.Message, true); }
        finally
        {
            _busy = false;
            _busyTool = null;
            _pickCts = null;
            Render();
            _changed();
        }
    }

    /// <summary>Итоги подбора таблицей: стратегия, YouTube, Discord, ваши сайты, время и кнопка «Выбрать».</summary>
    private void RenderPick()
    {
        var z = _tools.OfType<ZapretTool>().FirstOrDefault();
        var current = z?.State.Strategy ?? ZapretTool.DefaultStrategy;
        var sig = _pick == null || z == null ? null : $"{_pick.GetHashCode()}|{current}|{_busy}";
        if (sig == _pickSignature) return;
        _pickSignature = sig;
        _pickHost.Children.Clear();
        if (sig == null) return;

        var body = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = _pick!.FirstOrDefault(r => r.File != null && r.Score > 0);
        if (top != null && top.File != current)
        {
            var apply = Ui.Action($"Включить лучшую: {top.Title}", "Primary", () => _ = Choose(z!, top)).With(b => { b.IsEnabled = !_busy; b.Margin = new Thickness(0); });
            DockPanel.SetDock(apply, Dock.Right); head.Children.Add(apply);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Итоги подбора", FontSize = 16, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(Ui.Hint(top == null ? "Ни одна стратегия не помогла" : $"Лучшая открыла {top.Score} из {top.MaxScore}", 2).With(h => h.FontSize = 11));
        head.Children.Add(titles);
        body.Children.Add(head);
        if (_pick!.FirstOrDefault(r => r.File == null) is { } none && none.Score == none.MaxScore)
            body.Children.Add(Ui.Hint("YouTube, Discord и ваши сайты открываются и без обхода: zapret сейчас не нужен", 0, "Ok").With(h => h.Margin = new Thickness(0, 0, 0, 8)));

        bool sites = _pick!.Any(r => r.SitesTotal > 0);
        var header = PickGrid(sites);
        int col = 0;
        foreach (var h in new[] { "Стратегия", "YouTube", "Discord" }.Concat(sites ? new[] { "Ваши сайты" } : Array.Empty<string>()).Append("Время"))
        {
            var tb = Ui.Hint(h, 0).With(x => x.FontSize = 11); Grid.SetColumn(tb, col++); header.Children.Add(tb);
        }
        header.Margin = new Thickness(8, 0, 8, 4);
        body.Children.Add(header);
        foreach (var r in _pick!.Where(r => r.File != null).Take(8))
        {
            bool chosen = r.File == current, isTop = ReferenceEquals(r, top);
            var g = PickGrid(sites);
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = r.Title, FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            if (isTop) name.Children.Add(Ui.Badge());
            g.Children.Add(name);
            int c = 1;
            foreach (var ok in new[] { r.YouTube, r.Discord })
            {
                var mark = new Controls.Icon { Kind = ok ? "circle-check" : "circle-x", Size = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }
                    .Ref(Controls.Icon.ForegroundProperty, ok ? "Ok" : "Bad");
                Grid.SetColumn(mark, c++); g.Children.Add(mark);
            }
            if (sites)
            {
                var failed = r.SitesFailed is { Count: > 0 } f ? "Не догрузились: " + string.Join(", ", f) : null;
                var st = new TextBlock { Text = r.SitesTotal > 0 ? $"{r.SitesOk!.Count} из {r.SitesTotal}" : "-", VerticalAlignment = VerticalAlignment.Center, ToolTip = failed }
                    .Ref(TextBlock.ForegroundProperty, r.SitesTotal > 0 && r.SitesOk!.Count == r.SitesTotal ? "Ok" : r.SitesOk?.Count > 0 ? "Warn" : "Muted");
                Grid.SetColumn(st, c++); g.Children.Add(st);
            }
            var ms = new TextBlock { Text = r.Score > 0 ? $"{r.Ms} мс" : "-", VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(ms, c++); g.Children.Add(ms);
            if (r.Score > 0)
            {
                var pick = Ui.Action(chosen ? "Выбрана" : "Выбрать", chosen ? "Ghost" : "Strong", () => _ = Choose(z!, r))
                    .With(b => { b.IsEnabled = !_busy && !chosen; b.Margin = new Thickness(0); b.Padding = new Thickness(12, 4, 12, 5); b.MinHeight = 28; });
                Grid.SetColumn(pick, c); g.Children.Add(pick);
            }
            var row = new Border { Child = g, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8) };
            if (chosen) row.SetResourceReference(Border.BackgroundProperty, "Surface2");
            body.Children.Add(row);
        }
        var card = Ui.Card(body, bottom: 0);
        card.Padding = new Thickness(18, 16, 18, 12);
        card.Margin = new Thickness(0, 12, 0, 0);
        _pickHost.Children.Add(card);
    }

    private static Grid PickGrid(bool sites)
    {
        var g = new Grid();
        foreach (var w in new[] { -1.0, 80, 80 }.Concat(sites ? new[] { 100.0 } : Array.Empty<double>()).Concat(new[] { 80.0, 110 }))
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private Task Choose(ZapretTool z, ZapretTool.StrategyResult r) => RunAsync(z, $"Стратегия {r.Title}", async p =>
    {
        z.State.Strategy = r.File;
        z.SaveState();
        if (z.IsRunning()) await z.StopAsync();
        await z.EnableAsync(p, CancellationToken.None);
    }, "Настройка");

    /// <summary>
    /// Окно «Сайты для обхода»: готовые наборы и большие списки галочками (с числом доменов), свои сайты строками.
    /// Сохранение пересобирает список zapret и сразу применяет его.
    /// </summary>
    private async Task EditSitesAsync(ZapretTool z)
    {
        var catalog = await BypassCatalog.LoadAsync();
        var w = DnexDialog.CreateWindow("Сайты для обхода", Ui.Wide);
        w.Owner = _w;
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ShowInTaskbar = false;
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Сайты для обхода",
            "Обход действует на эти сайты и на YouTube с Discord. Сайтам, закрытым для России, нужен Smart DNS, а не обход"));

        // Готовые наборы: две колонки галочек, пояснение в подсказке
        root.Children.Add(Ui.Section("Готовые наборы", first: true));
        var checks = new List<(CheckBox Box, string Id)>();
        var grid = new StackPanel();
        if (catalog == null)
            root.Children.Add(Ui.Card(Ui.Hint("Наборы не загрузились: нет сети и нет встроенной копии. Свои сайты можно задать ниже", 0, "Warn")));
        else
        {
            var chosen = BypassCatalog.Normalize(z.State.Groups);
            var groupsPanel = new StackPanel();
            foreach (var cat in catalog.Groups.GroupBy(g => g.Category.Length > 0 ? g.Category : "Другое"))
            {
                var wrap = new WrapPanel { ItemWidth = 245 }; // две колонки, у каждой строки своя высота
                var inCat = new List<CheckBox>();
                foreach (var g in cat)
                {
                    var cb = Ui.SetCheck(g.Title, g.Domains.Count, g.Note, g.Recommend == "zapret", chosen.Contains(g.Id));
                    cb.Tag = g.Id;
                    checks.Add((cb, g.Id));
                    inCat.Add(cb);
                    wrap.Children.Add(cb);
                }
                groupsPanel.Children.Add(Ui.CategoryHeader(cat.Key, inCat, groupsPanel.Children.Count == 0));
                groupsPanel.Children.Add(wrap);
            }
            grid.Children.Add(groupsPanel);
            var groupsBody = new StackPanel();
            var recIds = catalog.Groups.Where(g => g.Recommend == "zapret").Select(g => g.Id).ToHashSet();
            groupsBody.Children.Add(Ui.Steps("Не знаете, что выбрать: нажмите «Рекомендуемые», затем «Сохранить». Это сайты, которые режут провайдеры. " +
                                             "У каждого раздела есть «выбрать все»", 0).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
            groupsBody.Children.Add(Ui.ChoiceBar(checks.Select(c => c.Box).ToList(), c => recIds.Contains((string)c.Tag)));
            groupsBody.Children.Add(Ui.Scroll(grid, Math.Clamp(SystemParameters.WorkArea.Height - 920, 260, 560))); // больше наборов видно сразу на большом экране
            groupsBody.Children.Add(Ui.Hint($"Наборы от {catalog.Updated}, источник: {catalog.Source}", 8).With(t => t.FontSize = 11));
            root.Children.Add(Ui.Card(groupsBody));
        }

        // Большие списки от сторонних проектов: качаются у авторов при сохранении и перед каждым запуском обхода
        root.Children.Add(Ui.Section("Большие списки"));
        var bigChecks = new List<(CheckBox Box, string Id)>();
        var bigBody = new StackPanel();
        foreach (var l in RemoteLists.All.Where(l => !l.IsIp))
        {
            var n = RemoteLists.Cached(l).Count;
            var cb = Ui.Toggle(z.State.BigLists.Contains(l.Id)).With(c => c.ToolTip = l.Url);
            bigChecks.Add((cb, l.Id));
            bigBody.Children.Add(Ui.Row(l.Title + (n > 0 ? $" ({n})" : ""), l.Note, cb));
        }
        root.Children.Add(Ui.Card(bigBody));

        root.Children.Add(Ui.Section("Свои сайты"));
        var box = Ui.MultiLine(string.Join(Environment.NewLine, z.UserDomains()), 110);
        var ownBody = new StackPanel();
        ownBody.Children.Add(box);
        ownBody.Children.Add(Ui.Hint("По одному в строке, поддомены включаются. Новые сайты CHIMERION проверит сама и подскажет, " +
                                     "поможет ли им zapret или их лучше отправить в Smart DNS", 8).With(t => t.FontSize = 11));
        // Проверка своих сайтов: сама для новых (когда поле теряет фокус) и по кнопке для всех
        var checkedHosts = new HashSet<string>(z.UserDomains(), StringComparer.OrdinalIgnoreCase);
        var results = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var checkBtn = Ui.Action("Проверить сайты", "Ghost", () => { }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(-10, 6, 0, 0); });
        List<string> Hosts() => box.Text.Split('\n').Select(SplitDns.Clean).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        async Task CheckSitesAsync(IReadOnlyList<string> hosts)
        {
            if (hosts.Count == 0) return;
            checkBtn.IsEnabled = false;
            results.Children.Clear();
            results.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 6) });
            results.Children.Add(Ui.Hint($"Проверяю {string.Join(", ", hosts.Take(8))}…", 0));
            var checks2 = await Task.WhenAll(hosts.Take(8).Select(async h => { try { return await SiteCheck.CheckAsync(h); } catch (Exception) { return null; } }));
            results.Children.Clear();
            foreach (var r in checks2.Where(r => r != null))
            {
                var (text, brush, smart) = r!.Verdict switch
                {
                    // С работающим обходом «открывается» может быть его заслугой: убрать не советуем
                    SiteVerdict.Ok => (z.IsRunning() ? "открывается" : "открывается и без обхода, его можно убрать из списка", "Ok", false),
                    SiteVerdict.Refused => ("сам закрыт для России: zapret не поможет, отправьте его в Smart DNS", "Warn", true),
                    SiteVerdict.Throttled => ("загрузка обрывается: zapret поможет, если не поможет, включите обход по адресам (IPSet)", "Warn", false),
                    SiteVerdict.Blocked or SiteVerdict.NoDns => ("заблокирован: оставьте в списке, zapret его откроет", "Warn", false),
                    SiteVerdict.Protected => ("закрыт проверкой Cloudflare: откройте в браузере, CHIMERION тут ни при чем", "Muted", false),
                    _ => ("проверить не удалось", "Muted", false),
                };
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                if (smart && _toSmartDns != null)
                {
                    var host = r.Host;
                    Button? to = null;
                    to = Ui.Action("В Smart DNS", "Strong", () =>
                    {
                        var msg = _toSmartDns(host);
                        // Убираем из обхода: этот сайт пойдет через Smart DNS
                        box.Text = string.Join(Environment.NewLine, box.Text.Split('\n').Select(l => l.Trim())
                                       .Where(l => l.Length > 0 && !string.Equals(SplitDns.Clean(l), host, StringComparison.OrdinalIgnoreCase)));
                        to!.IsEnabled = false;
                        to.Content = "Отправлен";
                        to.ToolTip = msg;
                    }).With(b => { b.Padding = new Thickness(10, 3, 10, 3); b.MinHeight = 26; b.Margin = new Thickness(8, 0, 0, 0); });
                    DockPanel.SetDock(to, Dock.Right);
                    row.Children.Add(to);
                }
                row.Children.Add(Ui.Hint($"{r.Host}: {text}", 0, brush).With(t => t.VerticalAlignment = VerticalAlignment.Center));
                results.Children.Add(row);
            }
            foreach (var h in hosts) checkedHosts.Add(h);
            checkBtn.IsEnabled = true;
        }
        checkBtn.Click += (_, _) => _ = CheckSitesAsync(Hosts());
        box.LostKeyboardFocus += (_, _) => _ = CheckSitesAsync(Hosts().Where(h => !checkedHosts.Contains(h)).ToList());
        ownBody.Children.Add(checkBtn);
        ownBody.Children.Add(results);
        root.Children.Add(Ui.Card(ownBody, bottom: 0));

        var err = Ui.Hint("", 0, "Bad");
        var save = Ui.Action("Сохранить", "Primary", () =>
        {
            var lines = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var bad = lines.Where(l => SplitDns.Clean(l).Length == 0).ToList();
            if (bad.Count > 0) { err.Text = "Не похоже на домен: " + string.Join(", ", bad.Take(3)); return; }
            var groups = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList();
            var big = bigChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToList();
            // Идет другое действие (подбор стратегии, обновление): RunAsync его не прервет и молча пропустит сохранение.
            // Раньше окно при этом закрывалось, и введенные сайты терялись
            if (_busy) { err.Text = "Подождите: идет другое действие с zapret. Сохраните, когда оно закончится"; return; }
            w.Close();
            _ = RunAsync(z, $"Сайты для обхода сохранены: наборов {groups.Count}, больших списков {big.Count}, своих {lines.Count}", async p =>
            {
                if (big.Count > 0) p.Report("Скачиваю большие списки…");
                await z.SaveSitesAsync(lines, groups, big, catalog);
            });
        });
        var cancel = Ui.Action("Отмена", "Ghost", () => w.Close()).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(err, save, cancel));
        w.Content = Ui.WindowContent(root);
        w.ShowDialog();
    }

    private void ConnectTelegram()
    {
        var tg = _tools.OfType<TgProxyTool>().First();
        if (TgProxyTool.ProxyLink() is not { } link)
        {
            SetStatus(tg, "Настройки прокси еще не созданы. Подождите пару секунд после включения и повторите", true);
            return;
        }
        AutoStart.OpenUrl(link);
        SetStatus(tg, "Ссылка отправлена в Telegram: подтвердите подключение к прокси в его окне", false);
        _log("Telegram: открыта ссылка подключения к прокси");
    }
}
