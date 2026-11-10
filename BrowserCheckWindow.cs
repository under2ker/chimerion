using System.Windows;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Проверка браузера»: идет ли Claude в браузере человека тем же путем, что проверяет CHIMERION. Успешная проверка
/// программы этого не доказывает: у браузера бывают свой DNS, прокси или VPN-расширение. Окно показывает, что видно
/// в настройках браузеров, и дает проверить сам браузер служебной страницей Cloudflare на claude.ai: она пишет страну,
/// из которой пришел запрос (loc=). Вход в аккаунт и переписка для этого не нужны. Программа страницу не читает:
/// браузер ее результат никому не отдает, сравнивает человек.
/// </summary>
internal static class BrowserCheckWindow
{
    public const string Title = "Проверка браузера";
    public const string TraceUrl = "https://claude.ai/cdn-cgi/trace";

    /// <summary>Что показать про ожидаемую страну. expected — страна Claude по свежему измерению программы.</summary>
    public static string ExpectedText(bool connected, string? expected) =>
        !connected ? "CHIMERION сейчас не подключен: страница покажет вашу настоящую страну"
        : expected == null ? "Страну Claude при этом подключении программа еще не измерила. Если на странице ваша настоящая страна, браузер идет мимо CHIMERION"
        : $"Через CHIMERION Claude видит вас из страны: {GeoLocation.CountryName(expected)} (loc={expected})";

    /// <summary>Как читать результат на странице.</summary>
    public static string VerdictText(bool connected, string? expected) =>
        connected && expected != null
            ? $"loc={expected}: этот браузер идет через CHIMERION. Другая страна: браузер использует другой путь (свой DNS, VPN или прокси-расширение)"
            : "Найдите строку loc=: это страна, из которой Claude видит этот браузер";

    /// <param name="state">Подключено ли и страна Claude по свежему измерению (null — не измерена).</param>
    public static void Show(Func<(bool Connected, string? Expected)> state) => DnexDialog.Present(Build(state));

    internal static Window Build(Func<(bool Connected, string? Expected)> state)
    {
        var w = DnexDialog.CreateWindow(Title, Ui.Narrow + 40);
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20), LastChildFill = true };
        var header = DnexDialog.Header(DialogKind.Info, Title, "Идет ли Claude в вашем браузере через CHIMERION", glyph: "globe");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        var footer = Ui.Footer(null, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var (connected, expected) = state();
        // Слева главное действие (проверка в браузере), справа то, что видно в настройках
        var left = new StackPanel();
        left.Children.Add(Ui.Section("Проверка в браузере", first: true));
        left.Children.Add(CheckCard(connected, expected));
        var right = new StackPanel();
        right.Children.Add(Ui.Section("Настройки браузеров", first: true));
        right.Children.Add(SettingsCard());

        var scroll = Ui.Scroll(Ui.Columns(left, right, 820, 3, 2), Ui.WorkAreaOf(null).Height - 230);
        root.Children.Add(scroll);
        w.Content = root;
        return w;
    }

    /// <summary>Свой DNS и прокси в браузерах по их файлам настроек: только чтение, без запросов в сеть.</summary>
    private static Border SettingsCard()
    {
        var box = new StackPanel();
        var issues = SystemCheck.BrowserIssues();
        if (issues.Count == 0)
            box.Children.Add(Row("circle-check", "Ok", "Своего DNS и прокси не найдено",
                "Chrome, Edge, Яндекс Браузер, Brave и Firefox берут адреса у Windows, то есть у CHIMERION. VPN- и прокси-расширения по настройкам не видны: их покажет проверка слева"));
        for (int i = 0; i < issues.Count; i++)
        {
            if (i > 0) box.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 12, 0, 12)));
            box.Children.Add(Row("triangle-alert", "Warn", issues[i].Title, issues[i].Fix));
        }
        return Ui.Card(box).With(c => c.Padding = new Thickness(22));
    }

    private static DockPanel Row(string icon, string brush, string title, string text)
    {
        var row = new DockPanel();
        var glyph = new Controls.Icon { Kind = icon, Size = 18, Margin = new Thickness(0, 1, 14, 0), VerticalAlignment = VerticalAlignment.Top }
            .Ref(Controls.Icon.ForegroundProperty, brush);
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Ui.Hint(text, 4));
        row.Children.Add(texts);
        return row;
    }

    private static Border CheckCard(bool connected, string? expected)
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = ExpectedText(connected, expected), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(Ui.Hint("1. Откройте страницу в том браузере, где пользуетесь Claude. Если это не браузер по умолчанию, скопируйте ссылку", 10));
        box.Children.Add(Ui.Hint("2. " + VerdictText(connected, expected), 6));

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Ui.Action("Открыть в браузере", "Strong", () => AutoStart.OpenUrl(TraceUrl))
            .With(b => { b.Margin = new Thickness(0, 0, 10, 0); b.ToolTip = TraceUrl; }));
        buttons.Children.Add(Ui.Action("Скопировать ссылку", "Ghost", () =>
        {
            try { Clipboard.SetText(TraceUrl); } catch (Exception) { /* буфер занят другой программой */ }
        }));
        box.Children.Add(buttons);

        box.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 18, 0, 12)));
        box.Children.Add(new TextBlock
        {
            Text = "Это служебная страница Cloudflare на claude.ai: вход в аккаунт не нужен, переписка не затрагивается. Проверка подтверждает только браузер: приложение Claude и Claude Code подключаются к своим адресам.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        }.Ref(TextBlock.ForegroundProperty, "Muted"));
        return Ui.Card(box).With(c => c.Padding = new Thickness(22));
    }
}
