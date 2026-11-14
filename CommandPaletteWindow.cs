using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Палитра команд (Ctrl+K): поле поиска и список действий. Любое место программы и любая настройка за три нажатия:
/// набрать начало слова, стрелками выбрать, Enter. Окно без рамки, закрывается по Esc, по щелчку мимо и после выбора.
/// Список строк и их действия собирает главное окно (MainWindow.Palette.cs), здесь только показ и выбор.
/// </summary>
internal sealed class CommandPaletteWindow
{
    private static CommandPaletteWindow? _open;
    private readonly Window _w;
    private readonly IReadOnlyList<PaletteCommand> _all;
    private readonly TextBox _query;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private IReadOnlyList<PaletteCommand> _shown = Array.Empty<PaletteCommand>();
    private readonly List<Border> _rows = new();
    private int _index;
    private bool _closing;

    /// <summary>Открыть палитру или вернуть фокус уже открытой. owner: главное окно, если оно на экране.</summary>
    public static void Show(Window? owner, IReadOnlyList<PaletteCommand> commands)
    {
        if (_open != null) { _open._w.Activate(); _open._query.Focus(); return; }
        _open = new CommandPaletteWindow(owner, commands);
    }

    private CommandPaletteWindow(Window? owner, IReadOnlyList<PaletteCommand> commands)
    {
        _all = commands;
        _w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, SizeToContent = SizeToContent.Height, Width = 680, Topmost = owner == null,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13, UseLayoutRounding = true,
            Title = "CHIMERION · Поиск и команды",
        };
        TextOptions.SetTextFormattingMode(_w, TextFormattingMode.Display);
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
        {
            _w.Owner = owner;
            _w.WindowStartupLocation = WindowStartupLocation.Manual;
            _w.Left = owner.Left + (owner.ActualWidth - 712) / 2;
            _w.Top = owner.Top + Math.Min(110, owner.ActualHeight * .14);
        }
        else
        {
            var area = Ui.WorkAreaOf(null);
            _w.WindowStartupLocation = WindowStartupLocation.Manual;
            _w.Left = area.Left + (area.Width - 712) / 2;
            _w.Top = area.Top + area.Height * .16;
        }

        // Строка поиска
        _query = new TextBox
        {
            Style = null, // без рамки и подсветки из общего стиля полей: строка поиска сама часть карточки
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 16, VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6), 
        };
        _query.SetResourceReference(Control.ForegroundProperty, "Text");
        _query.SetResourceReference(TextBox.CaretBrushProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(_query, "Поиск и команды");
        var magnifier = new Controls.Icon { Kind = "search", Size = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(Controls.Icon.ForegroundProperty, "Muted");
        var hintKey = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center,
                                   Child = new TextBlock { Text = "Esc", FontSize = 11 }.Ref(TextBlock.ForegroundProperty, "Muted") }
            .Ref(Border.BackgroundProperty, "Surface2");
        var top = new DockPanel { Margin = new Thickness(18, 10, 14, 10) };
        DockPanel.SetDock(magnifier, Dock.Left); top.Children.Add(magnifier);
        DockPanel.SetDock(hintKey, Dock.Right); top.Children.Add(hintKey);
        top.Children.Add(_query);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 400, Content = _list };
        _empty = new TextBlock { Text = "Ничего не найдено. Попробуйте другое слово", Margin = new Thickness(20, 16, 20, 18), Visibility = Visibility.Collapsed }
            .Ref(TextBlock.ForegroundProperty, "Muted");
        var footer = new TextBlock { Text = "↑ ↓ выбрать    Enter выполнить    Esc закрыть", FontSize = 11.5, Margin = new Thickness(20, 8, 20, 10) }
            .Ref(TextBlock.ForegroundProperty, "Muted");
        var divider = new Border { Height = 1 }.Ref(Border.BackgroundProperty, "Border");
        var divider2 = new Border { Height = 1 }.Ref(Border.BackgroundProperty, "Border");

        var body = new StackPanel();
        body.Children.Add(top); body.Children.Add(divider); body.Children.Add(_scroll); body.Children.Add(_empty); body.Children.Add(divider2); body.Children.Add(footer);
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(16), Child = body, ClipToBounds = true,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = .4, Color = Colors.Black },
        }.Ref(Border.BackgroundProperty, "Surface").Ref(Border.BorderBrushProperty, "Border");
        _w.Content = card;
        _w.Width = 680 + 32;

        _query.TextChanged += (_, _) => { _index = 0; Render(); };
        _w.PreviewKeyDown += OnKey;
        _w.Deactivated += (_, _) => Close();
        _w.Closed += (_, _) => _open = null;
        _w.Loaded += (_, _) => { _query.Focus(); Keyboard.Focus(_query); };
        Render();
        _w.Show();
        _w.Activate();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(6); e.Handled = true; break;
            case Key.PageUp: Move(-6); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_shown.Count == 0) return;
        _index = Math.Clamp(_index + delta, 0, _shown.Count - 1);
        Paint();
        _rows[_index].BringIntoView();
    }

    private void Render()
    {
        _shown = PaletteSearch.Rank(_query.Text, _all);
        _list.Children.Clear();
        _rows.Clear();
        string? lastGroup = null;
        bool grouped = string.IsNullOrWhiteSpace(_query.Text);
        for (int i = 0; i < _shown.Count; i++)
        {
            var c = _shown[i];
            if (grouped && c.Group != lastGroup)
            {
                lastGroup = c.Group;
                _list.Children.Add(new TextBlock { Text = c.Group, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(20, i == 0 ? 8 : 12, 20, 4) }
                    .Ref(TextBlock.ForegroundProperty, "Muted"));
            }
            var row = BuildRow(c, i, showGroup: !grouped);
            _rows.Add(row); _list.Children.Add(row);
        }
        _empty.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Paint();
    }

    private Border BuildRow(PaletteCommand c, int index, bool showGroup)
    {
        var icon = new Controls.Icon { Kind = c.Icon, Size = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) }
            .Ref(Controls.Icon.ForegroundProperty, "Accent");
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = c.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis }.Ref(TextBlock.ForegroundProperty, "Text"));
        if (!string.IsNullOrEmpty(c.Note))
            texts.Children.Add(new TextBlock { Text = c.Note, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }.Ref(TextBlock.ForegroundProperty, "Muted"));
        var group = new TextBlock { Text = c.Group, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }
            .Ref(TextBlock.ForegroundProperty, "Muted");
        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        // Без запроса строки уже стоят под заголовками групп: название справа повторяло бы заголовок
        if (showGroup) { DockPanel.SetDock(group, Dock.Right); line.Children.Add(group); }
        line.Children.Add(texts);
        var row = new Border { Padding = new Thickness(20, 8, 16, 8), Child = line, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        System.Windows.Automation.AutomationProperties.SetName(row, c.Title);
        row.MouseMove += (_, _) => { if (_index != index) { _index = index; Paint(); } };
        row.MouseLeftButtonUp += (_, e) => { _index = index; RunSelected(); e.Handled = true; };
        return row;
    }

    /// <summary>Выбранная строка с мягким акцентным фоном, остальные без фона.</summary>
    private void Paint()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (i == _index) _rows[i].SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            else _rows[i].ClearValue(Border.BackgroundProperty);
            if (i != _index) _rows[i].Background = Brushes.Transparent;
        }
    }

    private void RunSelected()
    {
        if (_shown.Count == 0 || _index >= _shown.Count) return;
        var command = _shown[_index];
        Close();
        // Окно уже закрыто: действие идет в главном окне. Ошибка действия не должна ронять программу
        _ = RunSafely(command);
    }

    private static async Task RunSafely(PaletteCommand command)
    {
        try { await command.Run(); }
        catch (Exception ex) { SupportTrace.Write("Палитра, «" + command.Title + "»: " + SupportTrace.Error(ex)); }
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        try { _w.Close(); } catch (InvalidOperationException) { }
    }
}
