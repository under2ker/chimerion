using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Как вас видят сервисы»: из какой страны и с какого адреса ChatGPT, Claude и Gemini видят вас сейчас,
/// какая страна для них привычная и что из этого грозит аккаунту. Данные берутся из последней проверки
/// подключения и истории стран (AccountSafety), окно само в сеть не ходит.
/// </summary>
internal sealed class AccountRiskWindow
{
    private static AccountRiskWindow? _open;
    private readonly Window _w;
    private readonly Func<(AccountRisk.Result Result, DateTime? Taken, string? Pinned)> _source;
    private readonly Action<string> _pin;
    private readonly StackPanel _body = new();
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal };

    public static void Show(Window? owner, Func<(AccountRisk.Result, DateTime?, string?)> source, Action<string> pin)
    {
        if (_open != null) { _open._w.Activate(); _open.Render(); return; }
        _open = new AccountRiskWindow(source, pin);
        _open._w.Show();
    }

    private AccountRiskWindow(Func<(AccountRisk.Result, DateTime?, string?)> source, Action<string> pin)
    {
        _source = source;
        _pin = pin;
        _w = DnexDialog.CreateWindow("Как вас видят сервисы", 640);
        _w.MaxHeight = Math.Min(860, Ui.WorkAreaOf(null).Height - 40);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Как вас видят сервисы",
            "Страна и адрес, с которыми ChatGPT, Claude и Gemini видят ваш вход. По последней проверке, без новых запросов", glyph: "shield-user"));

        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var footer = Ui.Footer(_left, close);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        _w.Content = root;
        Render();
    }

    private void Render()
    {
        var (r, taken, pinned) = _source();
        _body.Children.Clear();
        _left.Children.Clear();

        // Итог: одно слово цветом уровня и короткое пояснение
        var brush = !r.Known ? "Muted" : r.Level switch { AccountRisk.Level.Calm => "Ok", AccountRisk.Level.Attention => "Warn", _ => "Bad" };
        var verdict = new StackPanel();
        verdict.Children.Add(new TextBlock { Text = r.Known ? AccountRisk.Title(r.Level) : "Нет данных", FontSize = 20, FontWeight = FontWeights.SemiBold }
            .Ref(TextBlock.ForegroundProperty, brush));
        verdict.Children.Add(Ui.Hint(AccountRisk.Summary(r), 4, "Text"));
        if (taken is { } t) verdict.Children.Add(Ui.Hint($"Проверка подключения в {t:HH:mm}", 6));
        _body.Children.Add(Ui.Card(verdict));

        _body.Children.Add(Ui.Section("Сервисы", true));
        var table = new StackPanel();
        foreach (var s in r.Services) table.Children.Add(ServiceRow(s));
        _body.Children.Add(Ui.Card(table));

        if (r.Factors.Count > 0)
        {
            _body.Children.Add(Ui.Section("Что поправить"));
            var list = new StackPanel();
            foreach (var f in r.Factors) list.Children.Add(FactorRow(f));
            _body.Children.Add(Ui.Card(list));
        }

        _body.Children.Add(Ui.Hint("Что именно сервисы считают подозрительным, снаружи не знает никто. Чаще всего блокируют за частую смену страны " +
                                   "и за адреса из черных списков: за этим окно и следит", 2));

        // Одно действие, которое реально снижает риск: закрепить привычную страну, если она общая и еще не закреплена
        if (pinned == null && AccountRisk.CommonHome(r.Services) is { } home)
            _left.Children.Add(Ui.Action($"Закрепить страну: {GeoLocation.CountryName(home)}", "Strong", () => { _pin(home); Render(); }));
        else if (pinned != null)
            _left.Children.Add(Ui.Hint($"Страна закреплена: {GeoLocation.CountryName(pinned)}", 0).With(h => h.VerticalAlignment = VerticalAlignment.Center));
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
