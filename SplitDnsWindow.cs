using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Два DNS (эксперимент)»: общий DNS для всего интернета и Smart DNS только для доменов ИИ.
/// Общий DNS выбирается из готовых или задается свой; кнопка «Проверить все» показывает для каждого, открываются ли
/// заблокированные зарубежные сайты и настоящие ли адреса у российских. Сохранение пишет settings.json;
/// если подключение активно, главное окно переподключается, чтобы режим заработал сразу.
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
    private readonly TextBox _extra = new() { AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox _enabled = new() { Content = "Включить два DNS" };
    private readonly TextBlock _state = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
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
    private TextBox? _extraBox;
    private readonly List<CheckBox> _groupChecks = new();
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
        _extra.Text = string.Join(Environment.NewLine, _edit.ExtraAiDomains);
        _enabled.IsChecked = _edit.Enabled;

        _w = DnexDialog.CreateWindow("Два DNS", Ui.Wide);
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; _w.ShowInTaskbar = false; }

        var root = Ui.Root();
        root.Margin = new Thickness(24, 22, 24, 4);
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Два DNS",
            "ChatGPT, Claude и Gemini идут через Smart DNS, а все остальные сайты через общий DNS. Друг другу они не мешают", glyph: "⇄"));

        _enabled.Content = null;
        _enabled.Margin = new Thickness(0);
        root.Children.Add(Ui.Card(Ui.Row("Включить два DNS",
            "Рекомендуем включить. Заработает при подключении к серверу или сразу, если подключение уже есть", _enabled), bottom: 0));
        root.Children.Add(Ui.Steps("Не знаете, что выбрать: включите, оставьте общий DNS с меткой «рекомендуем» и нажмите «Сохранить»"));

        // Две вкладки вместо длинной простыни. Обе страницы лежат в одной ячейке, неактивная скрыта (Hidden, а не
        // Collapsed): высота окна по большей из них и не прыгает при переключении
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        var tabBox = new Border
        {
            Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Background = Ui.B("Surface2"), BorderBrush = Ui.B("Border"),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 12), Child = tabs,
        };
        _checkBtn.Click += (_, _) => _ = CheckAllAsync();
        var pageGeneral = GeneralPage();
        var pageSmart = SmartPage(settings);
        void Tab(string text, FrameworkElement page, bool on)
        {
            var rb = new RadioButton { Content = text, GroupName = "split", IsChecked = on, Style = (Style)Res["Tab"] };
            rb.Checked += (_, _) => { pageGeneral.Visibility = page == pageGeneral ? Visibility.Visible : Visibility.Hidden; pageSmart.Visibility = page == pageSmart ? Visibility.Visible : Visibility.Hidden; };
            tabs.Children.Add(rb);
        }
        Tab("Общий DNS", pageGeneral, true);
        Tab("ИИ и наборы", pageSmart, false);
        pageSmart.Visibility = Visibility.Hidden;
        root.Children.Add(tabBox);
        var pages = new Grid();
        pages.Children.Add(pageGeneral);
        pages.Children.Add(pageSmart);
        root.Children.Add(pages);

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
        scroll.MaxHeight = SystemParameters.WorkArea.Height - 160;
        dock.Children.Add(scroll);
        _w.Content = dock;
        RenderPresets();
    }

    /// <summary>Вкладка «Общий DNS»: компактный список вариантов с кружком выбора и проверка.</summary>
    private FrameworkElement GeneralPage()
    {
        var page = new StackPanel();
        page.Children.Add(Ui.Hint("Через него идут все сайты, кроме ИИ. Лучше тот, что отдает российским сайтам настоящие адреса", 0)
                            .With(t => t.Margin = new Thickness(2, 0, 0, 10)));
        page.Children.Add(Ui.Card(_presets, bottom: 0).With(b => b.Padding = new Thickness(6)));
        _checkBtn.Margin = new Thickness(0, 0, 8, 0);
        _pickRecBtn.Click += (_, _) => { _selected = _recommended; RenderPresets(); };
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(_checkBtn);
        actions.Children.Add(_pickRecBtn);
        page.Children.Add(actions);
        _checkState.Margin = new Thickness(2, 8, 0, 0);
        page.Children.Add(_checkState);
        page.Children.Add(Ui.Hint("Instagram, X и YouTube провайдер режет не через DNS: для них в CHIMERION есть zapret («Telegram и YouTube»)", 8)
                            .With(t => { t.FontSize = 11; t.Margin = new Thickness(2, 8, 0, 0); }));
        return page;
    }

    /// <summary>Вкладка «ИИ и наборы»: какие домены идут в Smart DNS, свои домены и готовые наборы сервисов.</summary>
    private FrameworkElement SmartPage(AppSettings settings)
    {
        var page = new StackPanel();
        // Только домены ИИ: без своих доменов и наборов, они показаны ниже отдельно
        var aiOnly = new AppSettings();
        var ns = SplitDns.Namespaces(aiOnly).Where(n => !n.StartsWith('.')).ToList();

        // Домены ИИ: одной строкой, полный список по кнопке
        var ai = new StackPanel();
        ai.Children.Add(Ui.CardTitle("Домены ИИ", $"{ns.Count} доменов", "Muted"));
        ai.Children.Add(Ui.Hint("ChatGPT, Claude и Gemini всегда идут через Smart DNS. Поиск Google, почта и YouTube остаются на общем DNS", 4)
                          .With(t => t.FontSize = 11));
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
        ai.Children.Add(Ui.Separator());
        ai.Children.Add(new TextBlock { Text = "Свои домены для Smart DNS", Margin = new Thickness(0, 0, 0, 6) });
        _extraBox = Ui.MultiLine(_extra.Text, 56);
        ai.Children.Add(_extraBox);
        ai.Children.Add(Ui.Hint("По одному в строке, поддомены включаются", 6).With(t => t.FontSize = 11));
        page.Children.Add(Ui.Card(ai));

        // Готовые наборы через Smart DNS: для сервисов, которые сами закрылись для России
        var sets = new StackPanel();
        sets.Children.Add(Ui.CardTitle("Наборы сервисов"));
        sets.Children.Add(Ui.Hint("Для сервисов, которые сами закрылись для России: Figma, Adobe, Unity, Docker, JetBrains, Notion. " +
                                  "Работает, если ваш Smart DNS их поддерживает", 4).With(t => t.FontSize = 11));
        var catalog = BypassCatalog.LoadCached();
        var chosen = BypassCatalog.Normalize(_edit.Groups);
        var list = new StackPanel();
        if (catalog != null)
        {
            var recIds = catalog.Groups.Where(g => g.Recommend == "smartdns").Select(g => g.Id).ToHashSet();
            sets.Children.Add(Ui.ChoiceBar(_groupChecks, c => recIds.Contains((string)c.Tag)).With(w => w.Margin = new Thickness(0, 10, 0, 10)));
            // Сначала разделы с рекомендуемыми для Smart DNS наборами
            foreach (var cat in catalog.Groups.GroupBy(g => g.Category.Length > 0 ? g.Category : "Другое")
                                              .OrderByDescending(c => c.Any(g => g.Recommend == "smartdns")))
            {
                var wrap = new WrapPanel { ItemWidth = 245 };
                var inCat = new List<CheckBox>();
                foreach (var g in cat)
                {
                    var cb = Ui.SetCheck(g.Title, g.Domains.Count, g.Note, g.Recommend == "smartdns", chosen.Contains(g.Id));
                    cb.Tag = g.Id;
                    _groupChecks.Add(cb);
                    inCat.Add(cb);
                    wrap.Children.Add(cb);
                }
                list.Children.Add(Ui.CategoryHeader(cat.Key, inCat, list.Children.Count == 0));
                list.Children.Add(wrap);
            }
        }
        sets.Children.Add(Ui.Scroll(list, 230));
        page.Children.Add(Ui.Card(sets, bottom: 0));
        return page;
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
        var lines = (_extraBox ?? _extra).Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
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

    private static TextBlock Section(string text) => new()
    {
        Text = text, Foreground = (Brush)Res["Muted"], FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6),
    };

    private static TextBlock Muted(string text, double left, double top) => new()
    {
        Text = text, Foreground = (Brush)Res["Muted"], FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, top, 0, 0),
    };
}
