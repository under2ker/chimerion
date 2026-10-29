using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Стартовая панель: только данные уже выполненных проверок и явные действия пользователя.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private TextBlock _dashboardTitle = null!, _dashboardDescription = null!, _dashboardChecked = null!, _dashboardMessage = null!, _dashboardHeroMode = null!;
    private TextBlock _dashboardSiteYoutube = null!, _dashboardSiteDiscord = null!, _dashboardSiteTelegram = null!;
    private StackPanel _dashboardEvents = null!;
    private string? _dashboardEventsSignature;
    private Border _dashboardHero = null!, _dashboardHeroIcon = null!;
    private Button _dashboardProbe = null!, _dashboardSiteProbe = null!;
    private bool _dashboardToolBusy, _dashboardChecking, _dashboardSitesChecking;
    private string? _dashboardMessageText;
    private bool _dashboardMessageError;
    private Guid? _dashboardPreviousServer;
    private DateTime _dashboardConnectionChanged = DateTime.MinValue;
    private bool _dashboardSeenConnection;
    private readonly Dictionary<string, (SiteWatch.Verdict Verdict, DateTime Time)> _dashboardSiteResults = new(StringComparer.OrdinalIgnoreCase);

    // ---------- Главная: запустил, включил, пользуешься ----------
    private Controls.Switch _dashboardPower = null!;
    private TextBlock _dashboardPowerText = null!, _dashboardSmartName = null!, _dashboardSmartChip = null!, _dashboardPlainDns = null!, _dashboardPlainHint = null!;
    private TextBlock _dashboardUptime = null!, _dashboardUptimeHint = null!;
    private Image _dashboardSmartFlag = null!;
    private Border _dashboardSmartChipBox = null!;
    private Controls.Icon _dashboardHeroMark = null!;
    private readonly Dictionary<string, (Ellipse Dot, TextBlock Status, TextBlock Note)> _dashboardServiceRows = new();
    private readonly List<(Controls.Switch Toggle, TextBlock Status, Ellipse Dot, Func<Tool> Tool)> _dashboardToolRows = new();
    private StackPanel _dashboardServers = null!;
    private string? _dashboardServersSignature;
    private bool _dashboardSyncing; // обновление переключателей из кода не должно запускать действия

    private void SetupDashboard()
    {
        var page = new StackPanel { Margin = new Thickness(24, 20, 24, 24), MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch };

        // ----- Карточка защиты: знак, состояние, большой переключатель и три факта -----
        var heroTop = new DockPanel();
        var power = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        _dashboardPower = new Controls.Switch { Width = 64, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Включить или выключить Smart DNS" };
        System.Windows.Automation.AutomationProperties.SetName(_dashboardPower, "Защита");
        _dashboardPower.Click += (_, _) => { if (!_dashboardSyncing) _ = DashboardConnectAsync(); };
        _dashboardPowerText = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 104 };
        power.Children.Add(_dashboardPower); power.Children.Add(_dashboardPowerText);
        DockPanel.SetDock(power, Dock.Right); heroTop.Children.Add(power);
        _dashboardHeroMark = new Controls.Icon { Kind = "circle-check", Size = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _dashboardHeroIcon = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Margin = new Thickness(0, 0, 18, 0), Child = _dashboardHeroMark, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_dashboardHeroIcon, Dock.Left); heroTop.Children.Add(_dashboardHeroIcon);
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _dashboardTitle = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        _dashboardDescription = new TextBlock { FontSize = 14, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        _dashboardHeroMode = Ui.Hint("", 3);
        heroText.Children.Add(_dashboardTitle); heroText.Children.Add(_dashboardDescription); heroText.Children.Add(_dashboardHeroMode);
        heroTop.Children.Add(heroText);

        var heroFacts = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < 3; i++) heroFacts.ColumnDefinitions.Add(new ColumnDefinition());
        // Smart DNS для нейросетей: имя сервера, флаг и плашка состояния
        var smartValue = new StackPanel { Orientation = Orientation.Horizontal };
        _dashboardSmartName = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210 };
        _dashboardSmartFlag = new Image { Width = 18, Height = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform };
        _dashboardSmartChip = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        _dashboardSmartChipBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = _dashboardSmartChip };
        smartValue.Children.Add(_dashboardSmartName); smartValue.Children.Add(_dashboardSmartFlag); smartValue.Children.Add(_dashboardSmartChipBox);
        DashboardFact(heroFacts, 0, "globe", "Smart DNS для нейросетей", smartValue, null);
        _dashboardPlainDns = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _dashboardPlainHint = Ui.Hint("", 2);
        DashboardFact(heroFacts, 1, "arrow-left-right", "Обычный DNS", _dashboardPlainDns, _dashboardPlainHint);
        _dashboardUptime = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        _dashboardUptimeHint = Ui.Hint("", 2);
        DashboardFact(heroFacts, 2, "activity", "Время работы", _dashboardUptime, _dashboardUptimeHint);

        var heroContent = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        heroContent.Children.Add(heroTop); heroContent.Children.Add(heroFacts);
        var heroLayer = new Grid();
        heroLayer.SizeChanged += (_, e) => heroLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 11, 11);
        heroLayer.Children.Add(new DashboardAtmosphere());
        heroLayer.Children.Add(heroContent);
        _dashboardHero = Ui.Card(heroLayer, bottom: 0); _dashboardHero.Padding = new Thickness(0); page.Children.Add(_dashboardHero);
        _dashboardMessage = Ui.Hint("", 8); _dashboardMessage.Visibility = Visibility.Collapsed; page.Children.Add(_dashboardMessage);

        // ----- Две колонки: нейросети и дополнительные сервисы -----
        var services = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        services.ColumnDefinitions.Add(new ColumnDefinition()); services.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardProbe = DashboardHeaderButton("Проверить все", () => _ = DashboardProbeAsync());
        var ai = new StackPanel();
        _dashboardChecked = Ui.Hint("", 0);
        ai.Children.Add(DashboardPanelHeader("ИИ-сервисы", _dashboardChecked, _dashboardProbe));
        foreach (var (name, logo, url, domain) in new[] { ("ChatGPT", "LogoOpenAI", "https://chatgpt.com/", "chatgpt.com"), ("Claude", "LogoClaude", "https://claude.ai/", "claude.ai"), ("Gemini", "LogoGemini", "https://gemini.google.com/", "gemini.google.com") })
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var note = Ui.Hint("", 1).With(t => { t.FontSize = 11; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; });
            var open = Ui.Action("Открыть", "Ghost", () => AutoStart.OpenUrl(url)); open.Padding = new Thickness(12, 5, 12, 6); open.Margin = new Thickness(10, 0, 0, 0);
            ai.Children.Add(DashboardRow(DashboardLogo(logo), name, domain, dot, status, note, open));
            _dashboardServiceRows[name] = (dot, status, note);
        }
        var aiCard = Ui.Card(ai, bottom: 0); aiCard.Padding = new Thickness(14, 12, 14, 8); aiCard.Margin = new Thickness(0, 0, 6, 0);
        services.Children.Add(aiCard);

        _dashboardSiteProbe = DashboardHeaderButton("Проверить сайты", () => _ = DashboardSitesAsync());
        var extra = new StackPanel();
        extra.Children.Add(DashboardPanelHeader("Дополнительные сервисы", null, _dashboardSiteProbe));
        _dashboardSiteYoutube = Ui.Hint("", 1); _dashboardSiteDiscord = Ui.Hint("", 1); _dashboardSiteTelegram = Ui.Hint("", 1);
        foreach (var (name, kind, sub, tool, site) in new (string, string, string, Func<Tool>, TextBlock)[]
                 {
                     ("YouTube", "zap", "Обход замедления через zapret", () => _tools.OfType<ZapretTool>().First(), _dashboardSiteYoutube),
                     ("Discord", "zap", "Обход замедления через zapret", () => _tools.OfType<ZapretTool>().First(), _dashboardSiteDiscord),
                     ("Telegram", "send", "Ускорение через TG WS Proxy", () => _tools.OfType<TgProxyTool>().First(), _dashboardSiteTelegram),
                 })
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Controls.Switch { Width = 44, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = name + ": включить или выключить" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, name);
            var getTool = tool;
            toggle.Click += (_, _) => { if (!_dashboardSyncing) _ = DashboardToolAsync(getTool()); };
            var symbol = DashboardIcon(kind, "Surface2", "Accent", 38);
            extra.Children.Add(DashboardRow(symbol, name, sub, dot, status, site, toggle));
            _dashboardToolRows.Add((toggle, status, dot, tool));
        }
        var extraCard = Ui.Card(extra, bottom: 0); extraCard.Padding = new Thickness(14, 12, 14, 8); extraCard.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(extraCard, 1); services.Children.Add(extraCard);
        page.Children.Add(services);

        // ----- Лучшие серверы: три самых надежных, щелчок подключает -----
        var serversBox = new StackPanel();
        serversBox.Children.Add(DashboardPanelHeader("Лучшие серверы", Ui.Hint("Щелчок по строке подключает сервер", 0),
            DashboardHeaderButton("Найти новые серверы", () => { ShowControlPanel(); Fetch_Click(this, new RoutedEventArgs()); })));
        _dashboardServers = new StackPanel(); serversBox.Children.Add(_dashboardServers);
        var serversCard = Ui.Card(serversBox, bottom: 0); serversCard.Padding = new Thickness(14, 12, 14, 10); serversCard.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(serversCard);

        // ----- Журнал: последние строки как в журнале программы -----
        var logBox = new StackPanel();
        logBox.Children.Add(DashboardPanelHeader("Журнал", null, DashboardHeaderButton("Открыть журнал", () => { ShowControlPanel(); LogToggle.IsChecked = true; })));
        _dashboardEvents = new StackPanel { Margin = new Thickness(2, 2, 2, 2) }; logBox.Children.Add(_dashboardEvents);
        var logCard = Ui.Card(logBox, bottom: 0); logCard.Padding = new Thickness(14, 12, 14, 12); logCard.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(logCard);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
        var layout = new Grid();
        var sidebarColumn = new ColumnDefinition { Width = new GridLength(208) };
        layout.ColumnDefinitions.Add(sidebarColumn); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = DashboardSidebar(); Grid.SetColumn(scroll, 1); layout.Children.Add(sidebar); layout.Children.Add(scroll);
        int lastLayout = -1;
        layout.SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width >= 900;
            sidebarColumn.Width = new GridLength(wide ? 208 : 0);
            sidebar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            double available = e.NewSize.Width - (wide ? 208 : 0);
            int mode = available < 640 ? 0 : available < 960 ? 1 : 2;
            if (mode == lastLayout) return;
            lastLayout = mode;
            page.Margin = mode == 0 ? new Thickness(12, 14, 12, 16) : new Thickness(24, 20, 24, 24);
            DashboardReflow(heroFacts, mode == 0 ? 1 : 3);
            DashboardReflow(services, mode < 2 ? 1 : 2);
            _dashboardHeroIcon.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        DashboardHost.Content = layout;
        Loaded += (_, _) => RefreshDashboard();
        _dashboardTimer.Tick += (_, _) => RefreshDashboard(); _dashboardTimer.Start();
    }

    private static Button DashboardHeaderButton(string title, Action click)
    {
        var b = Ui.Action(title, "Strong", click);
        b.Padding = new Thickness(12, 5, 12, 6); b.Margin = new Thickness(0); b.FontSize = 13; b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    private static DockPanel DashboardPanelHeader(string title, TextBlock? hint, Button? action)
    {
        var d = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        if (action != null) { DockPanel.SetDock(action, Dock.Right); d.Children.Add(action); }
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (hint != null) { hint.FontSize = 11; t.Children.Add(hint); }
        d.Children.Add(t);
        return d;
    }

    private static Border DashboardLogo(string resource)
    {
        var image = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        image.SetResourceReference(Image.SourceProperty, resource);
        return new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Child = image }.Ref(Border.BackgroundProperty, "Surface2");
    }

    /// <summary>Строка сервиса: значок, название и подпись, состояние с цветной точкой и пояснением, действие справа.</summary>
    private static Border DashboardRow(FrameworkElement symbol, string name, string sub, Ellipse dot, TextBlock status, TextBlock note, FrameworkElement action)
    {
        var g = new Grid { MinHeight = 52 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        symbol.Margin = new Thickness(0, 0, 12, 0); symbol.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(symbol);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Ui.Hint(sub, 1).With(t => { t.FontSize = 12; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal }; line.Children.Add(dot); line.Children.Add(status);
        state.Children.Add(line); state.Children.Add(note);
        Grid.SetColumn(state, 2); g.Children.Add(state);
        action.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(action, 3); g.Children.Add(action);
        return new Border { Child = g, Padding = new Thickness(4, 4, 4, 4), BorderThickness = new Thickness(0, 1, 0, 0) }.Ref(Border.BorderBrushProperty, "Border");
    }

    /// <summary>Фирменный узор сайта: мягкое свечение и тонкие диагональные полосы, без влияния на ввод.</summary>
    private sealed class DashboardAtmosphere : FrameworkElement
    {
        private static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
            "AccentBrush", typeof(Brush), typeof(DashboardAtmosphere), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            "LineBrush", typeof(Brush), typeof(DashboardAtmosphere), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public DashboardAtmosphere()
        {
            IsHitTestVisible = false;
            SetResourceReference(AccentBrushProperty, "Accent");
            SetResourceReference(LineBrushProperty, "Text");
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var accent = (GetValue(AccentBrushProperty) as SolidColorBrush)?.Color ?? Colors.Transparent;
            var line = GetValue(LineBrushProperty) as Brush;
            var glow = new RadialGradientBrush
            {
                Center = new Point(.82, .3), GradientOrigin = new Point(.82, .3), RadiusX = .55, RadiusY = 1.1,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(24, accent.R, accent.G, accent.B), 0),
                    new(Color.FromArgb(8, accent.R, accent.G, accent.B), .45),
                    new(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
                },
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (line == null) return;
            dc.PushOpacity(.045);
            var pen = new Pen(line, 1);
            for (double x = -ActualHeight; x < ActualWidth + ActualHeight; x += 28)
                dc.DrawLine(pen, new Point(x, ActualHeight), new Point(x + ActualHeight * .84, 0));
            dc.Pop();
        }
    }

    private FrameworkElement DashboardSidebar()
    {
        var content = new DockPanel { Margin = new Thickness(15, 24, 15, 18) };
        var update = CreateUpdateCard();
        DockPanel.SetDock(update, Dock.Bottom); content.Children.Add(update);
        var links = new StackPanel();
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 2, 0, 24) };
        logo.Children.Add(new Controls.BrandMark { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) });
        var brandText = new StackPanel();
        brandText.Children.Add(new TextBlock { Text = "CHIMERION", FontSize = 13, FontWeight = FontWeights.Bold });
        brandText.Children.Add(Ui.Hint("Панель сети", 1)); logo.Children.Add(brandText); links.Children.Add(logo);
        // Тот же порядок, что в меню панели управления: главная, серверы, обход, диагностика, настройки
        links.Children.Add(DashboardNav("Главная", "shield-check", () => { }, true));
        links.Children.Add(DashboardNav("Панель управления", "activity", ShowControlPanel));
        links.Children.Add(DashboardNav("Обход и Telegram", "zap", () => ToolsToggle_Click(this, new RoutedEventArgs())));
        links.Children.Add(DashboardNav("Диагностика", "stethoscope", () => DiagToggle_Click(this, new RoutedEventArgs())));
        links.Children.Add(DashboardNav("Настройки", "settings", () => Settings_Click(this, new RoutedEventArgs())));
        content.Children.Add(links);
        return new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = content }.Ref(Border.BackgroundProperty, "Surface").Ref(Border.BorderBrushProperty, "Border");
    }

    private static Button DashboardNav(string title, string icon, Action click, bool selected = false)
    {
        var button = Ui.Action(title, "Ghost", click);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Controls.Icon { Kind = icon, Size = 17, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        button.Content = row; button.Margin = new Thickness(0, 0, 0, 5); button.Padding = new Thickness(12, 10, 12, 10);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Template = DashboardNavTemplate();
        if (selected) { button.SetResourceReference(Control.BackgroundProperty, "AccentSoft"); button.SetResourceReference(Control.ForegroundProperty, "Accent"); }
        return button;
    }

    private static void DashboardReflow(Grid grid, int columns)
    {
        grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        int rows = (grid.Children.Count + columns - 1) / columns;
        for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < grid.Children.Count; i++)
        {
            var child = (FrameworkElement)grid.Children[i];
            Grid.SetRow(child, i / columns); Grid.SetColumn(child, i % columns);
            int span = i == grid.Children.Count - 1 ? columns - i % columns : 1;
            Grid.SetColumnSpan(child, span);
            child.Margin = new Thickness(i % columns == 0 ? 0 : 5, i < columns ? 0 : 10, i % columns + span == columns ? 0 : 5, 0);
        }
    }

    private static ControlTemplate DashboardNavTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "NavBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter); template.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SurfaceHover"), "NavBorder"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "NavBorder"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Accent"), "NavBorder"));
        template.Triggers.Add(focus); return template;
    }

    private static void DashboardFact(Grid grid, int index, string kind, string label, FrameworkElement value, TextBlock? hint)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Controls.Icon { Kind = kind, Size = 24, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Controls.Icon.ForegroundProperty, "Accent");
        row.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Hint(label, 0).With(t => t.FontSize = 12));
        value.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(value);
        if (hint != null) { hint.FontSize = 12; text.Children.Add(hint); }
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var card = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); Grid.SetColumn(card, index); grid.Children.Add(card);
    }

    private static Border DashboardIcon(string kind, string background, string foreground, double size)
    {
        var icon = new Controls.Icon { Kind = kind, Size = size * .48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Controls.Icon.ForegroundProperty, foreground);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10), Child = icon }.Ref(Border.BackgroundProperty, background);
    }

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} д {t.Hours} ч" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{Math.Max(1, t.Minutes)} мин";

    private void RefreshDashboard()
    {
        if (!IsVisible || DashboardHost.Visibility != Visibility.Visible) return;
        var active = DnsSwitcher.GetActive();
        if (_dashboardSeenConnection && _dashboardPreviousServer != active?.ServerId) _dashboardConnectionChanged = DateTime.Now;
        _dashboardSeenConnection = true; _dashboardPreviousServer = active?.ServerId;
        var snap = new[] { _traySnap, _after, _before }.Where(x => x != null).OrderByDescending(x => x!.Taken).FirstOrDefault();
        if (snap != null && (snap.Taken < _dashboardConnectionChanged || (active != null && snap.Taken < active.Since))) snap = null;
        var aiServices = snap?.Services.Where(x => x.Name is "ChatGPT" or "Claude" or "Gemini").ToList() ?? new();
        int open = aiServices.Count(x => x.Access.StartsWith("✓"));

        // Карточка защиты
        bool on = active != null;
        _dashboardTitle.Text = _busyConn ? "Подключение меняется" : on ? "Защита активна" : "Защита выключена";
        _dashboardDescription.Text = !on ? "Включите, чтобы открыть ChatGPT, Claude и Gemini"
            : aiServices.Count == 0 ? "Проверяю доступ к нейросетям…"
            : open == aiServices.Count ? "Доступ к ИИ-сервисам настроен" : $"Открываются {open} из {aiServices.Count} нейросетей";
        _dashboardHeroMode.Text = !on ? "Остальной интернет работает как обычно" : active!.Split ? "Нейросети через Smart DNS, остальной трафик через обычный DNS" : "Весь трафик идет через Smart DNS";
        _dashboardHero.SetResourceReference(Border.BackgroundProperty, on ? "OkSoft" : "Surface");
        _dashboardHero.SetResourceReference(Border.BorderBrushProperty, on ? "Ok" : "Border");
        _dashboardHeroIcon.SetResourceReference(Border.BackgroundProperty, on ? "Ok" : "Surface2");
        _dashboardHeroMark.Kind = on ? "circle-check" : "shield-check";
        _dashboardHeroMark.SetResourceReference(Controls.Icon.ForegroundProperty, on ? "OnToggle" : "Muted");
        _dashboardSyncing = true;
        _dashboardPower.IsChecked = on;
        _dashboardPower.IsEnabled = !_busyConn && !_dashboardChecking;
        _dashboardSyncing = false;
        _dashboardPowerText.Text = _busyConn ? "Подождите" : on ? "Включено" : "Выключено";

        var server = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        _dashboardSmartName.Text = active == null ? "Не подключен" : PrivacyGuard.Mask(server?.ShortName ?? active.Name);
        _dashboardSmartName.ToolTip = active == null ? null : string.Join(", ", active.Addresses);
        _dashboardSmartFlag.Source = server?.Flag;
        _dashboardSmartFlag.Visibility = server?.Flag == null ? Visibility.Collapsed : Visibility.Visible;
        bool works = on && aiServices.Count > 0 && open > 0;
        _dashboardSmartChipBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _dashboardSmartChip.Text = aiServices.Count == 0 ? "Проверка" : works ? "Работает" : "Не открывает";
        _dashboardSmartChipBox.SetResourceReference(Border.BackgroundProperty, works ? "ChipOk" : aiServices.Count == 0 ? "Surface2" : "ChipBad");
        _dashboardSmartChip.SetResourceReference(TextBlock.ForegroundProperty, works ? "Ok" : aiServices.Count == 0 ? "Muted" : "Bad");
        _dashboardPlainDns.Text = snap == null ? "Еще не проверялся" : PrivacyGuard.Mask(snap.SystemDns);
        _dashboardPlainHint.Text = active?.Split == true ? $"Общий DNS: {active.GeneralName}" : on ? "Тот же Smart DNS" : "DNS вашей сети";
        _dashboardUptime.Text = on ? Uptime(DateTime.Now - active!.Since) : "Не подключено";
        _dashboardUptimeHint.Text = on ? $"Подключено с {active!.Since:dd.MM HH:mm}" : "Нажмите переключатель";

        // Нейросети
        foreach (var (name, row) in _dashboardServiceRows)
        {
            var s = aiServices.FirstOrDefault(x => x.Name == name);
            bool ok = s?.Access.StartsWith("✓") == true;
            var color = s == null ? "Muted" : ok ? "Ok" : "Warn";
            row.Status.Text = s == null ? "Не проверялся" : ok ? "Доступен" : Cap(s.Access.TrimStart('✓', '✗', ' '));
            row.Status.SetResourceReference(TextBlock.ForegroundProperty, color);
            row.Dot.SetResourceReference(Shape.FillProperty, color);
            row.Note.Text = s?.Egress is { } eg ? "Выход: " + GeoLocation.CountryName(eg.Country) : s?.Note != null ? "Подробности в подсказке" : "";
            row.Status.ToolTip = s?.Note;
        }
        _dashboardChecked.Text = snap == null ? "Еще не проверялось" : $"Проверено в {snap.Taken:HH:mm}" + (DateTime.Now - snap.Taken > TimeSpan.FromMinutes(30) ? ", данные могли устареть" : "");
        _dashboardProbe.IsEnabled = !_dashboardChecking && !_busyConn;
        _dashboardProbe.Content = _dashboardChecking ? "Проверяю…" : "Проверить все";

        // Обход и Telegram
        _dashboardSiteProbe.IsEnabled = !_dashboardSitesChecking;
        _dashboardSiteProbe.Content = _dashboardSitesChecking ? "Проверяю…" : "Проверить сайты";
        _dashboardSiteYoutube.Text = DashboardSiteStatus("youtube.com");
        _dashboardSiteDiscord.Text = DashboardSiteStatus("discord.com");
        _dashboardSiteTelegram.Text = DashboardSiteStatus("t.me");
        _dashboardSyncing = true;
        foreach (var (toggle, status, dot, getTool) in _dashboardToolRows)
        {
            var tool = getTool();
            bool running = tool.IsRunning();
            bool foreign = tool is TgProxyTool tg && tg.ForeignCopyRunning() || tool is ZapretTool && running && !tool.State.Enabled;
            status.Text = foreign ? "Запущен отдельно" : running ? "Работает" : tool.IsInstalled ? "Выключен" : "Не установлен";
            var color = running ? "Ok" : "Muted";
            status.SetResourceReference(TextBlock.ForegroundProperty, color);
            dot.SetResourceReference(Shape.FillProperty, color);
            toggle.IsChecked = running;
            toggle.IsEnabled = !_dashboardToolBusy && !foreign;
            toggle.ToolTip = foreign ? "Запущен другой программой: управляйте им там" : null;
        }
        _dashboardSyncing = false;

        RefreshDashboardServers(active);
        RefreshDashboardEvents();
        _dashboardMessage.Text = _dashboardMessageText ?? "";
        _dashboardMessage.Visibility = string.IsNullOrEmpty(_dashboardMessageText) ? Visibility.Collapsed : Visibility.Visible;
        _dashboardMessage.SetResourceReference(TextBlock.ForegroundProperty, _dashboardMessageError ? "Bad" : "Muted");
    }

    private static string Cap(string t) => t.Length == 0 ? t : char.ToUpper(t[0]) + t[1..];

    /// <summary>Три самых надежных рабочих сервера (тот, к которому подключены, всегда в списке). Перестраивается, только если список изменился.</summary>
    private void RefreshDashboardServers(DnsSwitcher.ActiveConnection? active)
    {
        var top = _servers.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && !s.IsJunk && Usable(s)).OrderBy(s => s.Rank).Take(3).ToList();
        if (active != null && _servers.FirstOrDefault(s => s.Id == active.ServerId) is { } cur && !top.Contains(cur)) { top.Insert(0, cur); if (top.Count > 3) top.RemoveAt(3); }
        var sig = string.Join("|", top.Select(s => $"{s.Id}:{s.LatencyMs}:{s.Stability}:{s.Id == active?.ServerId}:{s.Status}")) + _busyConn;
        if (sig == _dashboardServersSignature) return;
        _dashboardServersSignature = sig;
        _dashboardServers.Children.Clear();
        if (top.Count == 0) { _dashboardServers.Children.Add(Ui.Hint("Рабочих серверов пока нет. Нажмите «Найти новые серверы»", 4)); return; }
        var head = DashboardServerGrid();
        foreach (var (t, c) in new[] { ("Сервер", 1), ("Страна", 2), ("Пинг", 3), ("Стабильность", 4), ("Статус", 5) })
        {
            var h = Ui.Hint(t, 0).With(x => x.FontSize = 11); Grid.SetColumn(h, c); head.Children.Add(h);
        }
        head.Margin = new Thickness(6, 0, 6, 4);
        _dashboardServers.Children.Add(head);
        foreach (var s in top)
        {
            bool used = s.Id == active?.ServerId;
            var g = DashboardServerGrid();
            var radio = new Ellipse { Width = 14, Height = 14, StrokeThickness = used ? 4 : 1.5, VerticalAlignment = VerticalAlignment.Center };
            radio.SetResourceReference(Shape.StrokeProperty, used ? "Accent" : "ControlBorder");
            g.Children.Add(radio);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(s.ShortName), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(Ui.Hint(s.Address, 0).With(x => { x.FontSize = 11; x.TextTrimming = TextTrimming.CharacterEllipsis; }));
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var country = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (s.Flag != null) country.Children.Add(new Image { Source = s.Flag, Width = 18, Height = 12, Margin = new Thickness(0, 0, 7, 0), Stretch = Stretch.Uniform });
            country.Children.Add(new TextBlock { Text = s.CountryName.Length > 0 ? s.CountryName : "-", TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(country, 2); g.Children.Add(country);
            var ping = new TextBlock { Text = s.LatencyMs is null ? "-" : $"{s.LatencyMs} мс", VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(ping, 3); g.Children.Add(ping);
            var stab = new TextBlock { Text = s.Stability, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(stab, 4); g.Children.Add(stab);
            var chipText = new TextBlock { Text = used ? "Используется" : s.Status == ServerStatus.Ok ? "Доступен" : "Частично", FontSize = 12, FontWeight = FontWeights.SemiBold };
            chipText.SetResourceReference(TextBlock.ForegroundProperty, used || s.Status == ServerStatus.Ok ? "Ok" : "Warn");
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = chipText };
            chip.SetResourceReference(Border.BackgroundProperty, used || s.Status == ServerStatus.Ok ? "ChipOk" : "ChipWarn");
            Grid.SetColumn(chip, 5); g.Children.Add(chip);
            var row = new Border { Child = g, Padding = new Thickness(6, 7, 6, 7), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
                                   Cursor = used || _busyConn ? null : System.Windows.Input.Cursors.Hand, ToolTip = used ? "Подключено" : "Подключиться к этому серверу" };
            if (used) row.SetResourceReference(Border.BackgroundProperty, "Surface2");
            else
            {
                row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                var target = s;
                row.MouseLeftButtonUp += async (_, _) => { if (!_busyConn) { await ConnectToAsync(target); RefreshDashboard(); } };
            }
            _dashboardServers.Children.Add(row);
        }
    }

    private static Grid DashboardServerGrid()
    {
        var g = new Grid();
        foreach (var w in new[] { 30.0, -2, -1.2, 80, 110, 120 })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(-w, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private void RefreshDashboardEvents()
    {
        var recent = _log.TakeLast(6).ToArray();
        var signature = string.Join("|", recent.Select(e => $"{e.Time.Ticks}:{e.Text}"));
        if (signature == _dashboardEventsSignature) return;
        _dashboardEventsSignature = signature;
        _dashboardEvents.Children.Clear();
        if (recent.Length == 0) { _dashboardEvents.Children.Add(Ui.Hint("Событий пока нет", 0)); return; }
        var mono = new FontFamily("Cascadia Mono, Consolas");
        foreach (var entry in recent)
        {
            var (tag, color) = entry.Level switch { LogLevel.Ok => ("OK  ", "Ok"), LogLevel.Warn => ("WARN", "Warn"), LogLevel.Error => ("ERR ", "Bad"), _ => ("INFO", "Muted") };
            var line = new TextBlock { FontFamily = mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 1), ToolTip = PrivacyGuard.Mask(entry.Text) };
            line.Inlines.Add(new System.Windows.Documents.Run(entry.Time.ToString("HH:mm:ss") + "  ").With(r => r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted")));
            line.Inlines.Add(new System.Windows.Documents.Run($"[{tag}]  ").With(r => r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, color)));
            line.Inlines.Add(new System.Windows.Documents.Run(PrivacyGuard.Mask(entry.Text).Trim()).With(r => r.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, entry.Level is LogLevel.Info ? "Text" : color)));
            _dashboardEvents.Children.Add(line);
        }
    }

    private string DashboardSiteStatus(string host)
    {
        if (_dashboardSiteResults.TryGetValue(host, out var result)) return $"Сайт {DashboardSiteVerdict(result.Verdict)}, {result.Time:HH:mm}";
        if (_sites.Sites.TryGetValue(host, out var site) && site.LastChecked is { } time) return $"Сайт {DashboardSiteVerdict(site.State)}, {time:HH:mm}";
        return "Сайт еще не проверялся";
    }
    private static string DashboardSiteVerdict(SiteWatch.Verdict verdict) => verdict switch
    {
        SiteWatch.Verdict.Ok => "открывается", SiteWatch.Verdict.Unknown => "без результата", _ => "не открывается"
    };

    private void DashboardMessage(string? message, bool error = false)
    {
        _dashboardMessageText = message; _dashboardMessageError = error; RefreshDashboard();
    }
    private async Task DashboardConnectAsync()
    {
        if (_busyConn || _dashboardChecking) return;
        DashboardMessage(null);
        if (DnsSwitcher.GetActive() != null) await DisconnectByUserAsync();
        else
        {
            SelectBest();
            if (ServerGrid.SelectedItem is DnsServer s)
            {
                if (!await ConnectToAsync(s)) DashboardMessage("Не удалось подключиться. Проверьте журнал и диагностику.", true);
            }
            else { ShowControlPanel(); DashboardMessage("Сначала найдите рабочий сервер."); ShowToast("Сначала найдите рабочий сервер", "Warn"); }
        }
        if (_disconnectFailed) DashboardMessage("Не удалось восстановить DNS. Подробности в диагностике.", true);
        RefreshDashboard();
    }
    private async Task DashboardProbeAsync()
    {
        if (_dashboardChecking || _busyConn) return;
        _dashboardChecking = true; DashboardMessage("Проверяю подключение и сервисы…");
        try
        {
            var snap = await ConnectionProbe.TakeAsync(_settings, leakTest: true);
            if (_after == null && _before == null) _before = snap; else _after = snap;
            ShowCompare(); SetTraySnapshot(snap); DashboardMessage("Проверка завершена.");
        }
        catch (Exception ex) { Log("Ошибка проверки подключения: " + ex.Message); DashboardMessage("Проверка не удалась: " + ex.Message, true); }
        finally { _dashboardChecking = false; RefreshDashboard(); }
    }
    private async Task DashboardSitesAsync()
    {
        if (_dashboardSitesChecking) return;
        _dashboardSitesChecking = true; DashboardMessage("Проверяю YouTube, Discord и сайт Telegram…");
        try
        {
            var hosts = new[] { "youtube.com", "discord.com", "t.me" };
            var results = await Task.WhenAll(hosts.Select(h => SiteWatch.CheckAsync(h, false, CancellationToken.None)));
            for (int i = 0; i < hosts.Length; i++) _dashboardSiteResults[hosts[i]] = (results[i].Item1, DateTime.Now);
            DashboardMessage("Проверка сайтов завершена.");
        }
        catch (Exception ex) { Log("Проверка сайтов: " + ex.Message); DashboardMessage("Не удалось проверить сайты: " + ex.Message, true); }
        finally { _dashboardSitesChecking = false; RefreshDashboard(); }
    }
    private async Task DashboardToolAsync(Tool tool)
    {
        if (_dashboardToolBusy) return;
        bool running = tool.IsRunning();
        if (tool is TgProxyTool tg && tg.ForeignCopyRunning() || tool is ZapretTool && running && !tool.State.Enabled) return;
        _dashboardToolBusy = true; DashboardMessage($"{tool.Title}: выполняется…");
        try
        {
            if (running) { await tool.DisableAsync(); Log($"{tool.Title}: выключено"); DashboardMessage($"{tool.Title}: выключено"); }
            else
            {
                await tool.EnableAsync(new Progress<string>(p => DashboardMessage($"{tool.Title}: {p}")), CancellationToken.None);
                Log($"{tool.Title}: включено"); DashboardMessage($"{tool.Title}: включено");
            }
        }
        catch (Exception ex) { Log($"{tool.Title}: {ex.Message}"); DashboardMessage($"{tool.Title}: {ex.Message}", true); }
        finally { _dashboardToolBusy = false; UpdateToolsTray(); RefreshDashboard(); }
    }
    private void ShowControlPanel() { DashboardHost.Visibility = Visibility.Collapsed; RootGrid.Visibility = Visibility.Visible; }
    private void DashboardHome_Click(object sender, RoutedEventArgs e) { if (_compact) SetCompact(false); RootGrid.Visibility = Visibility.Collapsed; DashboardHost.Visibility = Visibility.Visible; RefreshDashboard(); }
}
