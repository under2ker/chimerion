using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Вопрос при выходе с активным подключением. Системное MessageBox делает «Да» (отключиться) кнопкой
/// по умолчанию: лишний Enter отключает DNS. Здесь по умолчанию выбрано «Нет» (DNS остается как есть),
/// Esc и закрытие окна — «Отмена» (не выходить).
/// </summary>
internal static class ClosePrompt
{
    public static MessageBoxResult Ask(string connectionName)
    {
        var result = MessageBoxResult.Cancel;
        var res = Application.Current.Resources;
        var w = new Window
        {
            Title = "DNEX",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)res["Bg"],
            Foreground = (Brush)res["Text"],
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = false,
        };

        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };

        // Шапка: предупреждающий значок, заголовок и пояснение
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        head.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2D, 0x12)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "!", FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = (Brush)res["Warn"],
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = $"Подключение «{connectionName}» активно",
            FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        titles.Children.Add(new TextBlock
        {
            Text = "Что сделать с DNS при выходе из программы?",
            Foreground = (Brush)res["Muted"], FontSize = 12, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        head.Children.Add(titles);
        root.Children.Add(head);

        // Три варианта: каждый с пояснением, основной подсвечен
        root.Children.Add(MakeOption("Выйти, оставив DNS как есть",
            "Программа закроется, DNS останется переключенным. Вернуть исходные настройки можно в программе или через reset-dns.cmd.",
            MessageBoxResult.No, isDefault: true));
        root.Children.Add(MakeOption("Отключиться и выйти",
            "Исходные DNS вернутся, программа закроется.",
            MessageBoxResult.Yes, danger: true));
        root.Children.Add(MakeOption("Отмена",
            "Остаться в программе.",
            MessageBoxResult.Cancel, isCancel: true));

        w.Content = root;
        w.ShowDialog();
        return result;

        Button MakeOption(string title, string hint, MessageBoxResult answer,
                          bool isDefault = false, bool isCancel = false, bool danger = false)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold,
                Foreground = danger ? (Brush)res["Bad"] : (Brush)res["Text"],
            });
            body.Children.Add(new TextBlock
            {
                Text = hint, Foreground = (Brush)res["Muted"], FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            });

            var b = new Button
            {
                Content = body,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = (Brush)res["Surface2"],
                BorderBrush = isDefault ? (Brush)res["Accent"] : (Brush)res["Border"],
                BorderThickness = new Thickness(isDefault ? 1.5 : 1),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8),
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            // Кнопка-карточка: скругление как у карточек, подсветка от общего стиля кнопок
            b.Template = CardButtonTemplate();
            b.Click += (_, _) => { result = answer; w.Close(); };
            return b;
        }
    }

    private static ControlTemplate CardButtonTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        bd.SetValue(Border.PaddingProperty, new Thickness(0));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(16, 12, 16, 12));
        bd.AppendChild(content);
        t.VisualTree = bd;

        // Наведение: чуть светлее фон, нажатие: приглушение
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        over.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.Resources["SurfaceHover"], "bd"));
        var press = new Trigger { Property = Button.IsPressedProperty, Value = true };
        press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8));
        t.Triggers.Add(over);
        t.Triggers.Add(press);
        return t;
    }
}
