using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dnex;

/// <summary>
/// Окно «Помощь» с вкладками: «Не открывается» (проверка по шагам), «Под наблюдением» (сайты, которыми вы
/// пользуетесь) и «На телефоне» (Smart DNS на iPhone и Android). Страница вкладки создается при первом открытии.
/// </summary>
internal sealed class HelpWindow
{
    public enum Tab { NotOpening, Watched, Phone }

    private static HelpWindow? _open;
    private readonly Window _w;
    private readonly Func<FrameworkElement>[] _factories;
    private readonly FrameworkElement?[] _pages = new FrameworkElement?[3];
    private readonly List<(Border Box, Controls.Icon Icon, TextBlock Title, TextBlock Sub)> _tabs = new();
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly TranslateTransform _slide = new();
    private int _current = -1;

    /// <summary>factories: страницы вкладок по порядку Tab (создаются один раз, при первом выборе вкладки).</summary>
    public static void Show(Window? owner, Tab tab, Func<FrameworkElement>[] factories)
    {
        if (_open == null) { _open = new HelpWindow(owner, factories); _open._w.Show(); }
        else
        {
            if (_open._w.WindowState == WindowState.Minimized) _open._w.WindowState = WindowState.Normal;
            _open._w.Activate();
        }
        _open.Select((int)tab);
    }

    private HelpWindow(Window? owner, Func<FrameworkElement>[] factories)
    {
        _factories = factories;
        _w = DnexDialog.CreateWindow("Помощь", 860);
        // Высота по вкладке: короткая вкладка без пустоты снизу, длинная целиком, пока помещается на экран
        _w.SizeToContent = SizeToContent.Height;
        _w.MaxHeight = Math.Min(980, SystemParameters.WorkArea.Height - 40); // длинный список сайтов прокручивается, а не растягивает окно на весь экран
        _w.ResizeMode = ResizeMode.CanMinimize;
        _w.SizeChanged += (_, e) => KeepCenter(e);
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        Action repaint = () => _w.Dispatcher.Invoke(() => { for (int k = 0; k < _tabs.Count; k++) Paint(k); }); // смена темы: плитки вкладок
        Services.ThemeColors.Changed += repaint;
        _w.Closed += (_, _) => { _open = null; HelpWizard.Forget(); SitesWindow.Forget(); PhoneWindow.Forget(); Services.ThemeColors.Changed -= repaint; };
        _w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _w.Close();
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3) { Select(e.Key - Key.D1); e.Handled = true; }
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Помощь", "Что делать, если что-то не открывается, и как поставить Smart DNS на телефон", glyph: "circle-help"));

        // Вкладки крупными плитками: иконка, название и о чем вкладка
        var tabs = new Grid { Margin = new Thickness(0, 4, 0, 14) };
        var items = new (string Icon, string Title, string Sub)[]
        {
            ("stethoscope", "Не открывается", "Проверка по шагам и что сделать"),
            ("globe", "Под наблюдением", "Сайты, которыми вы пользуетесь"),
            ("smartphone", "На телефоне", "Smart DNS на iPhone и Android"),
        };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            var (box, icon, title, sub) = TabTile(items[i].Icon, items[i].Title, items[i].Sub);
            int index = i;
            box.MouseLeftButtonUp += (_, _) => Select(index);
            box.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Select(index); e.Handled = true; } };
            box.MouseEnter += (_, _) => Paint(index);
            box.MouseLeave += (_, _) => Paint(index);
            box.ToolTip = $"Ctrl+{i + 1}";
            Grid.SetColumn(box, i * 2);
            tabs.Children.Add(box);
            _tabs.Add((box, icon, title, sub));
        }
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);

        _host.RenderTransform = _slide;
        var scroll = new ScrollViewer { Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);

        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var footer = Ui.Footer(Ui.Hint("Ctrl+1, Ctrl+2, Ctrl+3: переключение вкладок", 0).With(t => { t.FontSize = 11; t.VerticalAlignment = VerticalAlignment.Center; }), close);
        footer.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        _w.Content = root;
    }

    /// <summary>
    /// Высота сменилась вместе с вкладкой: окно растет и сжимается вокруг своего центра, а не только вниз (общий
    /// обработчик окон потом удерживает его в пределах экрана).
    /// </summary>
    private void KeepCenter(SizeChangedEventArgs e)
    {
        if (!_w.IsLoaded || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
        var area = SystemParameters.WorkArea;
        var top = _w.Top - (e.NewSize.Height - e.PreviousSize.Height) / 2;
        _w.Top = Math.Clamp(top, area.Top + 10, Math.Max(area.Top + 10, area.Bottom - 10 - e.NewSize.Height));
    }

    private static (Border, Controls.Icon, TextBlock, TextBlock) TabTile(string iconKind, string title, string sub)
    {
        var icon = new Controls.Icon { Kind = iconKind, Size = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold };
        var s = Ui.Hint(sub, 2).With(h => { h.FontSize = 11; h.TextTrimming = TextTrimming.CharacterEllipsis; h.TextWrapping = TextWrapping.NoWrap; });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(t); text.Children.Add(s);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(text);
        var box = new Border { Child = row, Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5),
                               Cursor = Cursors.Hand, Focusable = true, Background = Brushes.Transparent };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        return (box, icon, t, s);
    }

    private void Paint(int i)
    {
        var (box, icon, title, _) = _tabs[i];
        bool on = i == _current, hover = box.IsMouseOver;
        Ui.FadeBrush(box, Border.BackgroundProperty, on ? "AccentSoft" : hover ? "SurfaceHover" : "Surface", 220);
        Ui.FadeBrush(box, Border.BorderBrushProperty, on ? "Accent" : "Border", 220);
        Ui.FadeBrush(title, TextBlock.ForegroundProperty, on ? "Accent" : "Text", 220);
        icon.SetResourceReference(Controls.Icon.ForegroundProperty, on ? "Accent" : "Muted");
    }

    /// <summary>Переход на вкладку: страница создается при первом выборе, появляется сдвигом снизу.</summary>
    private void Select(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        if (i == _current) return;
        _current = i;
        for (int k = 0; k < _tabs.Count; k++) Paint(k);
        _pages[i] ??= _factories[i]();
        _host.Content = _pages[i];
        if (!Ui.Animate) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }
}
