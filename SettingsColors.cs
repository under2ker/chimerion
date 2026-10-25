using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Раздел «Цвета интерфейса» в настройках: готовые наборы, свой цвет каждого элемента из палитры или по HEX,
/// предупреждение о читаемости, возврат стандартных цветов. Цвет меняется сразу во всей программе (предпросмотр);
/// в файл попадает по «Сохранить», «Отмена» возвращает прежние цвета.
/// </summary>
public partial class SettingsWindow
{
    private Dictionary<string, string> _colors = new();
    private Dictionary<string, string> _colorsBefore = new();
    private bool _colorsSaved;
    private string _theme = "dark", _themeBefore = "dark";
    private bool Dark => _theme != "light";
    /// <summary>Наборы на светлой теме задают только оттенки: подложки программа выводит сама, светлыми.</summary>
    private static readonly string[] HueSlots = { "Accent", "AccentButton", "ToggleOn", "Lime" };
    private string K(string slot) => ThemeColors.CustomKey(slot, Dark);

    /// <summary>Палитра быстрого выбора: спокойные цвета, которые читаются на темном фоне.</summary>
    private static readonly string[] Swatches =
    {
        "#4C8DFF", "#2A66C9", "#38BDF8", "#2DD4BF", "#34D399", "#0F8F63", "#A3E635", "#FACC15",
        "#F5B03C", "#FB923C", "#F87171", "#F472B6", "#BE2F72", "#A78BFA", "#6D4BD8", "#B4BCCB",
        "#1E3156", "#263F66", "#2C3446", "#242832", "#123B2E", "#2E2452", "#3D2E12", "#41203A",
    };

    private void BuildColors()
    {
        _colorsBefore = new Dictionary<string, string>(_settings.Colors);
        _colors = new Dictionary<string, string>(_settings.Colors);
        _theme = _themeBefore = _settings.Theme == "light" ? "light" : "dark";
        Closed += (_, _) => { if (!_colorsSaved) ThemeColors.Apply(_colorsBefore, _themeBefore); };
        RenderColors();
    }

    /// <summary>Перед сохранением настроек: свои цвета в файл.</summary>
    private void SaveColors()
    {
        _settings.Colors = new Dictionary<string, string>(_colors);
        _settings.Theme = _theme;
        _colorsSaved = true;
    }

    private void Preview() => ThemeColors.Apply(_colors, _theme);

    private void RenderColors()
    {
        ColorsHost.Children.Clear();

        // Тема: темная или светлая. Меняется сразу (предпросмотр), сохраняется по «Сохранить»
        ColorsHost.Children.Add(new TextBlock { Text = "Тема", Style = (Style)FindResource("FieldLabel") });
        var themes = new StackPanel { Orientation = Orientation.Horizontal };
        var themeBox = new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), Child = themes }
            .Ref(Border.BackgroundProperty, "Surface2").Ref(Border.BorderBrushProperty, "Border");
        foreach (var (id, title) in new[] { ("dark", "Темная"), ("light", "Светлая") })
        {
            var rb = new RadioButton { Content = title, GroupName = "theme", IsChecked = _theme == id, Style = (Style)FindResource("Tab") };
            rb.Checked += (_, _) => { if (_theme == id) return; _theme = id; Preview(); RenderColors(); };
            themes.Children.Add(rb);
        }
        ColorsHost.Children.Add(themeBox);

        // Готовые наборы: кружки двух главных цветов и название
        ColorsHost.Children.Add(new TextBlock { Text = "Готовые наборы", Style = (Style)FindResource("FieldLabel") });
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (name, set) in ThemeColors.Presets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var key in new[] { "Accent", "AccentButton" })
                row.Children.Add(new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 4, 0),
                    Background = new SolidColorBrush(ThemeColors.Get(set, key)), VerticalAlignment = VerticalAlignment.Center,
                });
            row.Children.Add(new TextBlock { Text = name, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var slotsOfTheme = Dark ? ThemeColors.Slots.Select(s => s.Key) : HueSlots;
            var active = slotsOfTheme.All(k => (set.TryGetValue(k, out var v) ? v : null) == (_colors.TryGetValue(K(k), out var c) ? c : null))
                         && ThemeColors.Slots.All(s => slotsOfTheme.Contains(s.Key) || !_colors.ContainsKey(K(s.Key)));
            var b = new Button { Content = row, Padding = new Thickness(12, 6, 12, 6), MinHeight = 32, Margin = new Thickness(0, 0, 8, 8) };
            if (active) b.SetResourceReference(Control.BorderBrushProperty, "Accent");
            b.Click += (_, _) =>
            {
                // Набор меняет цвета только текущей темы; на светлой — только оттенки
                foreach (var s2 in ThemeColors.Slots) _colors.Remove(K(s2.Key));
                foreach (var (k, v) in set) if (Dark || HueSlots.Contains(k)) _colors[K(k)] = v;
                Preview(); RenderColors();
            };
            presets.Children.Add(b);
        }
        ColorsHost.Children.Add(presets);

        // Свой цвет каждого элемента
        foreach (var slot in ThemeColors.Slots)
            ColorsHost.Children.Add(ColorRow(slot));

        var reset = new Button { Content = "Вернуть стандартные цвета", Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-18, 8, 0, 0) };
        reset.Click += (_, _) => { foreach (var s2 in ThemeColors.Slots) _colors.Remove(K(s2.Key)); Preview(); RenderColors(); };
        ColorsHost.Children.Add(reset);
        ColorsHost.Children.Add(new TextBlock
        {
            Text = "Цвета меняются сразу, чтобы было видно результат. Сохранятся по «Сохранить», «Отмена» вернет прежние. " +
                   "Оттенки наведения, рамок и текста на кнопках CHIMERION подбирает сама, чтобы все читалось",
            Style = (Style)FindResource("FieldHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
    }

    private FrameworkElement ColorRow(ThemeColors.Slot slot)
    {
        // На светлой теме подложки без своего цвета выводятся из акцента: показываем то, что применено
        var derived = !Dark && slot.LightDefault.Length == 0 && ThemeColors.Custom(_colors, slot.Key, false) == null;
        var color = derived && Application.Current.TryFindResource(slot.Key) is SolidColorBrush applied
            ? applied.Color : ThemeColors.Get(_colors, slot.Key, Dark);
        var std = ThemeColors.Hex(ThemeColors.Get(null, slot.Key, Dark));
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(new TextBlock { Text = slot.Title });
        left.Children.Add(new TextBlock { Text = slot.Where, Style = (Style)FindResource("FieldHint"), TextWrapping = TextWrapping.Wrap });
        var warn = ThemeColors.Warning(slot.Key, color);
        if (warn != null)
            left.Children.Add(new TextBlock { Text = "Внимание: " + warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
                .Ref(TextBlock.ForegroundProperty, "Warn"));
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Образец цвета: щелчок открывает палитру
        var swatch = new Button
        {
            Width = 34, Height = 34, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Выбрать из палитры",
            Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(color),
                                   BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) },
        };
        var hex = new TextBox { Text = ThemeColors.Hex(color), Width = 92, MaxLength = 7, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Tag = "#RRGGBB" };
        void Set(Color c)
        {
            if (!derived && ThemeColors.Hex(c) == std) _colors.Remove(K(slot.Key)); else _colors[K(slot.Key)] = ThemeColors.Hex(c);
            Preview();
            RenderColors();
        }
        hex.LostKeyboardFocus += (_, _) => { if (ThemeColors.TryParse(hex.Text, out var c) && c != color) Set(c); else hex.Text = ThemeColors.Hex(color); };
        hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && ThemeColors.TryParse(hex.Text, out var c)) { e.Handled = true; Set(c); } };
        swatch.Click += (_, _) => OpenPalette(swatch, color, Set);
        right.Children.Add(swatch);
        right.Children.Add(hex);
        if (_colors.ContainsKey(K(slot.Key)))
        {
            var undo = new Button { Content = new Controls.Icon { Kind = "refresh-cw", Size = 14 }, Style = (Style)FindResource("Ghost"), Padding = new Thickness(8), MinHeight = 0,
                                    Margin = new Thickness(4, 0, 0, 0), ToolTip = "Вернуть стандартный цвет" };
            undo.Click += (_, _) => { _colors.Remove(K(slot.Key)); Preview(); RenderColors(); };
            right.Children.Add(undo);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Палитра под образцом: готовые цвета и поле HEX. Закрывается при выборе или щелчке мимо.</summary>
    private void OpenPalette(FrameworkElement anchor, Color current, Action<Color> pick)
    {
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var grid = new UniformGrid { Columns = 8 };
        foreach (var h in Swatches)
        {
            ThemeColors.TryParse(h, out var c);
            var b = new Button
            {
                Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(3), ToolTip = h,
                Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(c),
                                       BorderBrush = Brushes.White, BorderThickness = new Thickness(c == current ? 2 : 0) },
            };
            b.Click += (_, _) => { popup.IsOpen = false; pick(c); };
            grid.Children.Add(b);
        }
        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(new TextBlock { Text = "Или свой цвет: введите HEX в поле рядом с образцом", Style = (Style)FindResource("FieldHint"), Margin = new Thickness(4, 6, 4, 2) });
        var border = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = body }
            .Ref(Border.BackgroundProperty, "MenuBg").Ref(Border.BorderBrushProperty, "Border");
        popup.Child = border;
        popup.IsOpen = true;
    }
}
