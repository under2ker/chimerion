using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Вопрос при выходе, когда что-то работает: подключение DNS, обход zapret, ускорение Telegram. Два варианта
/// карточками: «Выйти и оставить работать» (рекомендуем, Enter) и «Выключить все и выйти»; «Отмена» обычной
/// кнопкой внизу, Esc и закрытие окна тоже отмена. Системное MessageBox делало бы «Да» кнопкой по умолчанию:
/// лишний Enter выключал бы все.
/// </summary>
internal static class ClosePrompt
{
    public static MessageBoxResult Ask(string connectionName) => Ask(connectionName, Array.Empty<string>());

    /// <param name="connectionName">Активное подключение DNS или null.</param>
    /// <param name="tools">Работающие инструменты CHIMERION («обход zapret», «ускорение Telegram»).</param>
    public static MessageBoxResult Ask(string? connectionName, IReadOnlyList<string> tools)
    {
        var result = MessageBoxResult.Cancel;
        var w = DnexDialog.CreateWindow("Выход", Ui.Narrow + 40);
        var root = Ui.Root();

        // Что сейчас работает: коротко, без технических адресов
        var parts = new List<string>();
        if (connectionName != null) parts.Add($"Smart DNS ({connectionName})");
        parts.AddRange(tools);
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Что сделать при выходе?", "Сейчас работает: " + string.Join(", ", parts), glyph: "shield-check"));

        bool dns = connectionName != null;
        var keepHint = dns
            ? "Нейросети продолжат открываться. При следующем запуске CHIMERION продолжит с этим подключением и снова будет за ним следить"
            : "Все продолжит работать. При следующем запуске CHIMERION снова будет за этим следить";
        var keep = Option("circle-check", "Выйти и оставить работать", keepHint, "Ok", recommended: true, () => { result = MessageBoxResult.No; w.Close(); });
        if (dns) keep.ToolTip = "Вернуть исходные DNS без программы можно файлом reset-dns.cmd рядом с CHIMERION";
        root.Children.Add(keep);

        var stop = new List<string>();
        if (dns) stop.Add("DNS вернется к исходным настройкам");
        if (tools.Count > 0) stop.Add(string.Join(" и ", tools) + (tools.Count > 1 ? " остановятся" : " остановится"));
        root.Children.Add(Option("x", dns && tools.Count == 0 ? "Отключиться и выйти" : "Выключить все и выйти",
            string.Join(", ", stop), "Bad", recommended: false, () => { result = MessageBoxResult.Yes; w.Close(); }));

        var cancel = Ui.Action("Отмена", "Ghost", () => w.Close()).With(b => b.IsCancel = true);
        var keys = Ui.Hint("Enter: оставить работать · Esc: отмена", 0).With(t => { t.FontSize = 11; t.VerticalAlignment = VerticalAlignment.Center; });
        var footer = Ui.Footer(keys, cancel);
        footer.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(footer);

        w.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = MessageBoxResult.No; w.Close(); e.Handled = true; } };
        w.Content = root;
        w.Loaded += (_, _) => keep.Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>Вариант карточкой: значок, название, пояснение в одну-две строки, метка «рекомендуем».</summary>
    private static Border Option(string icon, string title, string hint, string color, bool recommended, Action click)
    {
        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Child = new Controls.Icon { Kind = icon, Size = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, color),
        }.Ref(Border.BackgroundProperty, color == "Ok" ? "ChipOk" : "ChipBad");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            .Ref(TextBlock.ForegroundProperty, color == "Bad" ? "Bad" : "Text"));
        if (recommended) head.Children.Add(Ui.Badge());
        var text = new StackPanel();
        text.Children.Add(head);
        text.Children.Add(new TextBlock { Text = hint, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = .82 }
            .Ref(TextBlock.ForegroundProperty, "Text"));
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left); row.Children.Add(badge); row.Children.Add(text);
        var card = new Border
        {
            Child = row, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(recommended ? 1.5 : 1), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, title);
        void Paint()
        {
            bool hover = card.IsMouseOver || card.IsKeyboardFocused;
            Ui.FadeBrush(card, Border.BackgroundProperty, hover ? "SurfaceHover" : recommended ? "Surface2" : "Surface", 160);
            Ui.FadeBrush(card, Border.BorderBrushProperty, recommended ? "Accent" : hover ? "ControlBorder" : "Border", 160);
        }
        card.MouseEnter += (_, _) => Paint();
        card.MouseLeave += (_, _) => Paint();
        card.IsKeyboardFocusedChanged += (_, _) => Paint();
        card.MouseLeftButtonUp += (_, _) => click();
        card.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { click(); e.Handled = true; } };
        Paint();
        return card;
    }
}
