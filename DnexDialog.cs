using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dnex;

internal enum DialogKind { Info, Ok, Warn, Error }

internal static class UiExt
{
    /// <summary>Настроить элемент на месте и вернуть его: удобно при сборке окон в коде.</summary>
    public static T With<T>(this T e, Action<T> set) { set(e); return e; }
}

/// <summary>
/// Окна-сообщения в стиле программы вместо системного MessageBox: темная рамка, значок CHIMERION в заголовке,
/// цветной значок рядом с заголовком сообщения, кнопки из общих стилей. На этой же основе собраны окна
/// выхода (ClosePrompt) и обновления (UpdateWindow), чтобы все окна выглядели одинаково.
/// </summary>
internal static class DnexDialog
{
    /// <summary>Пустое окно программы: заголовок «CHIMERION · …», значок, палитра, темная рамка.</summary>
    public static Window CreateWindow(string title, double width)
    {
        var res = Application.Current.Resources;
        var owner = Application.Current.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = $"CHIMERION · {title}",
            Icon = Controls.Brand.WindowIcon, // знак в текущих цветах (полосы следуют за акцентом)
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // Без видимого главного окна (запуск в трей) владельца нет: окно по центру экрана и в панели задач
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = owner == null,

            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        // Текст и раскладка как у главного окна: без этого при плавной прокрутке на дробное смещение текст
        // пересглаживался каждый кадр, строки дрожали и будто наезжали друг на друга (окно «Диагностика»)
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        w.UseLayoutRounding = true;
        w.SourceInitialized += (_, _) => Services.DarkTitle.Apply(w);
        // Esc закрывает окно. Если в окне есть своя кнопка отмены (IsCancel), Esc уже нажимает ее, второй обработчик не нужен
        w.Loaded += (_, _) =>
        {
            // Фокус сразу в окне: Tab и Enter работают без щелчка мышью
            if (!w.IsKeyboardFocusWithin) w.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            if (FindAll<Button>(w).Any(b => b.IsCancel)) return;
            w.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && !e.Handled) { e.Handled = true; w.Close(); } };
        };
        // Окно целиком на экране: по центру владельца высокое окно могло уйти верхом или низом за край
        w.SizeChanged += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            if (w.Top + w.ActualHeight > area.Bottom) w.Top = Math.Max(area.Top, area.Bottom - w.ActualHeight);
            if (w.Top < area.Top) w.Top = area.Top;
            if (w.Left + w.ActualWidth > area.Right) w.Left = Math.Max(area.Left, area.Right - w.ActualWidth);
            if (w.Left < area.Left) w.Left = area.Left;
        };
        return w;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }

    /// <summary>Шапка: цветной значок и заголовок с пояснением. Длинный заголовок переносится, а не обрезается.</summary>
    public static FrameworkElement Header(DialogKind kind, string title, string? subtitle, string? glyph = null)
    {
        var res = Application.Current.Resources;
        var (bg, fg, sign) = kind switch
        {
            DialogKind.Ok => ("OkSoft", "Ok", "circle-check"),
            DialogKind.Warn => ("WarnSoft", "Warn", "triangle-alert"),
            DialogKind.Error => ("BadSoft", "Bad", "circle-x"),
            _ => ("InfoSoft", "Accent", "info"),
        };
        // glyph: имя иконки (Controls.Icon) или прежний символ, который переводится в иконку
        var kindName = glyph switch
        {
            null => sign, "⇄" => "arrow-left-right", "⚡" => "zap", "✚" => "stethoscope", "⚙" => "settings",
            "↑" => "circle-arrow-up", "↓" => "download", "✓" => "circle-check", _ => glyph,
        };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = (Brush)res[bg],
            VerticalAlignment = VerticalAlignment.Top,
            Child = Controls.Icon.Outline.ContainsKey(kindName)
                ? new Controls.Icon { Kind = kindName, Size = 19, Foreground = (Brush)res[fg], HorizontalAlignment = HorizontalAlignment.Center }
                : new TextBlock
                {
                    Text = kindName, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = (Brush)res[fg],
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
        };
        grid.Children.Add(badge);
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
            titles.Children.Add(new TextBlock
            {
                Text = subtitle, Foreground = (Brush)res["Muted"], FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        return grid;
    }

    public static Button MakeButton(string text, string style, Action click)
    {
        var b = new Button
        {
            Content = text, Style = (Style)Application.Current.Resources[style],
            MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 9, 16, 9),
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Сообщение с одной кнопкой «Понятно».</summary>
    public static void Show(string title, string text, DialogKind kind = DialogKind.Info, string windowTitle = "Сообщение")
    {
        var w = CreateWindow(windowTitle, Ui.Narrow);
        var root = Body(title, text, kind);
        var ok = Ui.Action("Понятно", "Primary", () => w.Close());
        ok.IsDefault = true;
        ok.IsCancel = true;
        root.Children.Add(Ui.Footer(null, ok));
        w.Content = root;
        w.ShowDialog();
    }

    /// <summary>
    /// Вопрос с двумя кнопками. Esc и закрытие окна — отказ. danger: действие необратимо или рискованно,
    /// кнопка красная и Enter ее не нажимает.
    /// </summary>
    public static bool Ask(string title, string text, string yes, string no = "Отмена",
                           DialogKind kind = DialogKind.Warn, bool danger = false, string windowTitle = "Вопрос")
    {
        bool result = false;
        var w = CreateWindow(windowTitle, Ui.Narrow);
        var root = Body(title, text, kind);
        var yesBtn = Ui.Action(yes, danger ? "Danger" : "Primary", () => { result = true; w.Close(); });
        yesBtn.IsDefault = !danger; // рискованное действие Enter не нажимает
        var noBtn = Ui.Action(no, "Ghost", () => w.Close());
        noBtn.IsCancel = true;
        root.Children.Add(Ui.Footer(null, yesBtn, noBtn));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    /// <summary>
    /// Рискованный шаг, который программа не рекомендует: кнопка действия красная и неактивна, пока человек не отметит
    /// галочку confirm. По умолчанию (Enter, Esc, закрытие окна) выбирается отказ.
    /// </summary>
    public static bool AskRisk(string title, string text, string confirm, string yes, string no, string windowTitle = "Вопрос")
    {
        bool result = false;
        var w = CreateWindow(windowTitle, Ui.Narrow);
        var root = Body(title, text, DialogKind.Warn);
        var check = new CheckBox { Content = new TextBlock { Text = confirm, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(50, 10, 0, 4) };
        root.Children.Add(check);
        var yesBtn = Ui.Action(yes, "Danger", () => { result = true; w.Close(); });
        yesBtn.IsEnabled = false;
        check.Checked += (_, _) => yesBtn.IsEnabled = true;
        check.Unchecked += (_, _) => yesBtn.IsEnabled = false;
        var noBtn = Ui.Action(no, "Primary", () => w.Close());
        noBtn.IsDefault = true;
        noBtn.IsCancel = true;
        root.Children.Add(Ui.Footer(null, noBtn, yesBtn));
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    private static StackPanel Body(string title, string text, DialogKind kind)
    {
        var root = Ui.Root();
        root.Children.Add(Header(kind, title, null));
        if (!string.IsNullOrEmpty(text))
            root.Children.Add(new TextBlock
            {
                Text = text, Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(50, -6, 0, 4), LineHeight = 19,
            });
        return root;
    }
}
