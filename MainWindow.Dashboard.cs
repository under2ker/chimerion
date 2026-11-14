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
    private Border _dashboardHero = null!, _dashboardHeroIcon = null!;
    private Button _dashboardProbe = null!, _dashboardSiteProbe = null!;
    private ProgressBar _dashboardProbeProgress = null!, _dashboardSiteProgress = null!;
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
    private readonly Dictionary<string, (Ellipse Dot, TextBlock Status, TextBlock Note, Button Action, string Url)> _dashboardServiceRows = new();
    private Border _dashboardWarn = null!;
    private TextBlock _dashboardWarnText = null!, _dashboardSmartHint = null!;
    private Guid? _dashboardPickId; // выбранный в «Лучших серверах», но еще не подключенный
    private readonly List<(Controls.Switch Toggle, TextBlock Status, Ellipse Dot, Func<Tool> Tool)> _dashboardToolRows = new();
    private StackPanel _dashboardServers = null!;
    private string? _dashboardServersSignature;
    private bool _dashboardSyncing; // обновление переключателей из кода не должно запускать действия
    private Grid _dashboardHeroBadge = null!;
    private Border _dashboardHeroGlow = null!;
    private Ellipse _dashboardSpinner = null!, _dashboardRipple = null!;
    private readonly RotateTransform _dashboardSpin = new();
    private readonly ScaleTransform _dashboardRippleScale = new();
    /// <summary>Что человек попросил переключателем, пока это выполняется: переключатель стоит там, куда его перевели.</summary>
    private bool? _dashboardPowerTarget;
    private (Tool Tool, bool On)? _dashboardToolTarget;
    private bool? _dashboardShownOn;
    private bool _dashboardSpinning;

    /// <summary>Платный CHIMERION DNS на главной: небольшая золотая кнопка со звездой, цена и состав в подсказке и в окне описания.</summary>
    private static Button DashboardPromo()
    {
        var plan = ChimerionDnsInfo.Plans[0];
        var star = new Controls.Icon { Kind = "star", Solid = true, Size = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(Controls.Icon.ForegroundProperty, "OnPromoButton");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(star);
        line.Children.Add(new TextBlock { Text = "CHIMERION DNS", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var b = Ui.Action("CHIMERION DNS", "Primary", ChimerionDnsWindow.Show).With(x =>
        {
            x.Content = line;
            x.Margin = new Thickness(0, 0, 4, 0); x.Padding = new Thickness(12, 5, 12, 6); x.MinHeight = 30; x.VerticalAlignment = VerticalAlignment.Center;
            x.SetResourceReference(Control.BackgroundProperty, "PromoButton");
            x.SetResourceReference(Control.BorderBrushProperty, "PromoButton");
            x.SetResourceReference(Control.ForegroundProperty, "OnPromoButton");
            x.ToolTip = $"Личный Smart DNS · от {plan.Rub} ₽ за {plan.Days} дней. Что входит и где купить";
        });
        System.Windows.Automation.AutomationProperties.SetName(b, "CHIMERION DNS Премиум");
        return b;
    }

    private void SetupDashboard()
    {
        // VerticalAlignment.Top: иначе область прокрутки растягивает страницу до высоты окна и подгонка видит чужую высоту
        var page = new StackPanel { Margin = new Thickness(20, 12, 20, 12), MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };

        // ----- Карточка защиты: знак, состояние, большой переключатель и три факта -----
        var heroTop = new DockPanel();
        var power = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        _dashboardPower = new Controls.Switch { Width = 64, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Включить или выключить Smart DNS" };
        System.Windows.Automation.AutomationProperties.SetName(_dashboardPower, "Защита");
        _dashboardPower.Click += async (_, _) =>
        {
            if (_dashboardSyncing) return;
            if (_busyConn || _dashboardChecking || _dashboardPowerTarget != null) { RefreshDashboard(); return; } // занято: переключатель вернется на место
            _dashboardPowerTarget = _dashboardPower.IsChecked == true;
            RefreshDashboard(); // сразу «Включаю защиту…»: ответ на нажатие без ожидания прав и проверок
            try { await DashboardConnectAsync(); }
            finally { _dashboardPowerTarget = null; RefreshDashboard(); }
        };
        _dashboardPowerText = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 104 };
        power.Children.Add(_dashboardPower); power.Children.Add(_dashboardPowerText);
        DockPanel.SetDock(power, Dock.Right); heroTop.Children.Add(power);
        // Платный CHIMERION DNS: небольшая золотая кнопка между заголовком и переключателем
        var promo = DashboardPromo();
        DockPanel.SetDock(promo, Dock.Right); heroTop.Children.Add(promo);
        // На узком окне плашка не теснит заголовок и описание: прячется, пока места меньше, чем нужно всем трем частям
        heroTop.SizeChanged += (_, _) => promo.Visibility = heroTop.ActualWidth >= 820 ? Visibility.Visible : Visibility.Collapsed;
        _dashboardHeroMark = new Controls.Icon { Kind = "circle-check", Size = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _dashboardHeroIcon = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Child = _dashboardHeroMark,
                                          HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // Волна при включении и бегущее кольцо, пока защита включается или выключается
        _dashboardRipple = new Ellipse { Width = 54, Height = 54, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5), RenderTransform = _dashboardRippleScale };
        _dashboardRipple.SetResourceReference(Shape.FillProperty, "Ok");
        _dashboardSpinner = new Ellipse { Width = 64, Height = 64, StrokeThickness = 3, Opacity = 0, IsHitTestVisible = false, StrokeDashCap = PenLineCap.Round,
                                          StrokeDashArray = new DoubleCollection { 16, 64 }, RenderTransformOrigin = new Point(.5, .5), RenderTransform = _dashboardSpin };
        _dashboardSpinner.SetResourceReference(Shape.StrokeProperty, "Accent");
        _dashboardHeroBadge = new Grid { Width = 66, Height = 66, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _dashboardHeroBadge.Children.Add(_dashboardRipple); _dashboardHeroBadge.Children.Add(_dashboardSpinner); _dashboardHeroBadge.Children.Add(_dashboardHeroIcon);
        DockPanel.SetDock(_dashboardHeroBadge, Dock.Left); heroTop.Children.Add(_dashboardHeroBadge);
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _dashboardTitle = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        _dashboardDescription = new TextBlock { FontSize = 14, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        _dashboardHeroMode = Ui.Hint("", 3);
        heroText.Children.Add(_dashboardTitle); heroText.Children.Add(_dashboardDescription); heroText.Children.Add(_dashboardHeroMode);
        heroTop.Children.Add(heroText);

        var heroFacts = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (int i = 0; i < 3; i++) heroFacts.ColumnDefinitions.Add(new ColumnDefinition());
        // Smart DNS для нейросетей: имя сервера, флаг и плашка состояния
        // WrapPanel: в узком окне флаг и плашка уходят на следующую строку, а название занимает всю ширину карточки
        var smartValue = new WrapPanel { Orientation = Orientation.Horizontal };
        _dashboardSmartName = new TextBlock { FontSize = 15.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        _dashboardSmartFlag = new Image { Width = 18, Height = 12, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.Uniform };
        _dashboardSmartChip = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
        _dashboardSmartChipBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center, Child = _dashboardSmartChip };
        smartValue.Children.Add(_dashboardSmartName); smartValue.Children.Add(_dashboardSmartFlag); smartValue.Children.Add(_dashboardSmartChipBox);
        _dashboardSmartHint = Ui.Hint("", 2); // в карточку не вставляется: страна выхода показана под заголовком защиты
        DashboardFact(heroFacts, 0, "globe", "Smart DNS", smartValue, null); // страна выхода стоит в первой строке карточки, под заголовком
        _dashboardPlainDns = new TextBlock { FontSize = 15.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _dashboardPlainHint = Ui.Hint("", 2);
        DashboardFact(heroFacts, 1, "arrow-left-right", "Обычный DNS", _dashboardPlainDns, null); // пояснение в подсказке
        _dashboardUptime = new TextBlock { FontSize = 15.5, FontWeight = FontWeights.SemiBold };
        _dashboardUptimeHint = Ui.Hint("", 2);
        DashboardFact(heroFacts, 2, "activity", "Время работы", _dashboardUptime, _dashboardUptimeHint);

        // Подключено, но не все нейросети открываются: заметно и с переходом к проверке причины
        _dashboardWarnText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Warn");
        var why = Ui.Action("Почему?", "Strong", () => OpenHelp(HelpWindow.Tab.NotOpening)).With(b => { b.Margin = new Thickness(12, 0, 0, 0); b.Padding = new Thickness(12, 4, 12, 5); b.VerticalAlignment = VerticalAlignment.Center; });
        var warnRow = new DockPanel();
        DockPanel.SetDock(why, Dock.Right); warnRow.Children.Add(why);
        var warnIcon = new Controls.Icon { Kind = "triangle-alert", Size = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, "Warn");
        DockPanel.SetDock(warnIcon, Dock.Left); warnRow.Children.Add(warnIcon); warnRow.Children.Add(_dashboardWarnText);
        _dashboardWarn = new Border { Child = warnRow, Padding = new Thickness(14, 9, 10, 9), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed }
            .Ref(Border.BackgroundProperty, "ChipWarn");
        var heroContent = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };
        heroContent.Children.Add(heroTop); heroContent.Children.Add(_dashboardWarn); heroContent.Children.Add(heroFacts);
        var heroLayer = new Grid();
        heroLayer.SizeChanged += (_, e) => heroLayer.Clip = new RectangleGeometry(new Rect(e.NewSize), 11, 11);
        _dashboardHeroGlow = new Border { Opacity = 0, IsHitTestVisible = false };
        _dashboardHeroGlow.SetResourceReference(Border.BackgroundProperty, "OkSoft");
        heroLayer.Children.Add(_dashboardHeroGlow);
        heroLayer.Children.Add(new DashboardAtmosphere());
        heroLayer.Children.Add(heroContent);
        _dashboardHero = Ui.Card(heroLayer, bottom: 0); _dashboardHero.Padding = new Thickness(0); page.Children.Add(_dashboardHero);
        _dashboardHero.SetResourceReference(Border.BackgroundProperty, "Surface");
        _dashboardMessage = Ui.Hint("", 8); _dashboardMessage.Visibility = Visibility.Collapsed; page.Children.Add(_dashboardMessage);

        // ----- Две колонки: нейросети и дополнительные сервисы -----
        var services = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        services.ColumnDefinitions.Add(new ColumnDefinition()); services.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardProbe = DashboardHeaderButton("Проверить все", () => _ = DashboardProbeAsync());
        var ai = new StackPanel();
        _dashboardChecked = Ui.Hint("", 0);
        ai.Children.Add(DashboardPanelHeader("ИИ-сервисы", _dashboardChecked, _dashboardProbe));
        _dashboardProbeProgress = DashboardCheckProgress();
        ai.Children.Add(_dashboardProbeProgress);
        foreach (var (name, logo, url, domain) in new[] { ("ChatGPT", "LogoOpenAI", "https://chatgpt.com/", "chatgpt.com"), ("Claude", "LogoClaude", "https://claude.ai/", "claude.ai"), ("Gemini", "LogoGemini", "https://gemini.google.com/", "gemini.google.com") })
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var note = Ui.Hint("", 1).With(t => t.FontSize = 12);
            // Одинаково заметная кнопка в каждой строке: «Открыть», а если сервис не открывается, «Подробнее»
            var open = Ui.Action("Открыть", "Strong", () => { }); open.Padding = new Thickness(12, 5, 12, 6); open.Margin = new Thickness(10, 0, 0, 0); open.MinWidth = 104;
            var target = name;
            open.Click += (_, _) => { if (open.Tag as string == "why") OpenHelp(HelpWindow.Tab.NotOpening); else AutoStart.OpenUrl(_dashboardServiceRows[target].Url); };
            ai.Children.Add(DashboardRow(DashboardLogo(logo), name, domain, dot, status, note, open));
            _dashboardServiceRows[name] = (dot, status, note, open, url);
        }
        var aiCard = Ui.Card(ai, bottom: 0); aiCard.Padding = new Thickness(14, 10, 14, 4); aiCard.Margin = new Thickness(0, 0, 6, 0);
        services.Children.Add(aiCard);

        _dashboardSiteProbe = DashboardHeaderButton("Проверить сайты", () => _ = DashboardSitesAsync());
        var extra = new StackPanel();
        extra.Children.Add(DashboardPanelHeader("Дополнительные сервисы", null, _dashboardSiteProbe));
        _dashboardSiteProgress = DashboardCheckProgress();
        extra.Children.Add(_dashboardSiteProgress);
        _dashboardSiteYoutube = Ui.Hint("", 1); _dashboardSiteDiscord = Ui.Hint("", 1); _dashboardSiteTelegram = Ui.Hint("", 1);
        foreach (var (name, kind, sub, tool, site) in new (string, string, string, Func<Tool>, TextBlock)[]
                 {
                     ("YouTube", "LogoYouTube", "Обход замедления через zapret", () => _tools.OfType<ZapretTool>().First(), _dashboardSiteYoutube),
                     ("Discord", "LogoDiscord", "Обход замедления через zapret", () => _tools.OfType<ZapretTool>().First(), _dashboardSiteDiscord),
                     ("Telegram", "LogoTelegram", "Ускорение через TG WS Proxy", () => _tools.OfType<TgProxyTool>().First(), _dashboardSiteTelegram),
                 })
        {
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Controls.Switch { Width = 44, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = name + ": включить или выключить" };
            System.Windows.Automation.AutomationProperties.SetName(toggle, name);
            WireToolSwitch(toggle, tool);
            var symbol = DashboardLogo(kind); // фирменный логотип, как у нейросетей
            extra.Children.Add(DashboardRow(symbol, name, sub, dot, status, site, toggle));
            _dashboardToolRows.Add((toggle, status, dot, tool));
        }
        var extraCard = Ui.Card(extra, bottom: 0); extraCard.Padding = new Thickness(14, 10, 14, 4); extraCard.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(extraCard, 1); services.Children.Add(extraCard);
        page.Children.Add(services);
        var modesCard = BuildDashboardModes(); page.Children.Add(modesCard); // режим в один щелчок (MainWindow.DashboardModes.cs)
        var settingsCard = BuildDashboardSettings(); page.Children.Add(settingsCard); // «Два DNS» и часовой пояс одним спокойным блоком (MainWindow.DashboardSplit.cs)

        // ----- Лучшие серверы: три самых надежных, щелчок подключает -----
        var serversBox = new StackPanel();
        serversBox.Children.Add(DashboardPanelHeader("Лучшие серверы", Ui.Hint("Нажмите на сервер, и в его строке появится «Подключить»", 0).With(h => h.ToolTip = "Смену страны выхода программа подтвердит отдельно"),
            DashboardHeaderButton("Найти новые серверы", () => { ShowControlPanel(); Fetch_Click(this, new RoutedEventArgs()); }),
            DashboardHeaderButton("Подобрать автоматически", () => _ = AutoPickAsync()).With(b => b.ToolTip = "Программа сама пробует лучшие серверы по очереди и остается на том, через который открываются нейросети. Страну выхода не меняет")));
        _dashboardServers = new StackPanel(); serversBox.Children.Add(_dashboardServers);
        var serversCard = Ui.Card(serversBox, bottom: 0); serversCard.Padding = new Thickness(14, 10, 14, 6); serversCard.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(serversCard);

        // Главная должна помещаться без прокрутки: если высоты окна не хватает, вся страница слегка уменьшается
        var scale = new ScaleTransform(1, 1);
        var scaleHost = new Border { Child = page, LayoutTransform = scale };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = scaleHost };
        // Левое меню общее для всей программы (MainWindow.xaml), у главной своего нет
        var layout = new Grid();
        layout.Children.Add(scroll);
        int lastLayout = -1;
        var gapped = new FrameworkElement[] { services, modesCard, settingsCard, serversCard };
        double extraGap = 0;
        const double TwoColumns = 1040; // ширина окна под страницу, от которой нейросети и дополнительные сервисы стоят рядом
        layout.SizeChanged += (_, e) =>
        {
            double available = e.NewSize.Width; // только реальная ширина: если учитывать масштаб, раскладка и масштаб раскачивают друг друга
            int mode = available < 640 ? 0 : available < TwoColumns ? 1 : 2; // две колонки сервисов только когда им хватает места без обрезания подписей
            if (mode == lastLayout) { Fit(); return; }
            lastLayout = mode;
            page.Margin = mode == 0 ? new Thickness(12, 10, 12, 10) : new Thickness(20, 12, 20, 12);
            DashboardReflow(heroFacts, mode == 0 ? 1 : 3, new[] { 1.5, 1, 1.1 }); // первой ячейке больше места: имя сервера, флаг и плашка в одну строку
            DashboardReflow(services, mode < 2 ? 1 : 2);
            _dashboardHeroBadge.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            _dashboardModeGrid.Columns = mode == 0 ? 2 : 4;
            _dashboardSplitMore.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            Fit();
        };
        const double MinScale = .6, Gap = 8, MaxExtraGap = 24;
        // Свободная высота уходит в промежутки между карточками, а не в увеличение страницы: увеличение меняет ширину,
        // подписи переносятся иначе, высота снова меняется, и масштаб бесконечно прыгал туда-обратно
        void Fit()
        {
            double viewport = scroll.ActualHeight; // высота окна под страницу; ViewportHeight при изменении размера отстает на кадр
            if (viewport <= 0 || page.ActualHeight <= 0) return;
            // Высота без добавленных промежутков, в обычном масштабе (страница прижата к верху, ActualHeight настоящая)
            double contentH = page.ActualHeight - extraGap * gapped.Length + page.Margin.Top + page.Margin.Bottom;
            double want = Math.Clamp(viewport / contentH, MinScale, 1);
            // Рост масштаба только заметным шагом: мелкие колебания от переноса подписей не раскачивают страницу
            if (want < scale.ScaleX - .004 || want > scale.ScaleX + .02 || (want == 1 && scale.ScaleX != 1)) scale.ScaleX = scale.ScaleY = want;
            double extra = scale.ScaleX >= 1 ? Math.Floor(Math.Clamp((viewport - contentH) / gapped.Length, 0, MaxExtraGap)) : 0;
            if (extra == extraGap) return;
            extraGap = extra;
            foreach (var g in gapped) g.Margin = new Thickness(g.Margin.Left, Gap + extra, g.Margin.Right, g.Margin.Bottom);
        }
        scroll.SizeChanged += (_, _) => Fit();
        page.SizeChanged += (_, _) => Fit();
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

    private static DockPanel DashboardPanelHeader(string title, TextBlock? hint, params Button?[] actions)
    {
        var d = new DockPanel { Margin = new Thickness(2, 0, 0, 6), MinHeight = 40 }; // одна высота у заголовков соседних карточек
        bool first = true;
        foreach (var action in actions.OfType<Button>())
        {
            // Первая кнопка стоит у правого края, следующие левее, между ними промежуток
            action.Margin = new Thickness(0, 0, first ? 0 : 8, 0);
            first = false;
            DockPanel.SetDock(action, Dock.Right); d.Children.Add(action);
        }
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (hint != null) { hint.FontSize = 12; t.Children.Add(hint); }
        d.Children.Add(t);
        return d;
    }

    private static Border DashboardLogo(string resource)
    {
        var image = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        image.SetResourceReference(Image.SourceProperty, resource);
        return new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Child = image }.Ref(Border.BackgroundProperty, "Surface2");
    }

    /// <summary>Строка сервиса: значок, название и подпись, состояние с цветной точкой и пояснением, действие справа.</summary>
    private static Border DashboardRow(FrameworkElement symbol, string name, string sub, Ellipse dot, TextBlock status, TextBlock note, FrameworkElement action)
    {
        var g = new Grid { MinHeight = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Название фиксированной ширины, состояние сразу за ним: элементы одной строки читаются вместе
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        symbol.Margin = new Thickness(0, 0, 12, 0); symbol.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(symbol);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(Ui.Hint(sub, 1).With(t => { t.FontSize = 12.5; t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; }));
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var line = new StackPanel { Orientation = Orientation.Horizontal }; line.Children.Add(dot); line.Children.Add(status);
        // Подпись состояния всегда в одну строку: иначе длинная подпись растягивает ряд и три ряда слева не совпадают с тремя справа
        note.FontSize = 12; note.TextWrapping = TextWrapping.NoWrap; note.TextTrimming = TextTrimming.CharacterEllipsis;
        state.Children.Add(line); state.Children.Add(note);
        Grid.SetColumn(state, 2); g.Children.Add(state);
        action.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(action, 3); g.Children.Add(action);
        return new Border { Child = g, Padding = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(0, 1, 0, 0) }.Ref(Border.BorderBrushProperty, "Border");
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

    /// <summary>
    /// Пункт платного CHIMERION DNS в меню: золотая звезда, как у строки в таблице серверов и плашки на главной. Текст и фон
    /// как у остальных пунктов, чтобы выделение было заметным, но не кричащим. В светлой теме цвет темнее (ресурс «Promo»).
    /// </summary>
    internal static Button MarkPromo(Button nav)
    {
        if (nav.Content is StackPanel { Children.Count: > 0 } row && row.Children[0] is Controls.Icon star)
            star.SetResourceReference(Controls.Icon.ForegroundProperty, "Promo");
        return nav;
    }

    private static void DashboardReflow(Grid grid, int columns, double[]? weights = null)
    {
        grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights != null && weights.Length == columns ? weights[i] : 1, GridUnitType.Star) });
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

    /// <summary>Факт одной строкой: значок, подпись, значение и при желании короткое пояснение (в узком окне переносится).</summary>
    private static void DashboardFact(Grid grid, int index, string kind, string label, FrameworkElement value, TextBlock? hint)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Controls.Icon { Kind = kind, Size = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(Controls.Icon.ForegroundProperty, "Accent");
        row.Children.Add(icon);
        var line = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(Ui.Hint(label, 0).With(t => { t.FontSize = 13; t.Margin = new Thickness(0, 0, 8, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        value.Margin = new Thickness(0, 0, 8, 0); value.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(value);
        if (hint != null) { hint.FontSize = 12.5; hint.Margin = new Thickness(0); hint.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(hint); }
        Grid.SetColumn(line, 1); row.Children.Add(line);
        var card = new Border { Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(10), Child = row };
        card.SetResourceReference(Border.BackgroundProperty, "Surface"); Grid.SetColumn(card, index); grid.Children.Add(card);
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

        // Карточка защиты. Пока защита включается или выключается, карточка уже показывает, куда идет дело
        bool on = active != null;
        bool switching = _dashboardPowerTarget != null || _busyConn;
        bool goingOn = _dashboardPowerTarget ?? !on;
        bool settled = !switching;
        Ui.SwapText(_dashboardTitle, switching ? (goingOn ? "Включаю защиту…" : "Выключаю защиту…") : on ? "Защита активна" : "Защита выключена");
        Ui.SwapText(_dashboardDescription, switching ? (goingOn ? "Меняю DNS и проверяю, что интернет работает" : "Возвращаю DNS вашей сети")
            : !on ? "Включите, чтобы открыть ChatGPT, Claude и Gemini"
            : aiServices.Count == 0 ? "Проверяю доступ к нейросетям…"
            : open == aiServices.Count ? "Доступ к ИИ-сервисам настроен" : "Smart DNS подключен, но открываются не все нейросети", 4);
        var server = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        bool showPlace = on && settled && server != null && !string.IsNullOrEmpty(server.LocationCell);
        // Страна выхода: в первой строке карточки, рядом с состоянием защиты
        Ui.SwapText(_dashboardHeroMode, switching ? "Это занимает несколько секунд" : !on ? "Остальной интернет работает как обычно"
            : showPlace ? server!.LocationCell : active!.Split ? "Нейросети через Smart DNS, остальной трафик через обычный DNS" : "Весь трафик идет через Smart DNS", 3);
        _dashboardHeroMode.ToolTip = showPlace ? server!.LocationTooltip : null;
        bool partial = on && settled && aiServices.Count > 0 && open < aiServices.Count;
        if (partial)
        {
            var closed = aiServices.Where(x => !x.Access.StartsWith("✓")).Select(x => x.Name).ToList();
            _dashboardWarnText.Text = $"Открываются {open} из {aiServices.Count} нейросетей: {string.Join(", ", closed)} {(closed.Count == 1 ? "не открывается" : "не открываются")}";
        }
        _dashboardWarn.Visibility = partial ? Visibility.Visible : Visibility.Collapsed;
        DashboardHeroGlow(on && settled && !partial ? 1 : switching && goingOn || partial ? .4 : 0);
        Ui.FadeBrush(_dashboardHero, Border.BorderBrushProperty, partial ? "Warn" : on && settled ? "Ok" : switching ? "Accent" : "Border", 420);
        Ui.FadeBrush(_dashboardHeroIcon, Border.BackgroundProperty, on && settled ? "Ok" : "Surface2", 420);
        _dashboardHeroMark.Kind = on && settled ? "circle-check" : "shield-check";
        _dashboardHeroMark.SetResourceReference(Controls.Icon.ForegroundProperty, on && settled ? "OnToggle" : switching ? "Accent" : "Muted");
        DashboardSpinner(switching);
        if (settled && _dashboardShownOn != on)
        {
            if (_dashboardShownOn != null) { Ui.Pop(_dashboardHeroIcon, .7); if (on) DashboardRipple(); }
            _dashboardShownOn = on;
        }
        _dashboardSyncing = true;
        _dashboardPower.IsChecked = switching ? goingOn : on;
        _dashboardPower.IsHitTestVisible = settled && !_dashboardChecking; // занято: нажатия не принимаются, но вид не тускнеет
        _dashboardSyncing = false;
        Ui.SwapText(_dashboardPowerText, switching ? (goingOn ? "Включаю…" : "Выключаю…") : on ? "Включено" : "Выключено");
        Ui.FadeBrush(_dashboardPowerText, TextBlock.ForegroundProperty, on && settled ? "Ok" : "Text");

        Ui.SwapText(_dashboardSmartName, active == null ? "Не подключен" : PrivacyGuard.Mask(server?.ShortName ?? active.Name));
        // Страна самого DNS-сервера и страна, откуда нейросети видят запросы, могут различаться: подписываем обе
        _dashboardSmartHint.Text = server == null ? (on ? "" : "Включите защиту") : server.LocationCell;
        _dashboardSmartHint.ToolTip = server?.LocationTooltip;
        _dashboardSmartName.ToolTip = active == null ? null : string.Join(", ", active.Addresses);
        _dashboardSmartFlag.Source = server?.Flag;
        _dashboardSmartFlag.Visibility = server?.Flag == null ? Visibility.Collapsed : Visibility.Visible;
        bool works = on && aiServices.Count > 0 && open > 0;
        _dashboardSmartChipBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _dashboardSmartChip.Text = aiServices.Count == 0 ? "Проверка" : works ? "Работает" : "Не открывает";
        Ui.FadeBrush(_dashboardSmartChipBox, Border.BackgroundProperty, works ? "ChipOk" : aiServices.Count == 0 ? "Surface2" : "ChipBad");
        Ui.FadeBrush(_dashboardSmartChip, TextBlock.ForegroundProperty, works ? "Ok" : aiServices.Count == 0 ? "Muted" : "Bad");
        _dashboardPlainDns.Text = snap == null ? "Еще не проверялся" : PrivacyGuard.Mask(snap.SystemDns);
        _dashboardPlainHint.Text = active?.Split == true ? $"Общий DNS: {active.GeneralName}" : on ? "Тот же Smart DNS" : "DNS вашей сети";
        _dashboardPlainDns.ToolTip = _dashboardPlainHint.Text;
        Ui.SwapText(_dashboardUptime, on ? Uptime(DateTime.Now - active!.Since) : "Не подключено");
        _dashboardUptimeHint.Text = on ? $"с {active!.Since:dd.MM HH:mm}" : "нажмите переключатель";

        // Нейросети
        foreach (var (name, row) in _dashboardServiceRows)
        {
            var s = aiServices.FirstOrDefault(x => x.Name == name);
            bool ok = s?.Access.StartsWith("✓") == true;
            var color = s == null ? "Muted" : ok ? "Ok" : "Warn";
            bool probing = s == null && (_afterProbeRunning || _dashboardChecking);
            if (probing) color = "Accent";
            Ui.SwapText(row.Status, probing ? "Проверяю…" : s == null ? "Не проверялся" : ok ? "Доступен" : Cap(s.Access.TrimStart('✓', '✗', ' ')), 4);
            Ui.FadeBrush(row.Status, TextBlock.ForegroundProperty, color);
            Ui.FadeBrush(row.Dot, Shape.FillProperty, color);
            // В узкой колонке только страна: полная фраза переносилась в три строки и уходит в подсказку
            row.Note.Text = s == null ? "" : ok ? (s.Egress is { } eg ? GeoLocation.CountryName(eg.Country) : "")
                : ShortReason(s.Note);
            row.Note.ToolTip = ok && s?.Egress is { } egress ? "ИИ видят вас из страны: " + GeoLocation.CountryName(egress.Country) : s?.Note;
            row.Status.ToolTip = s?.Note;
            bool why = s != null && !ok;
            row.Action.Content = why ? "Подробнее" : "Открыть";
            row.Action.Tag = why ? "why" : null;
            row.Action.ToolTip = why ? "Проверить по шагам, почему не открывается" : row.Url;
            // Три кнопки «Открыть» подряд: для дикторов к действию добавляем название сервиса
            System.Windows.Automation.AutomationProperties.SetName(row.Action, Loc.T(why ? "Подробнее" : "Открыть") + " — " + name);
        }
        _dashboardChecked.Text = snap == null ? "Еще не проверялось" : $"Проверено в {snap.Taken:HH:mm}" + (DateTime.Now - snap.Taken > TimeSpan.FromMinutes(30) ? ", данные могли устареть" : "");
        DashboardCheckProgressState(_dashboardProbeProgress, _dashboardChecking);
        _dashboardProbe.IsHitTestVisible = !_dashboardChecking && !_busyConn;
        _dashboardProbe.Content = _dashboardChecking ? "Проверяю…" : "Проверить все";

        // Telegram и YouTube
        DashboardCheckProgressState(_dashboardSiteProgress, _dashboardSitesChecking);
        _dashboardSiteProbe.IsHitTestVisible = !_dashboardSitesChecking;
        _dashboardSiteProbe.Content = _dashboardSitesChecking ? "Проверяю…" : "Проверить сайты";
        _dashboardSiteYoutube.Text = DashboardSiteStatus("youtube.com");
        _dashboardSiteDiscord.Text = DashboardSiteStatus("discord.com");
        _dashboardSiteTelegram.Text = DashboardSiteStatus("t.me");
        RefreshToolSwitches();

        RefreshDashboardModes();
        RefreshDashboardSplit();
        RefreshDashboardZone();
        RefreshDashboardServers(active);
    }

    /// <summary>
    /// Переключатель Telegram или обхода: на главной и в компактном виде одно и то же действие и одно состояние.
    /// </summary>
    private void WireToolSwitch(Controls.Switch toggle, Func<Tool> getTool)
    {
        toggle.Click += async (_, _) =>
        {
            if (_dashboardSyncing) return;
            if (_dashboardToolBusy || _dashboardToolTarget != null) { RefreshToolSwitches(); return; }
            var t = getTool();
            _dashboardToolTarget = (t, toggle.IsChecked == true);
            RefreshToolSwitches(); RefreshDashboard();
            try { await DashboardToolAsync(t); }
            finally { _dashboardToolTarget = null; RefreshToolSwitches(); RefreshDashboard(); }
        };
    }

    /// <summary>Состояние переключателей программ (главная и компактный вид): включено, идет включение, запущено отдельно.</summary>
    private void RefreshToolSwitches()
    {
        _dashboardSyncing = true;
        foreach (var (toggle, status, dot, getTool) in _dashboardToolRows)
        {
            var tool = getTool();
            bool running = tool.IsRunning();
            bool foreign = tool is TgProxyTool tg && tg.ForeignCopyRunning() || tool is ZapretTool && running && !tool.State.Enabled;
            bool pending = _dashboardToolTarget is { } tt && tt.Tool == tool;
            bool want = pending ? _dashboardToolTarget!.Value.On : running;
            bool narrow = _compactToolSwitches.Contains(toggle); // компактный вид: ячейка узкая, состояние одним словом
            Ui.SwapText(status, pending ? (want ? "Включаю…" : "Выключаю…") : foreign ? (narrow ? "Отдельно" : "Запущен отдельно")
                : running ? (narrow ? "Работает" : tool is ZapretTool ? "Обход включен" : "Прокси включен") : tool.IsInstalled ? (narrow ? "Выключено" : "Выключен") : "Не установлен", 4);
            var color = pending ? "Accent" : running ? "Ok" : "Muted";
            Ui.FadeBrush(status, TextBlock.ForegroundProperty, color);
            Ui.FadeBrush(dot, Shape.FillProperty, color);
            toggle.IsChecked = want;
            toggle.IsEnabled = !foreign;
            toggle.IsHitTestVisible = !_dashboardToolBusy && _dashboardToolTarget == null;
            if (!narrow) toggle.ToolTip = foreign ? "Запущен другой программой: управляйте им там" : null;
        }
        _dashboardSyncing = false;
        _dashboardMessage.Text = _dashboardMessageText ?? "";
        _dashboardMessage.Visibility = string.IsNullOrEmpty(_dashboardMessageText) ? Visibility.Collapsed : Visibility.Visible;
        _dashboardMessage.SetResourceReference(TextBlock.ForegroundProperty, _dashboardMessageError ? "Bad" : "Muted");
    }

    private void DashboardHeroGlow(double to)
    {
        if (Math.Abs(_dashboardHeroGlow.Opacity - to) < .01 && !_dashboardHeroGlow.HasAnimatedProperties) return;
        if (_dashboardHeroGlow.Tag is double d && d == to) return;
        _dashboardHeroGlow.Tag = to;
        _dashboardHeroGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(to, Ui.Motion(600)) { EasingFunction = new QuadraticEase() });
    }

    /// <summary>Кольцо вокруг значка крутится, пока защита включается или выключается, потом плавно гаснет.</summary>
    private void DashboardSpinner(bool on)
    {
        if (on == _dashboardSpinning) return;
        _dashboardSpinning = on;
        if (on)
        {
            if (Ui.Animate) _dashboardSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(950)) { RepeatBehavior = RepeatBehavior.Forever });
            _dashboardSpinner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ui.Motion(200)));
        }
        else
        {
            var fade = new DoubleAnimation(0, Ui.Motion(350));
            fade.Completed += (_, _) => { if (!_dashboardSpinning) _dashboardSpin.BeginAnimation(RotateTransform.AngleProperty, null); };
            _dashboardSpinner.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>Защита включилась: от значка расходится мягкая волна.</summary>
    private void DashboardRipple()
    {
        if (!Ui.Animate) return;
        var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromMilliseconds(850)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _dashboardRippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _dashboardRippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _dashboardRipple.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 0, TimeSpan.FromMilliseconds(850)) { EasingFunction = new QuadraticEase() });
    }

    /// <summary>Первая фраза пояснения проверки, а если причины нет, честно: не установлена.</summary>
    private static string ShortReason(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "Причина не установлена";
        var t = Loc.T(note).Trim(); // сначала перевод целиком, потом обрезка: обрезанной строки нет в словаре
        var cut = t.IndexOfAny(new[] { '.', '\n', ';' });
        if (cut > 0) t = t[..cut];
        return t.Length > 80 ? t[..78] + "…" : t;
    }

    private static string Cap(string t) => t.Length == 0 ? t : char.ToUpper(t[0]) + t[1..];

    /// <summary>Три самых надежных рабочих сервера (тот, к которому подключены, всегда в списке). Перестраивается, только если список изменился.</summary>
    private void RefreshDashboardServers(DnsSwitcher.ActiveConnection? active)
    {
        var top = _servers.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && !s.IsJunk && Usable(s)).OrderBy(s => s.Rank).Take(3).ToList();
        if (active != null && _servers.FirstOrDefault(s => s.Id == active.ServerId) is { } cur && !top.Contains(cur)) { top.Insert(0, cur); if (top.Count > 3) top.RemoveAt(3); }
        if (_dashboardPickId != null && (top.All(s => s.Id != _dashboardPickId) || _dashboardPickId == active?.ServerId)) _dashboardPickId = null;
        var sig = string.Join("|", top.Select(s => $"{s.Id}:{s.LatencyMs}:{s.Stability}:{s.Id == active?.ServerId}:{s.Status}")) + _busyConn + _dashboardPickId;
        if (sig == _dashboardServersSignature) return;
        _dashboardServersSignature = sig;
        _dashboardServers.Children.Clear();
        if (top.Count == 0) { _dashboardServers.Children.Add(Ui.Hint("Рабочих серверов пока нет. Нажмите «Найти новые серверы»", 4)); return; }
        var head = DashboardServerGrid();
        foreach (var (t, c) in new[] { ("Сервер", 1), ("Страна", 2), ("Пинг", 3), ("Стабильность", 4), ("Статус", 5) })
        {
            var h = Ui.Hint(t, 0).With(x => x.FontSize = 12); Grid.SetColumn(h, c); head.Children.Add(h);
        }
        head.Margin = new Thickness(6, 0, 6, 4);
        _dashboardServers.Children.Add(head);
        foreach (var s in top)
        {
            bool used = s.Id == active?.ServerId, picked = s.Id == _dashboardPickId;
            var g = DashboardServerGrid();
            var radio = new Ellipse { Width = 14, Height = 14, StrokeThickness = used || picked ? 4 : 1.5, VerticalAlignment = VerticalAlignment.Center };
            radio.SetResourceReference(Shape.StrokeProperty, used || picked ? "Accent" : "ControlBorder");
            g.Children.Add(radio);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = PrivacyGuard.Mask(s.ShortName), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(Ui.Hint(s.Address, 0).With(x => { x.FontSize = 12; x.TextTrimming = TextTrimming.CharacterEllipsis; }));
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
            var target = s;
            if (picked)
            {
                // Подключение только явной кнопкой у выбранного сервера; смену страны выхода ConnectToAsync подтверждает риск-диалогом
                var connect = Ui.Action("Подключить", "Primary", () => { }).With(b => { b.Padding = new Thickness(12, 4, 12, 5); b.Margin = new Thickness(0); b.HorizontalAlignment = HorizontalAlignment.Left; b.IsEnabled = !_busyConn; });
                connect.Click += async (_, _) => { _dashboardPickId = null; await ConnectToAsync(target); _dashboardServersSignature = null; RefreshDashboard(); };
                Grid.SetColumn(connect, 5); g.Children.Add(connect);
            }
            else { Grid.SetColumn(chip, 5); g.Children.Add(chip); }
            var row = new Border { Child = g, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
                                   Cursor = used ? null : System.Windows.Input.Cursors.Hand, ToolTip = used ? "Сейчас подключено" : picked ? null : "Выбрать этот сервер" };
            if (used || picked) row.SetResourceReference(Border.BackgroundProperty, picked ? "AccentSoft" : "Surface2");
            else
            {
                row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "SurfaceHover");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => { _dashboardPickId = target.Id; RefreshDashboardServers(DnsSwitcher.GetActive()); };
            }
            _dashboardServers.Children.Add(row);
        }
    }

    private static Grid DashboardServerGrid()
    {
        var g = new Grid();
        foreach (var w in new[] { 30.0, -1.7, -1.3, -0.7, -0.9, -1 }) // ровно по ширине, без провала между названием и страной
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(-w, GridUnitType.Star) : new GridLength(w) });
        return g;
    }

    private string DashboardSiteStatus(string host)
    {
        // Состояние программы обхода показано строкой выше, здесь короткая подпись: что с сайтом и когда проверяли
        if (_dashboardSiteResults.TryGetValue(host, out var result)) return $"Сайт {DashboardSiteVerdict(result.Verdict)} · {ShortTime(result.Time)}";
        if (_sites.Sites.TryGetValue(host, out var site) && site.LastChecked is { } time) return $"Сайт {DashboardSiteVerdict(site.State)} · {ShortTime(time)}";
        return "Сайт не проверялся";
    }
    private static string ShortTime(DateTime t) => t.Date == DateTime.Today ? $"{t:HH:mm}" : $"{t:dd.MM}"; // точное время в подсказке не нужно: подпись должна влезать
    internal static string DashboardSiteVerdict(SiteWatch.Verdict verdict) => verdict switch
    {
        SiteWatch.Verdict.Ok => "открывается", SiteWatch.Verdict.Unknown => "без результата", _ => "не открывается"
    };

    private static ProgressBar DashboardCheckProgress() => new()
    {
        Height = 3, Margin = new Thickness(0, 0, 0, 3), Visibility = Visibility.Hidden, // место занято всегда: ряды соседних карточек не сдвигаются
        IsHitTestVisible = false, Value = 50
    };

    private static void DashboardCheckProgressState(ProgressBar bar, bool checking)
    {
        bar.Visibility = checking ? Visibility.Visible : Visibility.Hidden;
        bar.IsIndeterminate = checking && SystemParameters.ClientAreaAnimation;
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
            var results = await Task.WhenAll(hosts.Select(h => SiteWatch.CheckAsync(h, false, allowOutside: true, CancellationToken.None)));
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
        catch (Exception ex)
        {
            Log($"{tool.Title}: {ex.Message}"); DashboardMessage($"{tool.Title}: {ex.Message}", true);
            if (DashboardHost.Visibility != Visibility.Visible) ShowToast($"{tool.Title}: {ex.Message}", "Warn");
        }
        finally { _dashboardToolBusy = false; UpdateToolsTray(); RefreshDashboard(); }
    }
    private void ShowControlPanel() => ShowView(View.Panel);
    private void DashboardHome_Click(object sender, RoutedEventArgs e) { if (_compact) SetCompact(false); ShowView(View.Home); RefreshDashboard(); }
}
