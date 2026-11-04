using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «История изменений сети»: что CHIMERION меняла в системе (DNS, правила «Двух DNS», IPv6, запасной сервер),
/// когда и почему. Новые записи сверху. Открывается из «Диагностики».
/// </summary>
internal sealed class NetHistoryWindow
{
    private static NetHistoryWindow? _open;
    private readonly Window _w;
    private readonly StackPanel _list = new();
    private readonly TextBlock _state = Ui.Hint("", 0);

    public static void Show(Window? owner)
    {
        if (_open != null) { _open._w.Activate(); _open.Render(); return; }
        _open = new NetHistoryWindow();
        _open._w.Show();
    }

    private NetHistoryWindow()
    {
        _w = DnexDialog.CreateWindow("История изменений сети", 760);
        _w.MaxHeight = Math.Min(900, Ui.WorkAreaOf(null).Height - 40);
        _w.Closed += (_, _) => _open = null;
        _w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) _w.Close(); };

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "История изменений сети",
            "Что программа меняла в настройках сети, когда и почему. Личных данных здесь нет", glyph: "scroll-text"));

        _state.Margin = new Thickness(2, 12, 0, 8);
        Grid.SetRow(_state, 1); root.Children.Add(_state);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var card = Ui.Card(scroll, bottom: 0);
        Grid.SetRow(card, 2); root.Children.Add(card);

        var copy = Ui.Action("Скопировать", "Strong", Copy).With(b => b.Margin = new Thickness(0, 0, 8, 0));
        var clear = Ui.Action("Очистить", "Ghost", () => { NetHistory.Clear(); Render(); });
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(copy); left.Children.Add(clear);
        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var footer = Ui.Footer(left, close);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        _w.Content = root;
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var items = NetHistory.Load();
        _state.Text = items.Count == 0
            ? "Пока записей нет: программа еще ничего не меняла в сети с этой версии"
            : $"Записей: {items.Count}. Показаны от новых к старым";
        string? day = null;
        foreach (var c in Enumerable.Reverse(items))
        {
            var d = c.At.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            if (d != day)
            {
                day = d;
                _list.Children.Add(Ui.Section(d, _list.Children.Count == 0));
            }
            _list.Children.Add(Row(c));
        }
    }

    private static FrameworkElement Row(NetChange c)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = c.At.ToString("HH:mm:ss"), FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(0, 2, 0, 0) }
            .Ref(TextBlock.ForegroundProperty, "Muted"));
        var body = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(new TextBlock { Text = c.What, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = c.Why, FontSize = 12 }.Ref(TextBlock.ForegroundProperty, "Accent") }.Ref(Border.BackgroundProperty, "AccentSoft"));
        body.Children.Add(head);
        if (c.Details.Length > 0)
            body.Children.Add(new TextBlock { Text = c.Details, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
                .Ref(TextBlock.ForegroundProperty, "Muted"));
        Grid.SetColumn(body, 1); g.Children.Add(body);
        return g;
    }

    private void Copy()
    {
        var text = NetHistory.AsText();
        _state.Text = text.Length == 0 ? "Копировать нечего" : Ui.CopyText(text)
            ? "История скопирована"
            : "Буфер обмена занят другой программой, попробуйте еще раз";
    }
}
