using System.Windows;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Безопасность аккаунта»: короткая справка, как беречь аккаунт Claude. Слева советы, справа что можно
/// проверить прямо сейчас. Окно ничего не проверяет само и в сеть не ходит.
/// </summary>
internal static class AccountGuideWindow
{
    public const string Title = "Безопасность аккаунта";
    public const string ClaudeSettingsUrl = "https://claude.ai/settings";

    public const string Disclaimer = "CHIMERION помогает замечать смену страны подключения, но не может гарантировать защиту от блокировки. Правила проверки аккаунтов определяет сам Claude.";

    public static readonly (string Icon, string Title, string Text)[] Tips =
    {
        ("refresh-cw", "Старайтесь не менять сервер без необходимости",
            "Разные серверы могут открывать Claude через разные страны. Перед сменой CHIMERION предупредит, если заметит такую разницу."),
        ("triangle-alert", "Обращайте внимание на предупреждения",
            "Если программа не смогла определить страну или сообщает, что она изменилась, проверьте подключение, прежде чем продолжить работу."),
        ("smartphone", "Проверяйте входы в аккаунт",
            "В Claude откройте настройки аккаунта и посмотрите список активных сеансов. Если увидите незнакомое устройство, завершите его сеанс."),
        ("shield-check", "Защитите способ входа",
            "Используйте надежный пароль для почты или аккаунта, через который входите в Claude. Включите двухэтапную проверку, если она доступна."),
        ("globe", "Учитывайте правила Claude",
            "Сервис доступен пользователям, которые находятся в поддерживаемых странах. CHIMERION не может изменить это требование."),
    };

    /// <param name="openRisk">Открыть окно «Как вас видят сервисы».</param>
    /// <param name="openBrowser">Открыть окно «Проверка браузера».</param>
    public static void Show(Action openRisk, Action openBrowser) => DnexDialog.Present(Build(openRisk, openBrowser));

    internal static Window Build(Action openRisk, Action openBrowser)
    {
        var w = DnexDialog.CreateWindow(Title, Ui.Narrow + 40);
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 20), LastChildFill = true };
        var header = DnexDialog.Header(DialogKind.Info, Title, "Как беречь аккаунт Claude", glyph: "shield-check");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        var footer = Ui.Footer(null, close);
        footer.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var left = new StackPanel();
        left.Children.Add(Ui.Section("Как беречь аккаунт", first: true));
        left.Children.Add(TipsCard());

        var right = new StackPanel();
        right.Children.Add(Ui.Section("Проверить сейчас", first: true));
        right.Children.Add(CheckCard(w, openRisk, openBrowser));

        var scroll = Ui.Scroll(Ui.Columns(left, right, 820, 3, 2), Ui.WorkAreaOf(null).Height - 230);
        root.Children.Add(scroll);
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(scroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Background);
        w.Content = root;
        return w;
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

    /// <summary>Что можно сделать сразу: открыть окно стран и настройки Claude; внизу оговорка о гарантиях.</summary>
    private static Border CheckCard(Window w, Action openRisk, Action openBrowser)
    {
        var box = new StackPanel();
        box.Children.Add(new TextBlock { Text = "Из какой страны вас видят", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        box.Children.Add(Ui.Hint("Страна сейчас и привычная для ChatGPT, Claude и Gemini, по последней проверке подключения", 4));
        box.Children.Add(Ui.Action("Как вас видят сервисы", "Strong", openRisk)
            .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); }));

        box.Children.Add(new TextBlock { Text = "Этот браузер", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        box.Children.Add(Ui.Hint("Идет ли Claude в вашем браузере тем же путем, что проверяет программа", 4));
        box.Children.Add(Ui.Action("Проверить браузер", "Strong", openBrowser)
            .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); }));

        box.Children.Add(new TextBlock { Text = "Активные сеансы Claude", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0) });
        box.Children.Add(Ui.Hint("Список устройств, с которых выполнен вход, в настройках аккаунта Claude", 4));
        box.Children.Add(Ui.Action("Открыть настройки Claude", "Strong", () => AutoStart.OpenUrl(ClaudeSettingsUrl))
            .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 10, 0, 0); b.ToolTip = "Откроется в браузере"; }));

        box.Children.Add(Ui.Separator().With(s => s.Margin = new Thickness(0, 18, 0, 12)));
        box.Children.Add(new TextBlock { Text = Disclaimer, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Ref(TextBlock.ForegroundProperty, "Muted"));
        return Ui.Card(box).With(c => c.Padding = new Thickness(22));
    }
}
