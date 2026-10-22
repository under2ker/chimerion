using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Вопрос при выходе с активным подключением. Системное MessageBox делает «Да» (отключиться) кнопкой
/// по умолчанию: лишний Enter отключает DNS. Здесь по умолчанию выбрано «Нет» (DNS остаётся как есть),
/// Esc и закрытие окна — «Отмена» (не выходить).
/// </summary>
internal static class ClosePrompt
{
    public static MessageBoxResult Ask(string connectionName)
    {
        var result = MessageBoxResult.Cancel;
        var w = new Window
        {
            Title = "DNEX",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.FindResource("Surface"),
            Foreground = (Brush)Application.Current.FindResource("Text"),
            ShowInTaskbar = false,
        };

        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock
        {
            Text = $"Подключение «{connectionName}» активно.",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Нет — выйти, оставив DNS как есть\nДа — отключиться и выйти\nОтмена — не выходить",
            Foreground = (Brush)Application.Current.FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(MakeButton("Нет", "Primary", MessageBoxResult.No, isDefault: true));
        row.Children.Add(MakeButton("Да", "Danger", MessageBoxResult.Yes));
        row.Children.Add(MakeButton("Отмена", null, MessageBoxResult.Cancel, isCancel: true));
        sp.Children.Add(row);
        w.Content = sp;

        Button MakeButton(string text, string? style, MessageBoxResult answer, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 110,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 8, 14, 8),
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            if (style != null) b.Style = (Style)Application.Current.FindResource(style);
            b.Click += (_, _) => { result = answer; w.Close(); };
            return b;
        }

        w.ShowDialog();
        return result;
    }
}
