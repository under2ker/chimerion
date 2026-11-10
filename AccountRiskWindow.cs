using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Страница «Аккаунты»: из какой страны и с какого адреса ChatGPT, Claude и Gemini видят вас сейчас, какая страна
/// для них привычная, что из этого грозит аккаунту и как его беречь. Справа то, что можно проверить сразу: браузер
/// и активные сеансы Claude. Данные берутся из последней проверки подключения и истории стран (AccountSafety),
/// страница сама в сеть не ходит. Раньше это были две страницы: «Как вас видят сервисы» и «Безопасность аккаунта».
/// </summary>
internal sealed class AccountRiskWindow
{
    public const string Title = "Аккаунты";
    public const string ClaudeSettingsUrl = "https://claude.ai/settings";
    public const string Disclaimer = "CHIMERION помогает замечать смену страны подключения, но не может гарантировать защиту от блокировки. Правила проверки аккаунтов определяет сам сервис.";

    private static AccountRiskWindow? _open;
    private readonly Window _w;
    private readonly Func<(AccountRisk.Result Result, Freshness Fresh, string? Pinned)> _source;
    private readonly Action<string> _pin;
    private readonly Action _openBrowser;
    private readonly StackPanel _state = new();

    /// <summary>Когда страну последний раз измерили при текущем подключении; NetChanged — после смены сети еще не мерили.</summary>
    public sealed record Freshness(DateTime? MeasuredAt, bool NetChanged);

    /// <summary>«Страна проверена 12 мин назад», «После смены сети страна еще не проверена» или null, если сказать нечего.</summary>
    public static string? FreshnessText(Freshness f, DateTime now)
    {
        if (f.MeasuredAt is { } at)
        {
            var min = (int)Math.Max(0, (now - at).TotalMinutes);
            return min < 1 ? "Страна проверена только что" : min < 60 ? $"Страна проверена {min} мин назад" : $"Страна проверена {min / 60} ч назад";
        }
        return f.NetChanged ? "После смены сети страна еще не проверена" : null;
    }

    /// <summary>Советы: значок, заголовок, пояснение. Сначала то, за чем следит программа, потом то, что делает человек.</summary>
    public static readonly (string Icon, string Title, string Text)[] Tips =
    {
        ("globe", "Одна страна",
            "Заходите в ChatGPT, Claude и Gemini из одной и той же страны и не меняйте сервер без необходимости. Частая смена страны чаще всего ведет к проверкам и блокировкам. Перед сменой CHIMERION предупредит, если заметит разницу."),
        ("circle-check", "Закрепите страну",
            "Тогда серверы с другой страной выхода не подключатся сами. Кнопка в карточке итога вверху или выбор страны в настройках."),
        ("shield-check", "Чистый адрес",
            "Адреса из черных списков вызывают проверки и капчи. Если в карточке сервиса отмечен такой адрес, подключите другой сервер."),
        ("triangle-alert", "Не пропускайте предупреждения",
            "Если программа не смогла определить страну или сообщает, что она изменилась, проверьте подключение, прежде чем продолжить работу."),
        ("smartphone", "Один вход, свои устройства",
            "Не входите в один аккаунт одновременно из разных стран: для сервиса это похоже на передачу доступа. Незнакомые сеансы завершайте в настройках аккаунта."),
        ("shield-user", "Защитите способ входа",
            "Надежный пароль для почты или аккаунта, через который входите, и двухэтапная проверка, если она доступна."),
    };

    public static void Show(Func<(AccountRisk.Result, Freshness, string?)> source, Action<string> pin, Action openBrowser)
    {
        if (_open != null) { DnexDialog.Present(_open._w); _open.Render(); return; }
        _open = new AccountRiskWindow(source, pin, openBrowser);
        DnexDialog.Present(_open._w);
    }

    private AccountRiskWindow(Func<(AccountRisk.Result, Freshness, string?)> source, Action<string> pin, Action openBrowser)
    {
        _source = source;
        _pin = pin;
        _openBrowser = openBrowser;
        _w = DnexDialog.CreateWindow(Title, Ui.Narrow + 40);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };

        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20), LastChildFill = true };
        var header = DnexDialog.Header(DialogKind.Info, Title,
            "Как ChatGPT, Claude и Gemini видят ваш вход и как беречь аккаунт. По последней проверке, без новых запросов", glyph: "shield-user");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var footer = Ui.Footer(null, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Сверху состояние на всю ширину (итог, сервисы, что поправить), ниже советы и проверки в две колонки
        var body = new StackPanel();
        body.Children.Add(_state);
        var tips = new StackPanel();
        tips.Children.Add(Ui.Section("Как беречь аккаунт"));
        tips.Children.Add(TipsGrid());
        var checks = new StackPanel();
        checks.Children.Add(Ui.Section("Проверить сейчас"));
        checks.Children.Add(CheckCard());
        body.Children.Add(Ui.Columns(tips, checks, 900, 3, 2));

        var scroll = Ui.Scroll(body, Ui.WorkAreaOf(null).Height - 230);
        scroll.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(scroll);
        _w.Loaded += (_, _) => _w.Dispatcher.BeginInvoke(scroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Background);
        _w.Content = root;
        Render();
    }

    private static string LevelBrush(AccountRisk.Result r) =>
        !r.Known ? "Muted" : r.Level switch { AccountRisk.Level.Calm => "Ok", AccountRisk.Level.Attention => "Warn", _ => "Bad" };

    /// <summary>Состояние сейчас: карточка итога с закреплением страны, карточки сервисов, что поправить.</summary>
    private void Render()
    {
        var (r, fresh, pinned) = _source();
        _state.Children.Clear();
        _state.Children.Add(Verdict(r, fresh, pinned));

        _state.Children.Add(Ui.Section("Как вас видят сервисы"));
        var cards = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -12, 0) };
        cards.SizeChanged += (_, e) => cards.Columns = e.NewSize.Width < 720 ? 1 : 3;
        for (int i = 0; i < r.Services.Count; i++)
            cards.Children.Add(ServiceCard(r.Services[i]).With(c => c.Margin = new Thickness(0, 0, 12, 12)));
        _state.Children.Add(cards);

        if (r.Factors.Count > 0)
        {
            _state.Children.Add(Ui.Section("Что поправить"));
            var list = new StackPanel();
            for (int i = 0; i < r.Factors.Count; i++)
            {
                if (i > 0) list.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 12, 0, 12)));
                list.Children.Add(FactorRow(r.Factors[i]));
            }
            _state.Children.Add(Ui.Card(list).With(c => c.Padding = new Thickness(20, 16, 20, 16)));
        }
    }

    /// <summary>
    /// Итог: круглый значок цвета уровня, слово и пояснение, когда мерили. Справа единственное действие, которое
    /// реально снижает риск: закрепить привычную страну (или пометка, что она уже закреплена).
    /// </summary>
    private FrameworkElement Verdict(AccountRisk.Result r, Freshness fresh, string? pinned)
    {
        var brush = LevelBrush(r);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center,
                                 Child = new Controls.Icon { Kind = r.Known && r.Level != AccountRisk.Level.Calm ? "triangle-alert" : "shield-check", Size = 24,
                                                             HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                     .Ref(Controls.Icon.ForegroundProperty, brush) }
            .Ref(Border.BackgroundProperty, "Surface2");
        g.Children.Add(badge);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Known ? AccountRisk.Title(r.Level) : "Нет данных", FontSize = 22, FontWeight = FontWeights.SemiBold }
            .Ref(TextBlock.ForegroundProperty, brush));
        text.Children.Add(Ui.Hint(AccountRisk.Summary(r), 4, "Text"));
        if (FreshnessText(fresh, DateTime.Now) is { } when) text.Children.Add(Ui.Hint(when, 4).With(t => t.FontSize = 12));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        FrameworkElement? action = null;
        if (pinned != null)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Children.Add(new Controls.Icon { Kind = "circle-check", Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }
                .Ref(Controls.Icon.ForegroundProperty, "Ok"));
            chip.Children.Add(new TextBlock { Text = $"Страна закреплена: {GeoLocation.CountryName(pinned)}", VerticalAlignment = VerticalAlignment.Center });
            action = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 14, 8), Child = chip }.Ref(Border.BackgroundProperty, "Surface2");
        }
        else if (AccountRisk.CommonHome(r.Services) is { } home)
            action = Ui.Action($"Закрепить страну: {GeoLocation.CountryName(home)}", "Strong", () => { _pin(home); Render(); })
                .With(b => b.ToolTip = "Серверы с другой страной выхода не подключатся сами");
        if (action != null)
        {
            action.VerticalAlignment = VerticalAlignment.Center;
            action.Margin = new Thickness(18, 0, 0, 0);
            Grid.SetColumn(action, 2);
            g.Children.Add(action);
        }
        return Ui.Card(g, bottom: 0).With(c => { c.Padding = new Thickness(20, 18, 20, 18); c.SetResourceReference(Border.BorderBrushProperty, r.Known ? brush : "Border"); });
    }

    /// <summary>Карточка сервиса: логотип и название, метка «как обычно», страна крупно, привычная страна, история и адрес.</summary>
    private static Border ServiceCard(AccountRisk.ServiceView s)
    {
        var (mark, markBrush) = s.Country == null ? ("не через Smart DNS", "Muted")
            : s.Home == null ? ("привычной пока нет", "Muted")
            : string.Equals(s.Country, s.Home, StringComparison.OrdinalIgnoreCase) ? ("как обычно", "Ok") : ("не как обычно", "Warn");
        if (s.Rep?.Bad == true) (mark, markBrush) = ("адрес в черном списке", "Bad");

        var box = new StackPanel();
        var head = new DockPanel();
        var logo = new Controls.Icon { Kind = s.Service switch { "ChatGPT" => "openai", "Claude" => "claude", "Gemini" => "gemini", _ => "globe" }, Size = 18,
                                       Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center,
                                Child = new TextBlock { Text = mark, FontSize = 11, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, markBrush) }
            .Ref(Border.BackgroundProperty, "Surface2");
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = s.Service, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(head);

        box.Children.Add(new TextBlock { Text = s.Country == null ? "Не проверено" : GeoLocation.CountryName(s.Country), FontSize = 19, FontWeight = FontWeights.SemiBold,
                                         Margin = new Thickness(0, 14, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }
            .Ref(TextBlock.ForegroundProperty, s.Country == null ? "Muted" : "Text"));
        box.Children.Add(Ui.Hint(s.Country == null ? "Сервис сейчас идет мимо Smart DNS или еще не проверен" : "страна, из которой сервис видит вас сейчас", 2).With(t => t.FontSize = 11.5));

        box.Children.Add(Ui.Separator().With(x => x.Margin = new Thickness(0, 12, 0, 10)));
        void Line(string label, string value, string brush = "Text")
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var l = new TextBlock { Text = label, FontSize = 12, Width = 82 }.Ref(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            row.Children.Add(new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, brush));
            box.Children.Add(row);
        }
        Line("Обычно", s.Home == null ? "пока не набралось" : GeoLocation.CountryName(s.Home), s.Home == null ? "Muted" : "Text");
        Line($"{AccountSafety.RecentDays} дней", s.Recent.Count == 0 ? "нет данных" : string.Join(", ", s.Recent.Select(GeoLocation.CountryName)),
             s.Recent.Count > 1 ? "Warn" : s.Recent.Count == 0 ? "Muted" : "Text");
        Line("Адрес", s.Rep == null ? "не проверялся" : IpReputation.Describe(s.Rep), s.Rep == null ? "Muted" : s.Rep.Bad ? "Bad" : "Text");
        return Ui.Card(box, bottom: 0).With(c => c.Padding = new Thickness(18, 16, 18, 16));
    }

    /// <summary>Советы плитками в две колонки: значок, заголовок, пояснение.</summary>
    private static FrameworkElement TipsGrid()
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -12, 0) };
        grid.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width < 520 ? 1 : 2;
        for (int i = 0; i < Tips.Length; i++)
        {
            var (icon, title, text) = Tips[i];
            var row = new DockPanel();
            var glyph = new Controls.Icon { Kind = icon, Size = 18, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top }
                .Ref(Controls.Icon.ForegroundProperty, "Accent");
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(Ui.Hint(text, 4).With(t => t.FontSize = 12));
            row.Children.Add(texts);
            grid.Children.Add(Ui.Card(row, bottom: 12).With(c => { c.Padding = new Thickness(16, 14, 16, 14); c.Margin = new Thickness(0, 0, 12, 12); }));
        }
        return grid;
    }

    /// <summary>Что можно проверить сразу: браузер и активные сеансы Claude; внизу оговорка о гарантиях.</summary>
    private Border CheckCard()
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = "Этот браузер", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(Ui.Hint("Идет ли Claude в вашем браузере тем же путем, что проверяет программа", 4));
        box.Children.Add(Ui.Action("Проверить браузер", "Strong", _openBrowser)
            .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); }));

        box.Children.Add(new TextBlock { Text = "Активные сеансы Claude", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        box.Children.Add(Ui.Hint("Список устройств, с которых выполнен вход, в настройках аккаунта Claude", 4));
        box.Children.Add(Ui.Action("Открыть настройки Claude", "Strong", () => AutoStart.OpenUrl(ClaudeSettingsUrl))
            .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); b.ToolTip = "Откроется в браузере"; }));

        box.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 18, 0, 12)));
        box.Children.Add(new TextBlock { Text = "Что именно сервисы считают подозрительным, снаружи не знает никто. Чаще всего блокируют за частую смену страны и за адреса из черных списков: за этим страница и следит.",
                                         FontSize = 12, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted"));
        box.Children.Add(new TextBlock { Text = Disclaimer, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }.Ref(TextBlock.ForegroundProperty, "Muted"));
        return Ui.Card(box).With(c => c.Padding = new Thickness(22));
    }

    private static FrameworkElement FactorRow(AccountRisk.Factor f)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Controls.Icon { Kind = "triangle-alert", Size = 16, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top }
            .Ref(Controls.Icon.ForegroundProperty, f.Level == AccountRisk.Level.Risky ? "Bad" : "Warn"));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = f.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Hint(f.Advice, 2));
        Grid.SetColumn(text, 1); g.Children.Add(text);
        return g;
    }
}
