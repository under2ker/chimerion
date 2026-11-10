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
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal };

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
            "Тогда серверы с другой страной выхода не подключатся сами. Кнопка внизу или выбор страны в настройках."),
        ("shield-check", "Чистый адрес",
            "Адреса из черных списков вызывают проверки и капчи. Если в «Сервисах» отмечен такой адрес, подключите другой сервер."),
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
        var footer = Ui.Footer(_left, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var left = new StackPanel();
        left.Children.Add(_state);
        left.Children.Add(Ui.Section("Как беречь аккаунт"));
        left.Children.Add(TipsCard());

        var right = new StackPanel();
        right.Children.Add(Ui.Section("Проверить сейчас", first: true));
        right.Children.Add(CheckCard());

        var scroll = Ui.Scroll(Ui.Columns(left, right, 820, 3, 2), Ui.WorkAreaOf(null).Height - 230);
        scroll.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(scroll);
        _w.Loaded += (_, _) => _w.Dispatcher.BeginInvoke(scroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Background);
        _w.Content = root;
        Render();
    }

    /// <summary>Состояние сейчас (итог, сервисы, что поправить) и кнопка закрепления страны внизу.</summary>
    private void Render()
    {
        var (r, fresh, pinned) = _source();
        _state.Children.Clear();
        _left.Children.Clear();

        // Итог: одно слово цветом уровня и короткое пояснение
        var brush = !r.Known ? "Muted" : r.Level switch { AccountRisk.Level.Calm => "Ok", AccountRisk.Level.Attention => "Warn", _ => "Bad" };
        var verdict = new StackPanel();
        verdict.Children.Add(new TextBlock { Text = r.Known ? AccountRisk.Title(r.Level) : "Нет данных", FontSize = 20, FontWeight = FontWeights.SemiBold }
            .Ref(TextBlock.ForegroundProperty, brush));
        verdict.Children.Add(Ui.Hint(AccountRisk.Summary(r), 4, "Text"));
        if (FreshnessText(fresh, DateTime.Now) is { } when) verdict.Children.Add(Ui.Hint(when, 6));
        _state.Children.Add(Ui.Card(verdict));

        _state.Children.Add(Ui.Section("Сервисы"));
        var table = new StackPanel();
        foreach (var s in r.Services) table.Children.Add(ServiceRow(s));
        _state.Children.Add(Ui.Card(table));

        if (r.Factors.Count > 0)
        {
            _state.Children.Add(Ui.Section("Что поправить"));
            var list = new StackPanel();
            foreach (var f in r.Factors) list.Children.Add(FactorRow(f));
            _state.Children.Add(Ui.Card(list));
        }

        // Одно действие, которое реально снижает риск: закрепить привычную страну, если она общая и еще не закреплена
        if (pinned == null && AccountRisk.CommonHome(r.Services) is { } home)
            _left.Children.Add(Ui.Action($"Закрепить страну: {GeoLocation.CountryName(home)}", "Strong", () => { _pin(home); Render(); }));
        else if (pinned != null)
            _left.Children.Add(Ui.Hint($"Страна закреплена: {GeoLocation.CountryName(pinned)}", 0).With(h => h.VerticalAlignment = VerticalAlignment.Center));
    }

    /// <summary>Советы списком: значок, заголовок и пояснение, между пунктами тонкая черта.</summary>
    private static Border TipsCard()
    {
        var list = new StackPanel();
        for (int i = 0; i < Tips.Length; i++)
        {
            var (icon, title, text) = Tips[i];
            if (i > 0) list.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 14, 0, 14)));
            var row = new DockPanel();
            var glyph = new Controls.Icon { Kind = icon, Size = 18, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top }
                .Ref(Controls.Icon.ForegroundProperty, "Accent");
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(Ui.Hint(text, 4));
            row.Children.Add(texts);
            list.Children.Add(row);
        }
        return Ui.Card(list).With(c => c.Padding = new Thickness(22));
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

    private static FrameworkElement ServiceRow(AccountRisk.ServiceView s)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = s.Service, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        var info = new StackPanel();
        string now = s.Country == null ? "сейчас не через Smart DNS" : $"сейчас {GeoLocation.CountryName(s.Country)}";
        string usual = s.Home == null ? "привычной страны пока нет" : $"обычно {GeoLocation.CountryName(s.Home)}";
        info.Children.Add(new TextBlock { Text = $"{now} · {usual}", FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        var more = new List<string>();
        if (s.Recent.Count > 0) more.Add($"за {AccountSafety.RecentDays} дней: {string.Join(", ", s.Recent.Select(GeoLocation.CountryName))}");
        if (s.Rep != null) more.Add("адрес: " + IpReputation.Describe(s.Rep));
        if (more.Count > 0) info.Children.Add(Ui.Hint(string.Join(" · ", more), 2));
        Grid.SetColumn(info, 1); g.Children.Add(info);
        return g;
    }

    private static FrameworkElement FactorRow(AccountRisk.Factor f)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 10) };
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
