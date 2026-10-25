using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dnex;

internal enum DialogKind { Info, Ok, Warn, Error }

/// <summary>
/// Окна-сообщения в стиле программы вместо системного MessageBox: темная рамка, значок DNEX в заголовке,
/// цветной значок рядом с заголовком сообщения, кнопки из общих стилей. На этой же основе собраны окна
/// выхода (ClosePrompt) и обновления (UpdateWindow), чтобы все окна выглядели одинаково.
/// </summary>
internal static class DnexDialog
{
    /// <summary>Пустое окно программы: заголовок «DNEX · …», значок, палитра, темная рамка.</summary>
    public static Window CreateWindow(string title, double width)
    {
        var res = Application.Current.Resources;
        var owner = Application.Current.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = $"DNEX · {title}",
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/dnex-logo.png")),
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // Без видимого главного окна (запуск в трей) владельца нет: окно по центру экрана и в панели задач
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = owner == null,
            Background = (Brush)res["Bg"],
            Foreground = (Brush)res["Text"],
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
        };
        w.SourceInitialized += (_, _) => Services.DarkTitle.Apply(w);
        return w;
    }

    /// <summary>Шапка: цветной значок и заголовок с пояснением. Длинный заголовок переносится, а не обрезается.</summary>
    public static FrameworkElement Header(DialogKind kind, string title, string? subtitle, string? glyph = null)
    {
        var res = Application.Current.Resources;
        var (bg, fg, sign) = kind switch
        {
            DialogKind.Ok => (Color.FromRgb(0x13, 0x28, 0x1E), "Ok", "✓"),
            DialogKind.Warn => (Color.FromRgb(0x3A, 0x2D, 0x12), "Warn", "!"),
            DialogKind.Error => (Color.FromRgb(0x3A, 0x17, 0x1C), "Bad", "×"),
            _ => (Color.FromRgb(0x16, 0x26, 0x40), "Accent", "i"),
        };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(bg),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = glyph ?? sign, FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = (Brush)res[fg],
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
        var w = CreateWindow(windowTitle, 460);
        var root = Body(title, text, kind);
        var row = new DockPanel { LastChildFill = false };
        var ok = MakeButton("Понятно", "Primary", () => w.Close());
        ok.IsDefault = true;
        ok.IsCancel = true;
        DockPanel.SetDock(ok, Dock.Right);
        row.Children.Add(ok);
        root.Children.Add(row);
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
        var w = CreateWindow(windowTitle, 460);
        var root = Body(title, text, kind);
        var row = new DockPanel { LastChildFill = false };
        var yesBtn = MakeButton(yes, "Primary", () => { result = true; w.Close(); });
        if (danger)
        {
            // Красная заливка, как у кнопки «Отключить» в главном окне
            yesBtn.Background = (Brush)Application.Current.Resources["BadButton"];
            yesBtn.BorderBrush = yesBtn.Background;
        }
        yesBtn.IsDefault = !danger;
        var noBtn = MakeButton(no, "Ghost", () => w.Close());
        noBtn.IsCancel = true;
        DockPanel.SetDock(yesBtn, Dock.Right);
        DockPanel.SetDock(noBtn, Dock.Right);
        row.Children.Add(yesBtn);
        row.Children.Add(noBtn);
        root.Children.Add(row);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    private static StackPanel Body(string title, string text, DialogKind kind)
    {
        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        root.Children.Add(Header(kind, title, null));
        if (!string.IsNullOrEmpty(text))
            root.Children.Add(new TextBlock
            {
                Text = text, Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(50, -6, 0, 20), LineHeight = 19,
            });
        return root;
    }
}
