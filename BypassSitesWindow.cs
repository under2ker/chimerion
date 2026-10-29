using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Сайты для обхода»: три вкладки (готовые наборы плитками, большие списки, свои сайты с проверкой) и итог
/// сверху: сколько всего доменов уйдет в обход. Ничего не сохраняет само: возвращает выбор, сохраняет ToolsWindow.
/// </summary>
internal sealed class BypassSitesWindow
{
    public sealed record Result(List<string> Own, List<string> Groups, List<string> Big);

    private readonly Window _w;
    private readonly ZapretTool _z;
    private readonly BypassCatalog.Data? _catalog;
    private readonly Func<bool> _busy;
    private readonly Func<string, string>? _toSmartDns;
    private readonly HashSet<string> _groups;
    private readonly HashSet<string> _big;
    private readonly List<string> _own;
    private readonly Dictionary<string, SiteReport?> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Border Tile, Border Mark, Controls.Icon Check, TextBlock Title)> _tiles = new();
    private readonly List<(StackPanel Section, string Category, List<BypassCatalog.Group> Groups, TextBlock Count, UniformGrid Grid)> _categories = new();
    private readonly RadioButton[] _tabs = new RadioButton[3];
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly TranslateTransform _slide = new();
    private readonly FrameworkElement[] _pages = new FrameworkElement[3];
    private readonly TextBlock _total = Ui.Hint("", 0);
    private readonly TextBlock _error = Ui.Hint("", 0, "Bad");
    private readonly TextBox _search = new() { Tag = "Найти набор или сайт, например instagram" };
    private readonly TextBox _input = new() { Tag = "Адрес сайта, например uiverse.io. Можно несколько через пробел" };
    private readonly StackPanel _ownList = new();
    private Result? _result;

    public static Result? Ask(Window owner, ZapretTool z, BypassCatalog.Data? catalog, Func<bool> busy, Func<string, string>? toSmartDns)
    {
        var w = new BypassSitesWindow(owner, z, catalog, busy, toSmartDns);
        w._w.ShowDialog();
        return w._result;
    }

    private BypassSitesWindow(Window owner, ZapretTool z, BypassCatalog.Data? catalog, Func<bool> busy, Func<string, string>? toSmartDns)
    {
        _z = z; _catalog = catalog; _busy = busy; _toSmartDns = toSmartDns;
        _groups = BypassCatalog.Normalize(z.State.Groups);
        _big = z.State.BigLists.ToHashSet();
        _own = z.UserDomains().Select(SplitDns.Clean).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _w = DnexDialog.CreateWindow("Сайты для обхода", 860);
        _w.Owner = owner;
        _w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _w.ShowInTaskbar = false;
        _w.SizeToContent = SizeToContent.Manual;
        _w.Height = Math.Min(820, SystemParameters.WorkArea.Height - 40);
        _w.ResizeMode = ResizeMode.CanResize;
        _w.MinWidth = 760; _w.MinHeight = 560;
        _w.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { Select(0); _search.Focus(); e.Handled = true; }
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Сайты для обхода",
            "zapret ускоряет эти сайты. YouTube и Discord в обходе всегда. Сайтам, закрытым для России, нужен Smart DNS", glyph: "globe"));

        // Вкладки и итог
        var bar = new DockPanel { Margin = new Thickness(0, 4, 0, 12) };
        _total.VerticalAlignment = VerticalAlignment.Center; _total.FontSize = 12;
        DockPanel.SetDock(_total, Dock.Right); bar.Children.Add(_total);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            _tabs[i] = new RadioButton { GroupName = "bypass", Style = (Style)Application.Current.Resources["Tab"], IsChecked = i == 0 };
            _tabs[i].Checked += (_, _) => Select(index);
            tabs.Children.Add(_tabs[i]);
        }
        bar.Children.Add(new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = tabs, HorizontalAlignment = HorizontalAlignment.Left }
            .Ref(Border.BackgroundProperty, "Surface2").Ref(Border.BorderBrushProperty, "Border"));
        Grid.SetRow(bar, 1); root.Children.Add(bar);

        _pages[0] = SetsPage();
        _pages[1] = BigPage();
        _pages[2] = OwnPage();
        _host.RenderTransform = _slide;
        Grid.SetRow(_host, 2); root.Children.Add(_host);

        var save = Ui.Action("Сохранить", "Primary", Save);
        var cancel = Ui.Action("Отмена", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        _error.VerticalAlignment = VerticalAlignment.Center;
        var footer = Ui.Footer(_error, save, cancel);
        footer.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        _w.Content = root;

        Select(0);
        Refresh();
        _w.Loaded += (_, _) => _ = CheckAsync(_own.Take(8).ToList());
    }

    // ---------- Готовые наборы ----------

    private FrameworkElement SetsPage()
    {
        var page = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(Ui.Action("Рекомендуемые", "Strong", () => SetGroups(_catalog?.Groups.Where(g => g.Recommend == "zapret").Select(g => g.Id) ?? Enumerable.Empty<string>())));
        buttons.Children.Add(Ui.Action("Снять все", "Ghost", () => SetGroups(Enumerable.Empty<string>())).With(b => b.Margin = new Thickness(0)));
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons);
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; } };
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top); page.Children.Add(top);
        var tip = Ui.Steps("Не знаете, что выбрать: нажмите «Рекомендуемые», затем «Сохранить». Это сайты, которые чаще всего замедляют", 0)
            .With(t => t.Margin = new Thickness(2, 0, 0, 10));
        DockPanel.SetDock(tip, Dock.Top); page.Children.Add(tip);

        var list = new StackPanel();
        if (_catalog == null)
            list.Children.Add(Ui.Card(Ui.Hint("Наборы не загрузились: нет сети и нет встроенной копии. Свои сайты можно задать на вкладке «Свои сайты»", 0, "Warn")));
        else
        {
            foreach (var cat in _catalog.Groups.GroupBy(g => g.Category.Length > 0 ? g.Category : "Другое"))
            {
                var groups = cat.ToList();
                var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                var head = new DockPanel { Margin = new Thickness(2, 0, 4, 8) };
                var all = new TextBlock { Text = "выбрать все", FontSize = 12, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Accent");
                all.MouseLeftButtonUp += (_, _) =>
                {
                    bool every = groups.All(g => _groups.Contains(g.Id));
                    foreach (var g in groups) { if (every) _groups.Remove(g.Id); else _groups.Add(g.Id); }
                    Refresh();
                };
                DockPanel.SetDock(all, Dock.Right); head.Children.Add(all);
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(new TextBlock { Text = cat.Key, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                var count = Ui.Hint("", 0).With(t => { t.Margin = new Thickness(10, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; });
                title.Children.Add(count);
                head.Children.Add(title);
                section.Children.Add(head);
                var grid = new UniformGrid { Columns = 2 };
                foreach (var g in groups) grid.Children.Add(Tile(g));
                section.Children.Add(grid);
                list.Children.Add(section);
                _categories.Add((section, cat.Key, groups, count, grid));
                all.Tag = count;
            }
            list.Children.Add(Ui.Hint($"Наборы от {_catalog.Updated}, источник: {_catalog.Source}", 0).With(t => t.FontSize = 11));
        }
        page.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        return page;
    }

    /// <summary>Плитка набора: название, метка «рекомендуем», число доменов и пояснение. Щелчок выбирает.</summary>
    private Border Tile(BypassCatalog.Group g)
    {
        var check = new Controls.Icon { Kind = "check", Size = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 }
            .Ref(Controls.Icon.ForegroundProperty, "OnToggle");
        var mark = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), Child = check, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
        var title = new TextBlock { Text = g.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(title);
        if (g.Recommend == "zapret") head.Children.Add(Ui.Badge());
        var text = new StackPanel();
        text.Children.Add(head);
        var sub = $"{g.Domains.Count} {Plural(g.Domains.Count)}" + (g.Note.Length > 0 ? " · " + g.Note : "");
        text.Children.Add(Ui.Hint(sub, 3).With(t => { t.FontSize = 11; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Right); row.Children.Add(mark); row.Children.Add(text);
        var tile = new Border { Child = row, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
                                Cursor = Cursors.Hand, Focusable = true, ToolTip = (g.Note.Length > 0 ? g.Note + "\n\n" : "") + string.Join(", ", g.Domains.Take(12)) + (g.Domains.Count > 12 ? "…" : "") };
        System.Windows.Automation.AutomationProperties.SetName(tile, g.Title);
        tile.MouseLeftButtonUp += (_, _) => Toggle(g.Id);
        tile.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Toggle(g.Id); e.Handled = true; } };
        tile.MouseEnter += (_, _) => PaintTile(g.Id);
        tile.MouseLeave += (_, _) => PaintTile(g.Id);
        _tiles[g.Id] = (tile, mark, check, title);
        return tile;
    }

    private void Toggle(string id)
    {
        if (!_groups.Remove(id)) _groups.Add(id);
        if (_tiles.TryGetValue(id, out var t)) Ui.Pop(t.Mark, .7);
        Refresh();
    }

    private void SetGroups(IEnumerable<string> ids)
    {
        _groups.Clear();
        foreach (var id in ids) _groups.Add(id);
        Refresh();
    }

    private void PaintTile(string id)
    {
        var (tile, mark, check, title) = _tiles[id];
        bool on = _groups.Contains(id), hover = tile.IsMouseOver;
        Ui.FadeBrush(tile, Border.BackgroundProperty, on ? "AccentSoft" : hover ? "SurfaceHover" : "Surface", 200);
        Ui.FadeBrush(tile, Border.BorderBrushProperty, on ? "Accent" : hover ? "ControlBorder" : "Border", 200);
        Ui.FadeBrush(mark, Border.BackgroundProperty, on ? "Accent" : "Surface", 200);
        Ui.FadeBrush(mark, Border.BorderBrushProperty, on ? "Accent" : "ControlBorder", 200);
        check.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, Ui.Motion(160)));
    }

    /// <summary>Поиск по названию набора и его доменам: лишние плитки и пустые разделы скрываются.</summary>
    private void Filter()
    {
        var q = _search.Text.Trim();
        foreach (var (section, _, groups, _, grid) in _categories)
        {
            int shown = 0;
            foreach (var g in groups)
            {
                bool match = q.Length == 0 || g.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || g.Note.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || g.Domains.Any(d => d.Contains(q, StringComparison.OrdinalIgnoreCase));
                _tiles[g.Id].Tile.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) shown++;
            }
            section.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------- Большие списки ----------

    private FrameworkElement BigPage()
    {
        var list = new StackPanel();
        list.Children.Add(Ui.Hint("Списки от сторонних проектов. Скачиваются у авторов при сохранении и потом обновляются сами", 0).With(t => t.Margin = new Thickness(2, 0, 0, 12)));
        foreach (var l in RemoteLists.All.Where(l => !l.IsIp))
        {
            var n = RemoteLists.Cached(l).Count;
            var sw = new Controls.Switch { Width = 48, IsChecked = _big.Contains(l.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            var id = l.Id;
            sw.Click += (_, _) => { if (sw.IsChecked == true) _big.Add(id); else _big.Remove(id); Refresh(); };
            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = l.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (n > 0) head.Children.Add(Ui.Hint($"{n:N0} {Plural(n)}", 0).With(t => { t.Margin = new Thickness(10, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
            text.Children.Add(head);
            text.Children.Add(Ui.Hint(l.Note, 4));
            text.Children.Add(Ui.Hint("Источник: " + new Uri(l.Url).Host + new Uri(l.Url).AbsolutePath.Split("/main/")[0], 4).With(t => t.FontSize = 11));
            var row = new DockPanel();
            DockPanel.SetDock(sw, Dock.Right); row.Children.Add(sw); row.Children.Add(text);
            list.Children.Add(Ui.Card(row).With(c => c.Padding = new Thickness(18, 14, 18, 14)));
        }
        list.Children.Add(Ui.Hint("Большой список охватывает много сайтов сразу. Если что-то стало открываться хуже, выключите его и оставьте наборы", 2, "Warn")
            .With(t => t.FontSize = 11));
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
    }

    // ---------- Свои сайты ----------

    private FrameworkElement OwnPage()
    {
        var page = new DockPanel();
        var add = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(Ui.Action("Добавить", "Primary", AddInput));
        buttons.Children.Add(Ui.Action("Проверить все", "Ghost", () => _ = CheckAsync(_own.ToList(), force: true)).With(b => b.Margin = new Thickness(0)));
        DockPanel.SetDock(buttons, Dock.Right); add.Children.Add(buttons);
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddInput(); e.Handled = true; } };
        add.Children.Add(_input);
        DockPanel.SetDock(add, Dock.Top); page.Children.Add(add);
        var tip = Ui.Hint("Поддомены включаются сами. Новый сайт программа сразу проверит и подскажет, поможет ли ему zapret или его лучше отправить в Smart DNS", 0)
            .With(t => t.Margin = new Thickness(2, 0, 0, 10));
        DockPanel.SetDock(tip, Dock.Top); page.Children.Add(tip);
        page.Children.Add(new ScrollViewer { Content = Ui.Card(_ownList, bottom: 0).With(c => { c.Padding = new Thickness(6, 6, 6, 6); c.VerticalAlignment = VerticalAlignment.Top; }), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        return page;
    }

    private void AddInput()
    {
        var hosts = _input.Text.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var bad = hosts.Where(h => SplitDns.Clean(h).Length == 0).ToList();
        var added = hosts.Select(SplitDns.Clean).Where(h => h.Length > 0 && !_own.Contains(h, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _error.Text = bad.Count > 0 ? "Не похоже на адрес сайта: " + string.Join(", ", bad.Take(3)) : "";
        if (added.Count == 0) return;
        _own.InsertRange(0, added);
        _input.Text = bad.Count > 0 ? string.Join(" ", bad) : "";
        Refresh();
        _ = CheckAsync(added.Take(8).ToList());
    }

    private void RenderOwn()
    {
        _ownList.Children.Clear();
        if (_own.Count == 0)
        {
            _ownList.Children.Add(Ui.Hint("Своих сайтов пока нет. Впишите адрес выше и нажмите «Добавить»", 0)
                .With(t => { t.Margin = new Thickness(12, 14, 12, 14); t.HorizontalAlignment = HorizontalAlignment.Center; }));
            return;
        }
        foreach (var host in _own) _ownList.Children.Add(OwnRow(host));
    }

    /// <summary>Свой сайт: адрес, итог проверки цветной плашкой с советом, «В Smart DNS» для закрытых и удаление.</summary>
    private FrameworkElement OwnRow(string host)
    {
        var row = new DockPanel { Margin = new Thickness(0), MinHeight = 44 };
        var remove = Ui.Action("", "Ghost", () => { _own.Remove(host); _checks.Remove(host); Refresh(); });
        remove.Content = new Controls.Icon { Kind = "x", Size = 14 };
        remove.Padding = new Thickness(7); remove.MinHeight = 0; remove.Margin = new Thickness(6, 0, 0, 0); remove.ToolTip = "Убрать из обхода"; remove.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);

        _checks.TryGetValue(host, out var r);
        bool checking = _checks.ContainsKey(host) && r == null;
        var (label, color, advice, smart) = !_checks.ContainsKey(host) ? ("не проверен", "Muted", "", false)
            : checking ? ("проверяю…", "Accent", "", false)
            : r!.Verdict switch
            {
                // С работающим обходом «открывается» может быть его заслугой: убрать не советуем
                SiteVerdict.Ok => ("открывается", "Ok", _z.IsRunning() ? "" : "Открывается и без обхода, его можно убрать", false),
                SiteVerdict.Refused => ("закрыт для России", "Warn", "zapret не поможет: отправьте его в Smart DNS", true),
                SiteVerdict.Throttled => ("загрузка обрывается", "Warn", "zapret поможет. Не помог: включите обход по адресам (IPSet)", false),
                SiteVerdict.Blocked or SiteVerdict.NoDns => ("заблокирован", "Warn", "Оставьте в списке: zapret его откроет", false),
                SiteVerdict.Protected => ("проверка Cloudflare", "Muted", "Откройте в браузере: программа тут ни при чем", false),
                _ => ("проверить не удалось", "Muted", "", false),
            };
        if (smart && _toSmartDns != null)
        {
            var to = Ui.Action("В Smart DNS", "Strong", () => { }).With(b => { b.Padding = new Thickness(10, 3, 10, 4); b.MinHeight = 26; b.Margin = new Thickness(8, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center; });
            to.Click += (_, _) =>
            {
                var msg = _toSmartDns(host);
                _own.Remove(host); _checks.Remove(host); // этот сайт пойдет через Smart DNS, а не через обход
                _error.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
                _error.Text = msg;
                Refresh();
            };
            DockPanel.SetDock(to, Dock.Right); row.Children.Add(to);
        }
        var chipText = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, color);
        var chip = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center, Child = chipText }
            .Ref(Border.BackgroundProperty, color switch { "Ok" => "ChipOk", "Warn" => "ChipWarn", "Accent" => "AccentSoft", _ => "Surface2" });
        DockPanel.SetDock(chip, Dock.Right); row.Children.Add(chip);

        var icon = new Controls.Icon { Kind = "globe", Size = 16, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, "Muted");
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = host, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (advice.Length > 0) text.Children.Add(Ui.Hint(advice, 2).With(t => { t.FontSize = 11; t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; }));
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(6, 4, 4, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0, 0, 0, 1) }.Ref(Border.BorderBrushProperty, "Border");
    }

    private async Task CheckAsync(IReadOnlyList<string> hosts, bool force = false)
    {
        var todo = hosts.Where(h => force || !_checks.ContainsKey(h)).Take(12).ToList();
        if (todo.Count == 0) return;
        foreach (var h in todo) _checks[h] = null;
        RenderOwn();
        await Task.WhenAll(todo.Select(async h =>
        {
            SiteReport? r;
            try { r = await SiteCheck.CheckAsync(h); } catch (Exception) { r = new SiteReport(h, SiteVerdict.Error, Array.Empty<string>(), ""); }
            if (_own.Contains(h, StringComparer.OrdinalIgnoreCase)) _checks[h] = r; else _checks.Remove(h);
            RenderOwn();
        }));
    }

    // ---------- Общее ----------

    private void Select(int i)
    {
        if (_pages[i] == null) return;
        if (_tabs[i].IsChecked != true) { _tabs[i].IsChecked = true; return; }
        if (ReferenceEquals(_host.Content, _pages[i])) return;
        _host.Content = _pages[i];
        if (!Ui.Animate) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    /// <summary>Отметки плиток, счетчики разделов и вкладок, итог числа доменов и список своих сайтов.</summary>
    private void Refresh()
    {
        foreach (var id in _tiles.Keys) PaintTile(id);
        foreach (var (_, _, groups, count, _) in _categories)
        {
            int on = groups.Count(g => _groups.Contains(g.Id));
            count.Text = on == 0 ? $"{groups.Count}" : $"выбрано {on} из {groups.Count}";
        }
        int sets = _catalog?.Groups.Count(g => _groups.Contains(g.Id)) ?? 0;
        _tabs[0].Content = sets > 0 ? $"Готовые наборы · {sets}" : "Готовые наборы";
        _tabs[1].Content = _big.Count > 0 ? $"Большие списки · {_big.Count}" : "Большие списки";
        _tabs[2].Content = _own.Count > 0 ? $"Свои сайты · {_own.Count}" : "Свои сайты";

        var domains = new HashSet<string>(_own, StringComparer.OrdinalIgnoreCase);
        domains.UnionWith(BypassCatalog.DomainsOf(_catalog, _groups));
        foreach (var l in _big.Select(RemoteLists.Get).OfType<RemoteList>().Where(l => !l.IsIp)) domains.UnionWith(RemoteLists.Cached(l));
        _total.Text = $"В обходе {domains.Count:N0} {Plural(domains.Count)}, плюс YouTube и Discord";
        RenderOwn();
    }

    private void Save()
    {
        // Идет другое действие (подбор стратегии, обновление): сохранение молча пропустилось бы
        if (_busy()) { _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad"); _error.Text = "Подождите: идет другое действие с zapret. Сохраните, когда оно закончится"; return; }
        if (_input.Text.Trim().Length > 0) AddInput(); // вписали сайт, но не нажали «Добавить»
        _result = new Result(_own.ToList(), _catalog?.Groups.Where(g => _groups.Contains(g.Id)).Select(g => g.Id).ToList() ?? _groups.ToList(), _big.ToList());
        _w.Close();
    }

    private static string Plural(int n)
    {
        int m = n % 100, k = n % 10;
        return m is >= 11 and <= 14 ? "доменов" : k == 1 ? "домен" : k is >= 2 and <= 4 ? "домена" : "доменов";
    }
}
