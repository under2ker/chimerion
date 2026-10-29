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
    private readonly DispatcherTimer _dashboardUpdateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<(Border Box, TextBlock Text)> _dashboardUpdateNotices = new();
    private TextBlock _dashboardTitle = null!, _dashboardDescription = null!, _dashboardDns = null!, _dashboardNetwork = null!;
    private TextBlock _dashboardChecked = null!, _dashboardZapret = null!, _dashboardTelegram = null!, _dashboardMessage = null!;
    private TextBlock _dashboardHeroMode = null!, _dashboardHeroSince = null!, _dashboardHeroServices = null!;
    private TextBlock _dashboardSiteYoutube = null!, _dashboardSiteDiscord = null!, _dashboardSiteTelegram = null!, _dashboardDiagnostics = null!;
    private StackPanel _dashboardEvents = null!;
    private string? _dashboardEventsSignature;
    private Border _dashboardHero = null!, _dashboardHeroIcon = null!;
    private readonly Dictionary<string, (TextBlock Status, Controls.Icon Icon)> _dashboardServices = new();
    private Button _dashboardConnect = null!, _dashboardZapretButton = null!, _dashboardTelegramButton = null!, _dashboardProbe = null!, _dashboardSiteProbe = null!;
    private bool _dashboardToolBusy, _dashboardChecking, _dashboardSitesChecking;
    private string? _dashboardMessageText;
    private bool _dashboardMessageError;
    private Guid? _dashboardPreviousServer;
    private DateTime _dashboardConnectionChanged = DateTime.MinValue;
    private bool _dashboardSeenConnection;
    private readonly Dictionary<string, (SiteWatch.Verdict Verdict, DateTime Time)> _dashboardSiteResults = new(StringComparer.OrdinalIgnoreCase);

    private void SetupDashboard()
    {
        var page = new StackPanel { Margin = new Thickness(32, 30, 32, 36), MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        var panel = Ui.Action("Панель управления", "Strong", ShowControlPanel);
        panel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(panel, Dock.Right); top.Children.Add(panel);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Главная", FontSize = 30, FontWeight = FontWeights.Bold });
        heading.Children.Add(Ui.Hint("Подключение, сервисы и быстрые действия", 4));
        top.Children.Add(heading); page.Children.Add(top);

        var hero = new DockPanel { LastChildFill = true };
        _dashboardConnect = Ui.Action("Подключить", "Primary", () => _ = DashboardConnectAsync());
        _dashboardConnect.Padding = new Thickness(20, 10, 20, 10);
        _dashboardConnect.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_dashboardConnect, Dock.Right); hero.Children.Add(_dashboardConnect);
        _dashboardHeroIcon = DashboardIcon("shield-check", "AccentSoft", "Accent", 64);
        _dashboardHeroIcon.Margin = new Thickness(0, 0, 16, 0);
        _dashboardHeroIcon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_dashboardHeroIcon, Dock.Left); hero.Children.Add(_dashboardHeroIcon);
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        heroText.Children.Add(Ui.Hint("SMART DNS", 0));
        _dashboardTitle = new TextBlock { FontSize = 25, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _dashboardDescription = Ui.Hint("", 5); heroText.Children.Add(_dashboardTitle); heroText.Children.Add(_dashboardDescription);
        hero.Children.Add(heroText);
        var heroContent = new StackPanel();
        heroContent.Children.Add(hero);
        var heroFacts = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < 3; i++) heroFacts.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardHeroMode = DashboardFact(heroFacts, 0, "Режим", "arrow-left-right");
        _dashboardHeroSince = DashboardFact(heroFacts, 1, "Подключение", "link");
        _dashboardHeroServices = DashboardFact(heroFacts, 2, "Доступ к ИИ", "circle-check");
        heroContent.Children.Add(heroFacts);
        var heroLayer = new Grid();
        heroLayer.SizeChanged += (_, e) => heroLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 11, 11);
        heroLayer.Children.Add(new DashboardAtmosphere());
        heroContent.Margin = new Thickness(24, 22, 24, 22);
        heroLayer.Children.Add(heroContent);
        _dashboardHero = Ui.Card(heroLayer, bottom: 0); _dashboardHero.Padding = new Thickness(0); page.Children.Add(_dashboardHero);
        _dashboardMessage = Ui.Hint("", 10); _dashboardMessage.Visibility = Visibility.Collapsed; page.Children.Add(_dashboardMessage);

        page.Children.Add(DashboardSection("Подключение"));
        var connection = new Grid(); connection.ColumnDefinitions.Add(new ColumnDefinition()); connection.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardDns = DashboardCard(connection, 0, "Smart DNS", "globe");
        _dashboardNetwork = DashboardCard(connection, 1, "DNS системы", "activity");
        page.Children.Add(connection);

        var aiHeader = new DockPanel { Margin = new Thickness(2, 20, 0, 8) };
        _dashboardProbe = Ui.Action("Проверить", "Strong", () => _ = DashboardProbeAsync());
        _dashboardProbe.Margin = new Thickness(0); _dashboardProbe.Padding = new Thickness(12, 5, 12, 5);
        DockPanel.SetDock(_dashboardProbe, Dock.Right); aiHeader.Children.Add(_dashboardProbe);
        var aiTitle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        aiTitle.Children.Add(new TextBlock { Text = "Доступ к нейросетям", FontSize = 16, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, "Text"));
        _dashboardChecked = Ui.Hint("Еще не проверялось", 3); aiTitle.Children.Add(_dashboardChecked);
        aiHeader.Children.Add(aiTitle); page.Children.Add(aiHeader);
        var serviceGrid = new Grid();
        var names = new[] { ("ChatGPT", "LogoOpenAI", "https://chatgpt.com/"), ("Claude", "LogoClaude", "https://claude.ai/"), ("Gemini", "LogoGemini", "https://gemini.google.com/") };
        for (int i = 0; i < names.Length; i++)
        {
            serviceGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var cell = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var image = new Image { Width = 25, Height = 25, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            image.SetResourceReference(Image.SourceProperty, names[i].Item2);
            var icon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0), Child = image }.Ref(Border.BackgroundProperty, "Surface2");
            title.Children.Add(icon); title.Children.Add(new TextBlock { Text = names[i].Item1, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            cell.Children.Add(title);
            var statusLine = new DockPanel { Margin = new Thickness(1, 16, 0, 2) };
            var statusIcon = new Controls.Icon { Kind = "info", Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusIcon.SetResourceReference(Controls.Icon.ForegroundProperty, "Muted");
            var text = new TextBlock { Text = "Еще не проверялось", FontSize = 12, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(statusIcon, Dock.Left); statusLine.Children.Add(statusIcon); statusLine.Children.Add(text); cell.Children.Add(statusLine);
            string url = names[i].Item3;
            var open = Ui.Action("Открыть", "Ghost", () => AutoStart.OpenUrl(url));
            open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(-10, 9, 0, -4); cell.Children.Add(open);
            _dashboardServices[names[i].Item1] = (text, statusIcon);
            var card = Ui.Card(cell, bottom: 0); card.Padding = new Thickness(16); card.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            Grid.SetColumn(card, i); serviceGrid.Children.Add(card);
        }
        page.Children.Add(serviceGrid);

        var featuresHeader = new DockPanel { Margin = new Thickness(2, 20, 0, 8) };
        _dashboardSiteProbe = Ui.Action("Проверить сайты", "Ghost", () => _ = DashboardSitesAsync());
        _dashboardSiteProbe.Margin = new Thickness(0); _dashboardSiteProbe.Padding = new Thickness(12, 5, 12, 5);
        DockPanel.SetDock(_dashboardSiteProbe, Dock.Right); featuresHeader.Children.Add(_dashboardSiteProbe);
        featuresHeader.Children.Add(new TextBlock { Text = "Дополнительные сервисы", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Text"));
        page.Children.Add(featuresHeader);
        var features = new Grid(); features.ColumnDefinitions.Add(new ColumnDefinition()); features.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardZapretButton = Ui.Action("Включить", "Strong", () => _ = DashboardToolAsync(_tools.OfType<ZapretTool>().First()));
        _dashboardZapret = Ui.Hint("", 5); _dashboardSiteYoutube = Ui.Hint("", 8); _dashboardSiteDiscord = Ui.Hint("", 3);
        var zapret = DashboardFeature("YouTube и Discord", "zap", _dashboardZapret, _dashboardZapretButton, _dashboardSiteYoutube, _dashboardSiteDiscord);
        zapret.Margin = new Thickness(0, 0, 5, 0); features.Children.Add(zapret);
        _dashboardTelegramButton = Ui.Action("Включить", "Strong", () => _ = DashboardToolAsync(_tools.OfType<TgProxyTool>().First()));
        _dashboardTelegram = Ui.Hint("", 5); _dashboardSiteTelegram = Ui.Hint("", 8);
        var telegram = DashboardFeature("Telegram", "send", _dashboardTelegram, _dashboardTelegramButton, _dashboardSiteTelegram, Ui.Hint("Проверяется сайт t.me, не соединение приложения", 3));
        telegram.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(telegram, 1); features.Children.Add(telegram);
        page.Children.Add(features);

        page.Children.Add(DashboardSection("Диагностика"));
        var diagnostics = new DockPanel();
        var diagnosticButton = Ui.Action("Открыть диагностику", "Strong", () => DiagToggle_Click(this, new RoutedEventArgs()));
        diagnosticButton.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(diagnosticButton, Dock.Right); diagnostics.Children.Add(diagnosticButton);
        var diagnosticText = new StackPanel();
        diagnosticText.Children.Add(new TextBlock { Text = "Состояние соединения", FontSize = 15, FontWeight = FontWeights.SemiBold });
        _dashboardDiagnostics = Ui.Hint("", 5); diagnosticText.Children.Add(_dashboardDiagnostics); diagnostics.Children.Add(diagnosticText);
        page.Children.Add(Ui.Card(diagnostics, bottom: 0));
        page.Children.Add(DashboardSection("Последние события"));
        var events = new DockPanel();
        var eventsButton = Ui.Action("Открыть журнал", "Ghost", () =>
        {
            ShowControlPanel();
            LogToggle.IsChecked = true;
        });
        eventsButton.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(eventsButton, Dock.Right); events.Children.Add(eventsButton);
        _dashboardEvents = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; events.Children.Add(_dashboardEvents);
        page.Children.Add(Ui.Card(events, bottom: 0));
        var footer = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(Ui.Action("Настройки", "Ghost", () => Settings_Click(this, new RoutedEventArgs())));
        var footerUpdate = new StackPanel();
        footerUpdate.Children.Add(DashboardUpdateNotice());
        footerUpdate.Children.Add(DashboardUpdateButton("Проверить обновление"));
        footer.Children.Add(footerUpdate);
        footer.Children.Add(Ui.Action("Настроить обход", "Ghost", () => ToolsToggle_Click(this, new RoutedEventArgs())));
        page.Children.Add(footer);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = page };
        var layout = new Grid();
        var sidebarColumn = new ColumnDefinition { Width = new GridLength(208) };
        layout.ColumnDefinitions.Add(sidebarColumn); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = DashboardSidebar(); Grid.SetColumn(scroll, 1); layout.Children.Add(sidebar); layout.Children.Add(scroll);
        int lastLayout = -1;
        layout.SizeChanged += (_, e) =>
        {
            bool wide = e.NewSize.Width >= 1180;
            sidebarColumn.Width = new GridLength(wide ? 208 : 0);
            sidebar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
            double available = e.NewSize.Width - (wide ? 208 : 0);
            int mode = available < 600 ? 0 : available < 900 ? 1 : 2;
            bool compactHero = available < 680;
            int layoutKey = mode * 2 + (compactHero ? 1 : 0);
            if (layoutKey == lastLayout) return;
            lastLayout = layoutKey;
            page.Margin = mode == 0 ? new Thickness(16, 20, 16, 24) : new Thickness(32, 30, 32, 36);
            DashboardReflow(heroFacts, mode == 0 ? 1 : mode == 1 ? 2 : 3);
            DashboardReflow(connection, mode < 2 ? 1 : 2);
            DashboardReflow(serviceGrid, mode == 0 ? 1 : mode == 1 ? 2 : 3);
            DashboardReflow(features, mode < 2 ? 1 : 2);
            _dashboardHeroIcon.Visibility = compactHero ? Visibility.Collapsed : Visibility.Visible;
            DockPanel.SetDock(_dashboardConnect, compactHero ? Dock.Bottom : Dock.Right);
            _dashboardConnect.HorizontalAlignment = compactHero ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _dashboardConnect.Margin = compactHero ? new Thickness(0, 14, 0, 0) : new Thickness(0, 0, 8, 0);
        };
        DashboardHost.Content = layout;
        Loaded += (_, _) => RefreshDashboard();
        _dashboardUpdateTimer.Tick += (_, _) => HideDashboardUpdateResult();
        _dashboardTimer.Tick += (_, _) => RefreshDashboard(); _dashboardTimer.Start();
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
        var bottom = new DockPanel { Margin = new Thickness(8, 0, 0, 0), LastChildFill = true };
        var update = DashboardUpdateButton("Обновить");
        update.FontSize = 11; update.Padding = new Thickness(5, 5, 5, 5); update.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(update, Dock.Right); bottom.Children.Add(update);
        var version = Ui.Hint($"CHIMERION · {UpdateChecker.CurrentVersion}", 0);
        version.FontSize = 10; version.VerticalAlignment = VerticalAlignment.Center; version.TextTrimming = TextTrimming.CharacterEllipsis;
        bottom.Children.Add(version);
        var bottomGroup = new StackPanel(); bottomGroup.Children.Add(DashboardUpdateNotice()); bottomGroup.Children.Add(bottom);
        DockPanel.SetDock(bottomGroup, Dock.Bottom); content.Children.Add(bottomGroup);
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

    private static TextBlock DashboardSection(string title) => new TextBlock
    {
        Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 24, 0, 10),
    };

    private static TextBlock DashboardFact(Grid grid, int index, string label, string kind)
    {
        var row = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new Controls.Icon { Kind = kind, Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(Ui.Hint(label, 0)); row.Children.Add(heading);
        var value = new TextBlock { Text = "—", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(23, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        row.Children.Add(value);
        var card = new Border { Padding = new Thickness(12, 11, 12, 11), Margin = new Thickness(index == 0 ? 0 : 5, 0, index == 2 ? 0 : 5, 0), CornerRadius = new CornerRadius(10), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); Grid.SetColumn(card, index); grid.Children.Add(card); return value;
    }

    private static Border DashboardIcon(string kind, string background, string foreground, double size)
    {
        var icon = new Controls.Icon { Kind = kind, Size = size * .48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Controls.Icon.ForegroundProperty, foreground);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(11), Child = icon }.Ref(Border.BackgroundProperty, background);
    }

    private static TextBlock DashboardCard(Grid grid, int column, string label, string icon)
    {
        var content = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Controls.Icon { Kind = icon, Size = 16, Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(Ui.Hint(label, 0)); content.Children.Add(header);
        var value = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(value);
        var card = Ui.Card(content, bottom: 0); card.Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 0 ? 5 : 0, 0);
        Grid.SetColumn(card, column); grid.Children.Add(card); return value;
    }

    private static Border DashboardFeature(string name, string icon, TextBlock status, Button button, params TextBlock[] details)
    {
        var root = new StackPanel(); var row = new DockPanel();
        button.Margin = new Thickness(8, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        var symbol = DashboardIcon(icon, "Surface2", "Accent", 38); symbol.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(symbol, Dock.Left); row.Children.Add(symbol);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(status); row.Children.Add(text); root.Children.Add(row);
        foreach (var detail in details) root.Children.Add(detail);
        return Ui.Card(root, bottom: 0);
    }

    private void RefreshDashboard()
    {
        if (!IsVisible || DashboardHost.Visibility != Visibility.Visible) return;
        var active = DnsSwitcher.GetActive();
        if (_dashboardSeenConnection && _dashboardPreviousServer != active?.ServerId) _dashboardConnectionChanged = DateTime.Now;
        _dashboardSeenConnection = true; _dashboardPreviousServer = active?.ServerId;
        var snap = new[] { _traySnap, _after, _before }.Where(x => x != null).OrderByDescending(x => x!.Taken).FirstOrDefault();
        if (snap != null && (snap.Taken < _dashboardConnectionChanged || (active != null && snap.Taken < active.Since))) snap = null;
        _dashboardTitle.Text = _busyConn ? "Подключение меняется" : active == null ? "Smart DNS отключен" : "Smart DNS подключен";
        _dashboardDescription.Text = active == null ? "Подключите рабочий сервер для доступа к сервисам." : active.Split ? "Нейросети идут через Smart DNS, остальная сеть через системный DNS." : "DNS настроен на сетевых адаптерах.";
        _dashboardHero.SetResourceReference(Border.BackgroundProperty, active == null ? "Surface" : "OkSoft");
        _dashboardHero.SetResourceReference(Border.BorderBrushProperty, active == null ? "Border" : "Ok");
        _dashboardHeroIcon.SetResourceReference(Border.BackgroundProperty, active == null ? "Surface2" : "Surface");
        _dashboardConnect.Content = _busyConn ? "Подождите…" : active == null ? "Подключить" : "Отключить";
        _dashboardConnect.IsEnabled = !_busyConn && !_dashboardChecking;
        _dashboardHeroMode.Text = active == null ? "Отключен" : active.Split ? "Два DNS" : "Smart DNS";
        _dashboardHeroSince.Text = active == null ? "Не подключено" : $"Подключено с {active.Since:dd.MM HH:mm}";
        var available = snap?.Services.Count(x => (x.Name is "ChatGPT" or "Claude" or "Gemini") && x.Access.StartsWith("✓")) ?? 0;
        var total = snap?.Services.Count(x => x.Name is "ChatGPT" or "Claude" or "Gemini") ?? 0;
        _dashboardHeroServices.Text = total == 0 ? "Не проверялось" : $"Доступно: {available} из {total}";
        _dashboardDns.Text = active == null ? "Не подключен" : PrivacyGuard.Mask(active.Name + (active.Addresses.Count > 0 ? "\n" + string.Join(", ", active.Addresses) : ""));
        _dashboardNetwork.Text = snap == null ? "Еще не проверялось" : PrivacyGuard.Mask(snap.SystemDns);
        foreach (var (name, entry) in _dashboardServices)
        {
            var service = snap?.Services.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool ok = service?.Access.StartsWith("✓") == true;
            entry.Status.Text = service == null ? "Еще не проверялось" : service.Access.TrimStart('✓', '✗', ' ');
            var color = service == null ? "Muted" : ok ? "Ok" : "Warn";
            entry.Icon.Kind = service == null ? "info" : ok ? "circle-check" : "circle-x";
            entry.Status.SetResourceReference(TextBlock.ForegroundProperty, color);
            entry.Icon.SetResourceReference(Controls.Icon.ForegroundProperty, color);
            entry.Status.ToolTip = service?.Note;
        }
        _dashboardChecked.Text = snap == null ? "Еще не проверялось" : $"Последняя проверка: {snap.Taken:dd.MM HH:mm}" + (DateTime.Now - snap.Taken > TimeSpan.FromMinutes(30) ? " · данные могут устареть" : "");
        _dashboardProbe.IsEnabled = !_dashboardChecking && !_busyConn;
        _dashboardProbe.Content = _dashboardChecking ? "Проверяю…" : "Проверить";
        _dashboardSiteProbe.IsEnabled = !_dashboardSitesChecking;
        _dashboardSiteProbe.Content = _dashboardSitesChecking ? "Проверяю…" : "Проверить сайты";
        var z = _tools.OfType<ZapretTool>().First(); var tg = _tools.OfType<TgProxyTool>().First();
        RefreshTool(z, _dashboardZapret, _dashboardZapretButton); RefreshTool(tg, _dashboardTelegram, _dashboardTelegramButton);
        _dashboardSiteYoutube.Text = DashboardSiteStatus("YouTube", "youtube.com");
        _dashboardSiteDiscord.Text = DashboardSiteStatus("Discord", "discord.com");
        _dashboardSiteTelegram.Text = DashboardSiteStatus("t.me", "t.me");
        var checkedCount = snap?.Services.Count(x => x.Name is "ChatGPT" or "Claude" or "Gemini") ?? 0;
        var openCount = snap?.Services.Count(x => (x.Name is "ChatGPT" or "Claude" or "Gemini") && x.Access.StartsWith("✓")) ?? 0;
        _dashboardDiagnostics.Text = _disconnectFailed ? "Не удалось восстановить DNS. Откройте диагностику." : snap == null || checkedCount == 0 ? "Проверьте подключение, чтобы увидеть состояние сервисов." : $"Доступно сервисов: {openCount} из {checkedCount}. Подробности в диагностике.";
        RefreshDashboardEvents();
        _dashboardMessage.Text = _dashboardMessageText ?? "";
        _dashboardMessage.Visibility = string.IsNullOrEmpty(_dashboardMessageText) ? Visibility.Collapsed : Visibility.Visible;
        _dashboardMessage.SetResourceReference(TextBlock.ForegroundProperty, _dashboardMessageError ? "Bad" : "Muted");
    }

    private void RefreshDashboardEvents()
    {
        var recent = _log.TakeLast(4).Reverse().ToArray();
        var signature = string.Join("|", recent.Select(e => $"{e.Time.Ticks}:{PrivacyGuard.Mask(e.Text)}"));
        if (signature == _dashboardEventsSignature) return;
        _dashboardEventsSignature = signature;
        _dashboardEvents.Children.Clear();
        if (recent.Length == 0) { _dashboardEvents.Children.Add(Ui.Hint("Событий пока нет", 0)); return; }
        for (int i = 0; i < recent.Length; i++)
        {
            var entry = recent[i];
            var color = entry.Level switch { LogLevel.Ok => "Ok", LogLevel.Warn => "Warn", LogLevel.Error => "Bad", _ => "Muted" };
            var kind = entry.Level switch { LogLevel.Ok => "circle-check", LogLevel.Warn => "triangle-alert", LogLevel.Error => "circle-x", _ => "info" };
            var soft = entry.Level switch { LogLevel.Ok => "OkSoft", LogLevel.Warn => "WarnSoft", LogLevel.Error => "BadSoft", _ => "InfoSoft" };
            var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Controls.Icon { Kind = kind, Size = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Controls.Icon.ForegroundProperty, color);
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = icon };
            badge.SetResourceReference(Border.BackgroundProperty, soft); row.Children.Add(badge);
            // «Проверка сайтов: x.com блокирует провайдер» → заголовок «Проверка сайтов» и подробности ниже
            var text = PrivacyGuard.Mask(entry.Text).Trim();
            var cut = text.IndexOf(": ", StringComparison.Ordinal);
            string title = cut is > 0 and < 40 ? text[..cut] : text, detail = cut is > 0 and < 40 ? text[(cut + 2)..] : "";
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(content, 1);
            content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (detail.Length > 0) content.Children.Add(Ui.Hint(detail, 2).With(t => { t.FontSize = 12; t.MaxHeight = 36; t.TextTrimming = TextTrimming.CharacterEllipsis; }));
            row.Children.Add(content);
            var time = Ui.Hint(entry.Time.ToString("HH:mm"), 0).With(t => { t.FontSize = 11; t.Margin = new Thickness(12, 2, 0, 0); t.VerticalAlignment = VerticalAlignment.Top; });
            Grid.SetColumn(time, 2); row.Children.Add(time);
            _dashboardEvents.Children.Add(row);
        }
    }

    private string DashboardSiteStatus(string name, string host)
    {
        if (_dashboardSiteResults.TryGetValue(host, out var result)) return $"{name}: {DashboardSiteVerdict(result.Verdict)} · {result.Time:HH:mm}";
        if (_sites.Sites.TryGetValue(host, out var site) && site.LastChecked is { } time) return $"{name}: {DashboardSiteVerdict(site.State)} · {time:dd.MM HH:mm}";
        return $"{name}: еще не проверялся";
    }
    private static string DashboardSiteVerdict(SiteWatch.Verdict verdict) => verdict switch
    {
        SiteWatch.Verdict.Ok => "доступен", SiteWatch.Verdict.Unknown => "нет результата", _ => "есть проблема"
    };

    private void RefreshTool(Tool tool, TextBlock status, Button button)
    {
        bool running = tool.IsRunning();
        bool foreign = tool is TgProxyTool tg && tg.ForeignCopyRunning() || tool is ZapretTool && running && !tool.State.Enabled;
        status.Text = foreign ? "Запущен отдельно" : running ? "Инструмент работает" : tool.IsInstalled ? "Выключен" : "Еще не установлен";
        button.Content = foreign ? "Управляется отдельно" : running ? "Выключить" : "Включить";
        button.IsEnabled = !_dashboardToolBusy && !foreign;
    }

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
    private Button DashboardUpdateButton(string title)
    {
        var button = Ui.Action(title, "Ghost", OpenDashboardUpdate);
        button.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = CheckUpdateButton });
        return button;
    }

    private Border DashboardUpdateNotice()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var box = new Border { Child = text, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, Opacity = 0 };
        box.SetResourceReference(Border.BackgroundProperty, "Surface2");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        _dashboardUpdateNotices.Add((box, text)); return box;
    }

    private void OpenDashboardUpdate()
    {
        if (!CheckUpdateButton.IsEnabled) return;
        if (_pendingUpdate == null) ShowDashboardUpdateResult("Проверяю обновление…", "Muted", null, false);
        CheckUpdate_Click(this, new RoutedEventArgs());
    }

    private void ShowDashboardUpdateResult(string text, string color, string? reason, bool autoHide = true)
    {
        if (DashboardHost.Visibility != Visibility.Visible) return;
        _dashboardUpdateTimer.Stop();
        foreach (var (box, label) in _dashboardUpdateNotices)
        {
            label.Text = text; label.ToolTip = reason;
            label.SetResourceReference(TextBlock.ForegroundProperty, color);
            box.Visibility = Visibility.Visible;
            box.BeginAnimation(OpacityProperty, new DoubleAnimation(box.Opacity, 1, Ui.Motion(220)));
        }
        if (autoHide) _dashboardUpdateTimer.Start();
    }

    private void HideDashboardUpdateResult()
    {
        _dashboardUpdateTimer.Stop();
        foreach (var (box, _) in _dashboardUpdateNotices)
        {
            var fade = new DoubleAnimation(0, Ui.Motion(400));
            fade.Completed += (_, _) => { if (box.Opacity <= 0) box.Visibility = Visibility.Collapsed; };
            box.BeginAnimation(OpacityProperty, fade);
        }
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
