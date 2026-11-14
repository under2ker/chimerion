using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно первого запуска: знак программы, один вопрос «что вам нужно» (четыре плитки) и три галочки о том, как запускаться.
/// Выбор применяет MainWindow.FirstRun.cs. «Пропустить» и закрытие окна ничего не меняют. Показывается один раз, при самом
/// первом запуске; позже его открывает команда палитры «Показать приветствие».
/// </summary>
internal static class FirstRunWindow
{
    /// <summary>Показать окно по центру владельца. null: человек пропустил или закрыл окно.</summary>
    public static FirstRunChoice? Ask(bool autoStart, bool closeToTray, bool hotkeys)
    {
        FirstRunChoice? result = null;
        var w = DnexDialog.CreateWindow("Добро пожаловать", 560);
        var root = Ui.Root();

        // ----- Шапка: знак и два предложения -----
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var mark = new Controls.BrandMark { Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(mark, Dock.Left); head.Children.Add(mark);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Добро пожаловать в CHIMERION", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(Ui.Hint("Один вопрос, и все заработает. Потом любую настройку можно поменять", 4).With(t => t.FontSize = 12.5));
        head.Children.Add(titles);
        root.Children.Add(head);

        // ----- Вопрос: что нужно -----
        root.Children.Add(Ui.Section("Что вам нужно?", first: true));
        var selected = FirstRun.Default;
        var tiles = new List<(FirstRun.Option Option, Controls.ClickTile Tile, Controls.Icon Icon)>();
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, -8, 0) }; // край плиток вровень с краем окна
        Button go = null!;
        void Paint()
        {
            foreach (var (o, tile, icon) in tiles)
            {
                bool on = o == selected;
                tile.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoft" : "Surface2");
                tile.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
                icon.SetResourceReference(Controls.Icon.ForegroundProperty, on ? "Accent" : "Muted");
            }
            if (go != null) go.Content = selected.ModeId == null ? "Начать" : "Включить и начать";
        }
        foreach (var o in FirstRun.Options)
        {
            var option = o;
            var icon = new Controls.Icon { Kind = o.Icon, Size = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = o.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Ui.Hint(o.Sub, 3).With(t => t.FontSize = 12));
            var line = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon); line.Children.Add(text);
            var tile = new Controls.ClickTile
            {
                Child = line, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 8),
            };
            AutomationProperties.SetName(tile, o.Title);
            tile.Click += (_, _) => { selected = option; Paint(); };
            grid.Children.Add(tile);
            tiles.Add((o, tile, icon));
        }
        root.Children.Add(grid);

        // ----- Как запускаться -----
        root.Children.Add(Ui.Section("Как запускаться"));
        var autoBox = new CheckBox { Content = "Запускать вместе с Windows (рекомендуем)", IsChecked = autoStart, Margin = new Thickness(0, 6, 0, 0) };
        var trayBox = new CheckBox { Content = "Крестик прячет программу в трей", IsChecked = closeToTray, Margin = new Thickness(0, 12, 0, 0) };
        var keysBox = new CheckBox { Content = "Сочетания клавиш из любой программы", IsChecked = hotkeys, Margin = new Thickness(0, 12, 0, 0) };
        var hint = (string t) => Ui.Hint(t, 3).With(x => x.Margin = new Thickness(26, 3, 0, 0));
        root.Children.Add(autoBox);
        root.Children.Add(hint("Стартует в трее без прав администратора, окно не мешает"));
        root.Children.Add(trayBox);
        root.Children.Add(hint("Подключение продолжает работать, программа открывается из трея"));
        root.Children.Add(keysBox);
        root.Children.Add(hint("Ctrl+Alt+D включает и выключает защиту, Ctrl+Alt+K открывает поиск"));

        // ----- Низ: пояснение и кнопки -----
        go = Ui.Action("Включить и начать", "Primary", () =>
        {
            result = new FirstRunChoice(selected.ModeId, autoBox.IsChecked == true, trayBox.IsChecked == true, keysBox.IsChecked == true);
            w.Close();
        });
        go.IsDefault = true;
        var skip = Ui.Action("Пропустить", "Ghost", () => w.Close());
        skip.IsCancel = true;
        root.Children.Add(Ui.Footer(Ui.Hint("Ctrl+K: поиск по всем действиям", 0), go, skip));
        Paint();

        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
