using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Доступна новая версия». Показывает версию, описание из релиза и ведет на страницу релиза в браузере.
/// Ничего не загружает и не заменяет: скачивание делает пользователь.
/// </summary>
internal static class UpdateWindow
{
    public static void Show(Window owner, UpdateInfo info, Action onSkip)
    {
        var res = Application.Current.Resources;
        var w = new Window
        {
            Title = "DNEX: обновление",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // Программа запущена в трей и окно еще не открывалось: владельцем его назначить нельзя (WPF бросает
            // исключение), окно обновления встает по центру экрана и видно в панели задач
            Owner = owner.IsVisible ? owner : null,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Background = (Brush)res["Bg"],
            Foreground = (Brush)res["Text"],
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = !owner.IsVisible,
        };

        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x28, 0x1E)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "↑", FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = (Brush)res["Ok"],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = $"Доступна версия {info.Version}", FontSize = 15, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(new TextBlock
        {
            Text = $"Сейчас установлена {UpdateChecker.CurrentVersion}",
            Foreground = (Brush)res["Muted"], FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
        });
        head.Children.Add(titles);
        root.Children.Add(head);

        // Описание из релиза: только текст, без разметки
        var notes = new TextBlock
        {
            Text = CleanNotes(info.Notes),
            Foreground = (Brush)res["Muted"], FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var scroll = new ScrollViewer
        {
            Content = notes, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(0, 0, 8, 0),
        };
        root.Children.Add(scroll);

        var row = new DockPanel { LastChildFill = false };
        var download = MakeButton("Скачать", (Style)res["Primary"], () =>
        {
            try { AutoStart.OpenUrl(info.PageUrl); }
            catch (Exception) { }
            w.Close();
        });
        DockPanel.SetDock(download, Dock.Right);
        var later = MakeButton("Позже", (Style)res["Ghost"], () => w.Close());
        DockPanel.SetDock(later, Dock.Right);
        var skip = MakeButton("Пропустить версию", (Style)res["Ghost"], () => { onSkip(); w.Close(); });
        DockPanel.SetDock(skip, Dock.Left);
        row.Children.Add(download);
        row.Children.Add(later);
        row.Children.Add(skip);
        root.Children.Add(row);

        w.Content = root;
        w.SourceInitialized += (_, _) => Dnex.Services.DarkTitle.Apply(w);
        w.Show();

        Button MakeButton(string text, Style style, Action click)
        {
            var b = new Button { Content = text, Style = style, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 9, 16, 9) };
            b.Click += (_, _) => click();
            return b;
        }
    }

    /// <summary>Убирает разметку Markdown, которую не показываем: заголовки, жирный, код.</summary>
    internal static string CleanNotes(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", "").TrimEnd())
            .Where(l => l.Length > 0);
        var text = string.Join(Environment.NewLine, lines);
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
