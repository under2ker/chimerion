using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>
/// Окно «Google Flow»: проводит через подключение расширения Google Flow Geo Bypass в браузере. Сам браузер расширение
/// не примет без человека, поэтому окно копирует нужный адрес и путь к папке, а подключение проверяет само, когда
/// человек возвращается в окно.
/// </summary>
internal static class FlowWindow
{
    public static void Show(Action<string> log) => Build(log).ShowDialog();

    internal static Window Build(Action<string> log)
    {
        var w = DnexDialog.CreateWindow("Google Flow", Ui.Narrow + 40);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Google Flow", "Расширение для браузера, с которым Flow открывается из России", glyph: "film"));
        root.Children.Add(Ui.Hint("Оно меняет в браузере один флаг проверки страны. IP и аккаунт остаются прежними, а генерацию Google может и не дать.", 0)
            .With(t => t.ToolTip = "Google Flow Geo Bypass, открытый код (MIT). Работает только на flow.google.com и ничего не отправляет в сеть. Если Google ограничивает доступ на своих серверах, расширение не поможет."));

        var browsers = FlowExtension.Find();
        var current = FlowExtension.Preferred(browsers);
        var body = new StackPanel();
        root.Children.Add(body);
        var status = Ui.Status();

        var open = Ui.Action("Открыть Flow", "Primary", () =>
        {
            // В выбранном браузере, а не в браузере по умолчанию; не получилось, тогда в браузере по умолчанию
            if (current == null || !FlowExtension.Launch(current, FlowExtension.FlowUrl)) AutoStart.OpenUrl(FlowExtension.FlowUrl);
        });
        Button remove = null!;
        remove = Ui.Action("Убрать", "Ghost", () => RemoveExtension(current, status, log, Render));
        var close = Ui.Action("Закрыть", "Ghost", w.Close).With(b => b.IsCancel = true);
        var credit = new TextBlock { Text = "Google Flow Geo Bypass, MIT", FontSize = 12, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = FlowExtension.SourceUrl }
            .Ref(TextBlock.ForegroundProperty, "Muted");
        credit.MouseLeftButtonUp += (_, _) => AutoStart.OpenUrl(FlowExtension.SourceUrl);
        root.Children.Add(Ui.Footer(credit, open, remove, close));
        w.Content = root;

        void Render()
        {
            body.Children.Clear();
            if (browsers.Count > 1)
            {
                body.Children.Add(Ui.Section("Браузер"));
                var bar = new WrapPanel();
                foreach (var b in browsers)
                    bar.Children.Add(Ui.Action(b.Name, b == current ? "Strong" : "Ghost", () => { current = b; Render(); }));
                body.Children.Add(bar);
            }
            var state = current == null ? FlowExtension.State.NotConnected : FlowExtension.StateIn(current);
            body.Children.Add(Ui.Section("Состояние", first: browsers.Count <= 1));
            body.Children.Add(StateCard(current, state));

            if (state != FlowExtension.State.Enabled)
            {
                var page = current?.ExtensionsPage ?? "chrome://extensions";
                body.Children.Add(Ui.Section("Как подключить"));
                body.Children.Add(Step(1, "Откройте страницу расширений", "Вставьте адрес в адресную строку браузера и нажмите Enter",
                    Ui.Action("Скопировать адрес", "Strong", () =>
                    {
                        if (!Ui.CopyText(page)) { Ui.SetStatus(status, "Не удалось скопировать: буфер обмена занят", true); return; }
                        if (current != null) FlowExtension.Launch(current);
                        Ui.SetStatus(status, $"Адрес {page} скопирован: вставьте его в строку браузера (Ctrl+V) и нажмите Enter");
                    })));
                body.Children.Add(Step(2, "Включите «Режим разработчика»", $"Переключатель {current?.DevModeWhere ?? "справа вверху"} на странице расширений", null));
                body.Children.Add(Step(3, "Нажмите «Загрузить распакованное»", "В окне выбора вставьте путь в поле «Папка» и нажмите «Выбор папки»",
                    Ui.Action("Скопировать путь", "Strong", () =>
                    {
                        try { FlowExtension.Prepare(); }
                        catch (Exception ex)
                        {
                            log($"Не удалось распаковать расширение Google Flow: {ex.Message}");
                            Ui.SetStatus(status, "Не удалось подготовить папку расширения, подробности в журнале", true);
                            return;
                        }
                        if (!Ui.CopyText(FlowExtension.Folder)) { Ui.SetStatus(status, "Не удалось скопировать: буфер обмена занят", true); return; }
                        Ui.SetStatus(status, "Путь к папке скопирован: вставьте его в окне выбора (Ctrl+V)");
                    })));
                body.Children.Add(Ui.Hint("Когда вернетесь сюда, CHIMERION сам проверит подключение.", 2));
            }
            body.Children.Add(status);
            remove.Visibility = FlowExtension.Prepared ? Visibility.Visible : Visibility.Collapsed;
        }

        // Человек подключает расширение в браузере и возвращается: тогда и проверяем, без фоновых опросов
        var shown = false;
        w.Activated += (_, _) => { if (shown) Render(); };
        w.ContentRendered += (_, _) => shown = true;
        Render();
        return w;
    }

    private static Border StateCard(FlowExtension.Browser? b, FlowExtension.State state)
    {
        var (icon, brush, title, sub) = b == null
            ? ("info", "Muted", "Браузер не найден", "Расширение работает в Chrome, Edge и Яндекс Браузере. Подключите его по шагам ниже")
            : state switch
            {
                FlowExtension.State.Enabled => ("circle-check", "Ok", $"{b.Name}: подключено", "Откройте Flow, расширение сработает само"),
                FlowExtension.State.Disabled => ("triangle-alert", "Warn", $"{b.Name}: отключено", "Включите его переключателем на странице расширений"),
                _ => ("info", "Muted", $"{b.Name}: не подключено", "Три шага ниже, это займет минуту"),
            };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Controls.Icon { Kind = icon, Size = 18, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top }
            .Ref(Controls.Icon.ForegroundProperty, brush));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Hint(sub, 2));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var card = Ui.Card(row, bottom: 0);
        card.Padding = new Thickness(16, 12, 16, 12);
        return card;
    }

    /// <summary>Шаг инструкции: номер, что сделать, пояснение и необязательная кнопка справа.</summary>
    private static Grid Step(int n, string title, string hint, Button? action)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = n.ToString(), FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, -1, 0, 0) }
            .Ref(TextBlock.ForegroundProperty, "Accent"));
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Ui.Hint(hint, 2));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (action != null)
        {
            action.Margin = new Thickness(12, 0, 0, 0);
            action.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(action, 2);
            g.Children.Add(action);
        }
        return g;
    }

    private static void RemoveExtension(FlowExtension.Browser? b, TextBlock status, Action<string> log, Action render)
    {
        var page = b?.ExtensionsPage ?? "chrome://extensions";
        if (!DnexDialog.Ask("Убрать расширение?",
                "Папка расширения будет удалена. В браузере на странице расширений нажмите «Удалить» у Google Flow Geo Bypass: адрес страницы скопирую.",
                "Убрать")) return;
        try { FlowExtension.Remove(); }
        catch (Exception ex)
        {
            log($"Не удалось удалить папку расширения Google Flow: {ex.Message}");
            Ui.SetStatus(status, "Папка занята, закройте браузер и попробуйте еще раз", true);
            return;
        }
        log("Расширение Google Flow убрано из CHIMERION.");
        render();
        if (Ui.CopyText(page)) Ui.SetStatus(status, $"Папка удалена. Адрес {page} скопирован: удалите расширение и в браузере");
        else Ui.SetStatus(status, "Папка удалена. Удалите расширение и в браузере, на странице расширений");
    }
}
