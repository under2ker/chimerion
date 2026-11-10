using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Два DNS»: какие сайты идут через Smart DNS (ИИ всегда, плюс отмеченные сервисы, закрытые для России),
/// а какие через обычный DNS. Сверху схема «что куда идет», она меняется вместе с галочками. Слева выбор сервисов
/// с поиском по названию и домену, справа обычный DNS для остального интернета, свои домены и наборы для разработчиков.
/// Сохранение пишет settings.json; если подключение активно, главное окно переподключается, чтобы режим заработал сразу.
/// </summary>
internal sealed class SplitDnsWindow
{
    private readonly Window _w;
    private readonly AppSettings _settings;
    private readonly SplitDnsSettings _edit;
    private readonly StackPanel _presets = new();
    private readonly Dictionary<string, GeneralDnsCheck> _checks = new();
    private readonly TextBox _ip1 = new() { Width = 150, Tag = "IP 1" };
    private readonly TextBox _ip2 = new() { Width = 150, Tag = "IP 2", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBox _doh = new() { Tag = "DoH: https://…/dns-query", Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _enabled = new();
    private readonly TextBlock _state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _checkState = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Muted") };
    private readonly Button _checkBtn = Ui.Action("Проверить все", "Strong", () => { });
    /// <summary>Выбрать рекомендуемый общий DNS; видна, только когда выбран другой.</summary>
    private readonly Button _pickRecBtn = Ui.Action("Выбрать рекомендуемый", "Ghost", () => { });
    private string _selected;
    private StackPanel? _customPanel;
    /// <summary>
    /// Рекомендуемый общий DNS. До проверки «Не менять DNS системы»: ничего не ломает, в Smart DNS уходят только ИИ.
    /// После «Проверить все» лучший по проверке: все российские сайты с настоящими адресами и больше всего открытого зарубежного.
    /// </summary>
    private string _recommended = SplitDns.Presets[0].Name;
    private TextBox _extraBox = Ui.MultiLine("", 64);
    private readonly List<CheckBox> _groupChecks = new();
    private readonly Dictionary<CheckBox, BypassCatalog.Group> _groupOf = new();
    /// <summary>Разделы списка сервисов: заголовок, панель галочек и сами галочки (для поиска).</summary>
    private readonly List<(FrameworkElement Header, Panel Items, List<CheckBox> Checks)> _sections = new();
    private readonly TextBox _search = new() { Tag = "Найти сервис: Canva, Figma, Adobe, Cursor…" };
    private readonly TextBlock _searchState = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _routeSmart = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _routeGeneral = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) };
    private bool _checking;

    public bool Saved { get; private set; }

    private static ResourceDictionary Res => Application.Current.Resources;
    private const string Custom = "Свой DNS";

    public SplitDnsWindow(AppSettings settings, Window? owner)
    {
        _settings = settings;
        var s = settings.SplitDns;
        _edit = new SplitDnsSettings
        {
            Enabled = s.Enabled, GeneralName = s.GeneralName, GeneralAddresses = s.GeneralAddresses.ToList(),
            GeneralDoh = s.GeneralDoh, ExtraAiDomains = s.ExtraAiDomains.ToList(), Groups = s.Groups.ToList(),
        };
        _selected = SplitDns.Presets.Any(p => p.Name == _edit.GeneralName) ? _edit.GeneralName : Custom;
        if (_selected == Custom)
        {
            _ip1.Text = _edit.GeneralAddresses.ElementAtOrDefault(0) ?? "";
            _ip2.Text = _edit.GeneralAddresses.ElementAtOrDefault(1) ?? "";
            _doh.Text = _edit.GeneralDoh ?? "";
        }
        _extraBox.Text = string.Join(Environment.NewLine, _edit.ExtraAiDomains);
        _enabled.IsChecked = _edit.Enabled;

        _w = DnexDialog.CreateWindow("Два DNS", Math.Min(980, Ui.WorkAreaOf(null).Width - 40));
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; _w.ShowInTaskbar = false; }

        var root = Ui.Root();
        root.Margin = new Thickness(24, 22, 24, 4);
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Два DNS",
            "Нейросети и отмеченные сервисы идут через Smart DNS, весь остальной интернет через обычный DNS", glyph: "arrow-left-right"));

        // Включение и схема: что куда пойдет при текущем выборе
        var top = new StackPanel();
        top.Children.Add(Ui.Row("Включить два DNS", "Заработает при подключении к серверу или сразу, если подключение уже есть", _enabled));
        top.Children.Add(Ui.Separator().With(x => x.Margin = new Thickness(0, 14, 0, 12)));
        top.Children.Add(Route("Через Smart DNS", "Accent", _routeSmart));
        top.Children.Add(Route("Через обычный DNS", "Muted", _routeGeneral).With(r => r.Margin = new Thickness(0, 8, 0, 0)));
        root.Children.Add(Ui.Card(top).With(c => c.Margin = new Thickness(0, 18, 0, 0)));
        _enabled.Checked += (_, _) => UpdateRoutes();
        _enabled.Unchecked += (_, _) => UpdateRoutes();

        _checkBtn.Click += (_, _) => _ = CheckAllAsync();
        root.Children.Add(Ui.Columns(SmartSide(), GeneralSide(), 860, 3, 2).With(g => g.Margin = new Thickness(0, 4, 0, 0)));

        // Кнопки всегда видны внизу окна, вне прокрутки
        var save = Ui.Action("Сохранить", "Primary", Save).With(b => b.IsDefault = true);
        var cancel = Ui.Action("Отмена", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        _state.Margin = new Thickness(0, 0, 12, 0);
        _state.VerticalAlignment = VerticalAlignment.Center;
        var footer = Ui.Footer(_state, save, cancel);
        footer.Margin = new Thickness(24, 0, 24, 20);
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        var scroll = Ui.WindowContent(root);
        scroll.MaxHeight = Ui.WorkAreaOf(null).Height - 160;
        dock.Children.Add(scroll);
        _w.Content = dock;
        RenderPresets();
        UpdateRoutes();
    }

    /// <summary>Строка схемы: цветная метка пути слева, что по нему идет справа.</summary>
    private static FrameworkElement Route(string title, string brush, TextBlock what)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        label.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(System.Windows.Shapes.Shape.FillProperty, brush));
        label.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        g.Children.Add(label);
        Grid.SetColumn(what, 1);
        g.Children.Add(what);
        return g;
    }

    /// <summary>Схема сверху: сколько сервисов и доменов уйдет в Smart DNS и какой DNS у остального.</summary>
    private void UpdateRoutes()
    {
        var on = _enabled.IsChecked == true;
        var chosen = _groupChecks.Where(c => c.IsChecked == true).Select(c => _groupOf[c]).ToList();
        var extra = _extraBox.Text.Split('\n').Select(SplitDns.Clean).Count(d => d.Length > 0);
        var parts = new List<string> { "ChatGPT, Claude и Gemini" };
        if (chosen.Count > 0) parts.Add($"сервисов из списка: {chosen.Count} ({chosen.Sum(g => g.Domains.Count)} доменов)");
        if (extra > 0) parts.Add($"своих доменов: {extra}");
        _routeSmart.Text = string.Join(" · ", parts) + (chosen.Count == 0 ? ". Сервисы из списка не отмечены: Canva, Figma и другие пойдут обычным путем" : "");
        _routeGeneral.Text = $"Все остальные сайты · {(_selected == Custom ? "свой DNS" : _selected)}";
        _routeSmart.SetResourceReference(TextBlock.ForegroundProperty, on ? "Text" : "Muted");
        _routeGeneral.SetResourceReference(TextBlock.ForegroundProperty, on ? "Text" : "Muted");
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _state.Text = on ? "" : "Два DNS выключены: все сайты идут через Smart DNS сервера, как обычно";
    }

    /// <summary>Левая колонка: нейросети (всегда) и сервисы, закрытые для России, с поиском.</summary>
    private FrameworkElement SmartSide()
    {
        var side = new StackPanel();
        side.Children.Add(Ui.Section("Что идет через Smart DNS"));

        // Нейросети: всегда, без галочек. Полный список доменов по кнопке
        var ns = SplitDns.Namespaces(new AppSettings()).Where(n => !n.StartsWith('.')).ToList();
        var ai = new StackPanel();
        var head = new DockPanel();
        var logos = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        foreach (var k in new[] { "openai", "claude", "gemini" }) logos.Children.Add(new Controls.Icon { Kind = k, Size = 18, Margin = new Thickness(0, 0, 6, 0) });
        DockPanel.SetDock(logos, Dock.Left);
        head.Children.Add(logos);
        var always = new TextBlock { Text = "всегда", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Ok");
        DockPanel.SetDock(always, Dock.Right);
        head.Children.Add(always);
        head.Children.Add(new TextBlock { Text = "ChatGPT, Claude и Gemini", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        ai.Children.Add(head);
        ai.Children.Add(Ui.Hint($"{ns.Count} доменов. Поиск Google, почта и YouTube остаются на обычном DNS", 6).With(t => t.FontSize = 12));
        var full = Ui.Hint(string.Join(", ", ns), 8).With(t => { t.FontSize = 11; t.Visibility = Visibility.Collapsed; });
        var show = new Button { Content = "Показать домены", Style = (Style)Res["Ghost"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        show.Click += (_, _) =>
        {
            var open = full.Visibility != Visibility.Visible;
            full.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            show.Content = open ? "Скрыть домены" : "Показать домены";
        };
        ai.Children.Add(show);
        ai.Children.Add(full);
        side.Children.Add(Ui.Card(ai));

        // Сервисы, которые сами закрылись для России: только отмеченные пойдут через Smart DNS
        var sets = new StackPanel();
        sets.Children.Add(Ui.CardTitle("Сервисы, закрытые для России"));
        sets.Children.Add(Ui.Hint("Отметьте те, которыми пользуетесь. Без галочки сервис идет через обычный DNS, видит российский адрес и может не открыться", 4).With(t => t.FontSize = 12));
        _search.Margin = new Thickness(0, 12, 0, 0);
        _search.TextChanged += (_, _) => ApplySearch();
        sets.Children.Add(_search);
        sets.Children.Add(_searchState);

        var catalog = BypassCatalog.LoadCached();
        var list = new StackPanel();
        if (catalog == null)
            sets.Children.Add(Ui.Hint("Список сервисов еще не загружен: он скачивается при подключении к интернету. Откройте окно чуть позже", 10, "Warn"));
        else
        {
            var chosen = BypassCatalog.Normalize(_edit.Groups);
            var smart = catalog.Groups.Where(g => g.Recommend == "smartdns").ToList();
            var other = catalog.Groups.Where(g => g.Recommend != "smartdns").ToList();
            sets.Children.Add(Ui.ChoiceBar(_groupChecks, c => _groupOf[c].Recommend == "smartdns").With(w => w.Margin = new Thickness(0, 12, 0, 4)));
            sets.Children.Add(SupportBar(catalog));
            foreach (var cat in smart.GroupBy(g => g.Category.Length > 0 ? g.Category : "Другое"))
                AddSection(list, cat.Key, cat, chosen, null);
            if (other.Count > 0)
                AddSection(list, "Обычно открываются через zapret", other, chosen,
                    "Эти сайты режет провайдер, а не сам сервис: надежнее включить «YouTube и Discord» в «Telegram и YouTube». Через Smart DNS они открываются, только если сервер их поддерживает");
        }
        sets.Children.Add(Ui.Scroll(list, 380).With(x => x.Margin = new Thickness(0, 4, 0, 0)));
        side.Children.Add(Ui.Card(sets, bottom: 0));
        return side;
    }

    /// <summary>Раздел списка сервисов: заголовок с «выбрать все», необязательное пояснение и галочки в две колонки.</summary>
    private void AddSection(Panel list, string title, IEnumerable<BypassCatalog.Group> groups, HashSet<string> chosen, string? note)
    {
        var items = new WrapPanel { ItemWidth = 250 };
        var checks = new List<CheckBox>();
        foreach (var g in groups)
        {
            var cb = Ui.SetCheck(g.Title, g.Domains.Count, g.Note, false, chosen.Contains(g.Id));
            cb.Tag = g.Id;
            cb.Checked += (_, _) => UpdateRoutes();
            cb.Unchecked += (_, _) => UpdateRoutes();
            _groupChecks.Add(cb);
            _groupOf[cb] = g;
            checks.Add(cb);
            items.Children.Add(cb);
        }
        var header = new StackPanel();
        header.Children.Add(Ui.CategoryHeader(title, checks, list.Children.Count == 0));
        if (note != null) header.Children.Add(Ui.Hint(note, 2).With(t => { t.FontSize = 11.5; t.Margin = new Thickness(0, 2, 12, 2); }));
        list.Children.Add(header);
        list.Children.Add(items);
        _sections.Add((header, items, checks));
    }

    /// <summary>Поиск по названию набора и его доменам: «canva» находит «Figma, Canva, Framer…».</summary>
    private void ApplySearch()
    {
        var q = _search.Text.Trim().ToLowerInvariant();
        int found = 0;
        foreach (var (header, items, checks) in _sections)
        {
            int inSection = 0;
            foreach (var cb in checks)
            {
                var g = _groupOf[cb];
                bool hit = q.Length == 0 || g.Title.ToLowerInvariant().Contains(q) || g.Note.ToLowerInvariant().Contains(q)
                           || g.Domains.Any(d => d.Contains(q, StringComparison.OrdinalIgnoreCase));
                cb.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                if (hit) inSection++;
            }
            header.Visibility = items.Visibility = inSection > 0 ? Visibility.Visible : Visibility.Collapsed;
            found += inSection;
        }
        _searchState.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _searchState.Text = found == 0 ? $"«{_search.Text.Trim()}» нет в списке. Можно добавить свой домен справа, в «Свои домены»"
                                       : $"Найдено наборов: {found}. Отметьте нужный и нажмите «Сохранить»";
        _searchState.SetResourceReference(TextBlock.ForegroundProperty, found == 0 ? "Warn" : "Muted");
    }

    /// <summary>Правая колонка: обычный DNS для остального интернета, свои домены и наборы для разработчиков.</summary>
    private FrameworkElement GeneralSide()
    {
        var side = new StackPanel();
        side.Children.Add(Ui.Section("Остальной интернет"));
        var page = new StackPanel();
        page.Children.Add(Ui.Hint("Через этот DNS идут все сайты, кроме отмеченных слева. Лучше тот, что отдает российским сайтам настоящие адреса", 0)
                            .With(t => { t.FontSize = 12; t.Margin = new Thickness(0, 0, 0, 8); }));
        page.Children.Add(_presets);
        _checkBtn.Margin = new Thickness(0, 0, 8, 0);
        _pickRecBtn.Click += (_, _) => { _selected = _recommended; RenderPresets(); UpdateRoutes(); };
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(_checkBtn);
        actions.Children.Add(_pickRecBtn);
        page.Children.Add(actions);
        _checkState.Margin = new Thickness(2, 8, 0, 0);
        page.Children.Add(_checkState);
        side.Children.Add(Ui.Card(page).With(c => c.Padding = new Thickness(10, 12, 10, 12)));

        var own = new StackPanel();
        own.Children.Add(Ui.CardTitle("Свои домены через Smart DNS"));
        own.Children.Add(Ui.Hint("Если нужного сервиса нет в списке. По одному в строке, поддомены включаются", 4).With(t => t.FontSize = 12));
        _extraBox.Margin = new Thickness(0, 10, 0, 0);
        _extraBox.TextChanged += (_, _) => UpdateRoutes();
        own.Children.Add(_extraBox);
        side.Children.Add(Ui.Card(own));
        side.Children.Add(Ui.Card(DevCard(), bottom: 0));
        side.Children.Add(Ui.Hint("Instagram, X и YouTube провайдер режет не через DNS: для них в CHIMERION есть zapret («Telegram и YouTube»)", 10)
                            .With(t => { t.FontSize = 11; t.Margin = new Thickness(2, 10, 0, 0); }));
        return side;
    }

    /// <summary>
    /// «Что открывает сервер»: наборы проверяются на подключенном Smart DNS, у каждой галочки появляется пометка:
    /// открывает, не подменяет или не ответил. Без подключения проверять не на чем.
    /// </summary>
    private FrameworkElement SupportBar(BypassCatalog.Data catalog)
    {
        var state = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var btn = Ui.Action("Что открывает сервер", "Ghost", () => { });
        btn.Click += async (_, _) =>
        {
            if (DnsSwitcher.GetActive() is not { } active || active.Addresses.Count == 0)
            {
                state.Text = "Сначала подключитесь: наборы проверяются на подключенном Smart DNS";
                return;
            }
            var server = new DnsServer
            {
                Name = active.Name, Primary = active.Addresses[0],
                Secondary = active.Addresses.Count > 1 && active.BackupAddresses.Count == 0 ? active.Addresses[1] : null, DohUrl = active.ReadDohTemplate(),
            };
            btn.IsEnabled = false;
            state.Text = $"Проверяю наборы на «{DnsServer.Clip(active.Name)}»…";
            try
            {
                var res = await CatalogCheck.RunAsync(new ServerChecker(_settings), server, _settings.Targets, catalog.Groups, CancellationToken.None);
                foreach (var cb in _groupChecks)
                {
                    if (cb.Content is not TextBlock tb || !res.TryGetValue((string)cb.Tag, out var sup)) continue;
                    foreach (var old in tb.Inlines.OfType<System.Windows.Documents.Run>().Where(r => r.Tag as string == "support").ToList()) tb.Inlines.Remove(old);
                    tb.Inlines.Add(new System.Windows.Documents.Run(" · " + CatalogCheck.Describe(sup))
                    {
                        Tag = "support", FontSize = 11,
                        Foreground = Ui.B(sup switch { CatalogCheck.Support.Proxied => "Ok", CatalogCheck.Support.Different => "Warn", _ => "Muted" }),
                    });
                }
                int open = res.Count(r => r.Value == CatalogCheck.Support.Proxied);
                state.Text = $"«{DnsServer.Clip(active.Name)}» открывает наборов: {open} из {res.Count}. Включайте те, что открывает: остальные через этот сервер не заработают";
            }
            catch (Exception ex) { state.Text = "Проверка не удалась: " + ex.Message; }
            finally { btn.IsEnabled = true; }
        };
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(btn, Dock.Left);
        row.Children.Add(btn);
        row.Children.Add(state);
        return row;
    }

    /// <summary>
    /// «Для разработчиков»: одной кнопкой наборы для Cursor, Copilot, Windsurf, JetBrains AI и других LLM, плюс
    /// проверка WSL, где без DNS-туннеля Claude Code и pip внутри Linux идут мимо Smart DNS.
    /// </summary>
    private FrameworkElement DevCard()
    {
        var card = new StackPanel();
        card.Children.Add(Ui.CardTitle("Для разработчиков"));
        card.Children.Add(Ui.Hint("Cursor, Copilot, Windsurf, JetBrains AI, Cline и другие LLM. Claude Code работает и без набора", 4).With(t => t.FontSize = 11));
        var pick = Ui.Action("Включить наборы для разработчиков", "Strong", () =>
        {
            foreach (var cb in _groupChecks.Where(c => DevTools.Groups.Contains((string)c.Tag))) cb.IsChecked = true;
        });
        pick.HorizontalAlignment = HorizontalAlignment.Left;
        pick.Margin = new Thickness(0, 10, 0, 0);
        card.Children.Add(pick);

        var wsl = DevTools.Check();
        if (wsl.State != DevTools.WslState.None)
        {
            bool bad = wsl.State != DevTools.WslState.Ok;
            card.Children.Add(Ui.Separator());
            card.Children.Add(new TextBlock { Text = bad ? "WSL: запросы идут мимо Smart DNS" : "WSL: все в порядке", Foreground = Ui.B(bad ? "Warn" : "Ok"), Margin = new Thickness(0, 0, 0, 4) });
            card.Children.Add(Ui.Hint(DevTools.Describe(wsl), 0).With(t => t.FontSize = 11));
            if (bad)
            {
                var fix = Ui.Action("Включить DNS-туннель", "Ghost", () => DiagnosticsWindow.EnableWslTunnel(_ => { }));
                fix.HorizontalAlignment = HorizontalAlignment.Left;
                fix.Margin = new Thickness(-10, 6, 0, 0);
                card.Children.Add(fix);
            }
        }
        return card;
    }

    public bool ShowDialog() { _w.ShowDialog(); return Saved; }

    private void RenderPresets()
    {
        _pickRecBtn.Visibility = _selected == _recommended ? Visibility.Collapsed : Visibility.Visible;
        _pickRecBtn.Content = $"Выбрать рекомендуемый: {_recommended}";
        _presets.Children.Clear();
        foreach (var p in SplitDns.Presets) _presets.Children.Add(Card(p.Name, p.Addresses.Length == 0 ? "адаптер не меняется" : string.Join(", ", p.Addresses) + (p.Doh != null ? " · DoH" : ""), p.Note, null));
        // Поля «Свой DNS» живут все время окна: при перерисовке отцепляем их от прежней карточки, иначе WPF бросает
        // «элемент уже является логическим дочерним для другого элемента» (так и было при щелчке по карточке)
        if (_customPanel == null)
        {
            _customPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var ips = new StackPanel { Orientation = Orientation.Horizontal };
            ips.Children.Add(_ip1);
            ips.Children.Add(_ip2);
            _customPanel.Children.Add(ips);
            _customPanel.Children.Add(_doh);
        }
        if (_customPanel.Parent is Panel old) old.Children.Remove(_customPanel);
        _presets.Children.Add(Card(Custom, "свои адреса", "Любой DNS по IP, при желании с DoH", _customPanel));
    }

    private Border Card(string name, string addrs, string note, FrameworkElement? extra)
    {
        bool sel = name == _selected;
        // Кружок выбора, как у переключателя: обводка, у выбранного точка цвета акцента
        var dot = new Grid { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        dot.Children.Add(new System.Windows.Shapes.Ellipse { StrokeThickness = 1.5 }.Ref(System.Windows.Shapes.Shape.StrokeProperty, sel ? "Accent" : "Muted"));
        if (sel) dot.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8 }.Ref(System.Windows.Shapes.Shape.FillProperty, "Accent"));

        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        if (name == _recommended) head.Children.Add(Ui.Badge());
        body.Children.Add(head);
        body.Children.Add(Ui.Hint(addrs + " · " + note, 2).With(t => t.FontSize = 11));
        if (_checks.TryGetValue(name, out var c))
        {
            var good = c.ForeignOk * 2 >= c.ForeignTotal && c.RussianOk == c.RussianTotal;
            body.Children.Add(Ui.Hint($"Зарубежное: {c.ForeignOk} из {c.ForeignTotal} · российские настоящие: {c.RussianOk} из {c.RussianTotal}. {c.Detail}",
                4, good ? "Ok" : c.RussianOk < c.RussianTotal ? "Bad" : "Warn").With(t => t.FontSize = 11));
        }
        if (extra != null && sel) body.Children.Add(extra);

        var row = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        row.Children.Add(body);
        var b = new Border
        {
            Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
            Background = sel ? Ui.B("Surface2") : Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.MouseEnter += (_, _) => { if (name != _selected) b.Background = Ui.B("SurfaceHover"); };
        b.MouseLeave += (_, _) => { if (name != _selected) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) != null) return; // щелчок в поле ввода
            if (_selected == name) return;
            _selected = name;
            RenderPresets();
            UpdateRoutes();
        };
        return b;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x)) if (x is T t) return t;
        return null;
    }

    private async Task CheckAllAsync()
    {
        if (_checking) return;
        _checking = true;
        _checkBtn.IsEnabled = false;
        _checkState.Text = "Проверяю заблокированные зарубежные и российские сайты через каждый DNS. Это до 30 секунд…";
        if (_bar.Parent == null) ((Panel)_checkState.Parent).Children.Insert(((Panel)_checkState.Parent).Children.IndexOf(_checkState), _bar);
        _bar.Visibility = Visibility.Visible;
        var list = SplitDns.Presets.ToList();
        if (CustomPreset() is { } cp) list.Add(cp);
        try
        {
            var results = await Task.WhenAll(list.Select(p => SplitDns.CheckAsync(p, _settings, CancellationToken.None)));
            foreach (var r in results) _checks[r.Name] = r;
            var best = results.Where(r => r.RussianOk == r.RussianTotal).OrderByDescending(r => r.ForeignOk).FirstOrDefault();
            // Рекомендация меняется, только если какой-то DNS открывает заметно больше, чем «Не менять DNS системы»
            var keep = results.FirstOrDefault(r => r.Name == SplitDns.Presets[0].Name);
            if (best != null && (keep == null || best.ForeignOk > keep.ForeignOk || keep.RussianOk < keep.RussianTotal)) _recommended = best.Name;
            _checkState.Text = best == null ? "Ни один DNS не прошел проверку российских сайтов полностью"
                : $"Лучший по проверке: {best.Name}, зарубежное {best.ForeignOk} из {best.ForeignTotal}. Метка «рекомендуем» стоит на нем";
        }
        catch (Exception e) { _checkState.Text = "Проверка не завершилась: " + e.Message; }
        _bar.Visibility = Visibility.Collapsed;
        _checking = false;
        _checkBtn.IsEnabled = true;
        RenderPresets();
    }

    /// <summary>Свой DNS из полей ввода. Null, если адреса не заданы или неверны.</summary>
    private GeneralDnsPreset? CustomPreset()
    {
        var ips = new[] { _ip1.Text, _ip2.Text }.Select(t => t.Trim()).Where(t => IPAddress.TryParse(t, out _)).ToArray();
        if (ips.Length == 0) return null;
        var doh = _doh.Text.Trim();
        return new GeneralDnsPreset(Custom, ips, SourceParser.IsValidDoh(doh, out _) ? doh : null, "свой");
    }

    private void Save()
    {
        if (_selected == Custom)
        {
            if (new[] { _ip1.Text, _ip2.Text }.Any(t => t.Trim().Length > 0 && !IPAddress.TryParse(t.Trim(), out _)))
            { Error("Свой DNS: адрес должен быть IP, например 1.1.1.1"); return; }
            if (_doh.Text.Trim().Length > 0 && !SourceParser.IsValidDoh(_doh.Text.Trim(), out _))
            { Error("Свой DNS: DoH-адрес должен начинаться с https://"); return; }
            if (CustomPreset() is not { } cp) { Error("Свой DNS: укажите хотя бы один IP"); return; }
            _edit.GeneralName = "Свой DNS (" + string.Join(", ", cp.Addresses) + ")";
            _edit.GeneralAddresses = cp.Addresses.ToList();
            _edit.GeneralDoh = cp.Doh;
        }
        else
        {
            var p = SplitDns.Presets.First(x => x.Name == _selected);
            _edit.GeneralName = p.Name;
            _edit.GeneralAddresses = p.Addresses.ToList();
            _edit.GeneralDoh = p.Doh;
        }
        var lines = _extraBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = lines.Where(l => SplitDns.Clean(l).Length == 0).ToList();
        if (bad.Count > 0) { Error("Не похоже на домен: " + string.Join(", ", bad.Take(3))); return; }
        _edit.ExtraAiDomains = lines.Select(SplitDns.Clean).Distinct().ToList();
        _edit.Enabled = _enabled.IsChecked == true;
        if (_edit.Enabled && !_settings.SplitDns.Enabled && SplitDns.PolicyWarning(SplitDns.PolicyRuleCount()) is { } gpo &&
            !DnexDialog.Ask("«Два DNS» здесь не сработают", gpo, "Все равно включить", windowTitle: "Два DNS"))
            return;
        _edit.Groups = _groupChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        _settings.SplitDns = _edit;
        try { Storage.Save("settings.json", _settings); }
        catch (Exception e) { Error("Не удалось сохранить: " + e.Message); return; }
        Saved = true;
        _w.Close();
    }

    private void Error(string text) { _state.Foreground = (Brush)Res["Bad"]; _state.Text = text; }
}
