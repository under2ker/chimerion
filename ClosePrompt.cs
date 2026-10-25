using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Вопрос при выходе, когда что-то работает: подключение DNS, обход zapret, ускорение Telegram.
/// По умолчанию «Нет» (все остается работать, как с DNS), «Да» выключает все, Esc и закрытие окна — «Отмена».
/// Системное MessageBox делало бы «Да» кнопкой по умолчанию: лишний Enter выключал бы все.
/// </summary>
internal static class ClosePrompt
{
    public static MessageBoxResult Ask(string connectionName) => Ask(connectionName, Array.Empty<string>());

    /// <param name="connectionName">Активное подключение DNS или null.</param>
    /// <param name="tools">Работающие инструменты CHIMERION («обход zapret», «ускорение Telegram»).</param>
    public static MessageBoxResult Ask(string? connectionName, IReadOnlyList<string> tools)
    {
        var result = MessageBoxResult.Cancel;
        var res = Application.Current.Resources;
        var w = DnexDialog.CreateWindow("Выход", Ui.Narrow);
        var root = Ui.Root();
        var parts = new List<string>();
        if (connectionName != null) parts.Add($"DNS «{connectionName}»");
        parts.AddRange(tools);
        var what = string.Join(", ", parts);
        root.Children.Add(DnexDialog.Header(DialogKind.Warn,
            connectionName != null && tools.Count == 0 ? $"Подключение «{connectionName}» активно" : "Сейчас работает: " + what,
            "Что сделать с этим при выходе из программы?"));

        // Три варианта: каждый с пояснением, основной подсвечен
        var keepHint = new List<string>();
        if (connectionName != null) keepHint.Add("DNS останется переключенным");
        if (tools.Count > 0) keepHint.Add(string.Join(" и ", tools) + (tools.Count > 1 ? " продолжат работать" : " продолжит работать"));
        root.Children.Add(MakeOption(connectionName != null && tools.Count == 0 ? "Выйти, оставив DNS как есть" : "Выйти, оставив все работать",
            "Программа закроется, " + string.Join(", ", keepHint) + ". При следующем запуске CHIMERION подхватит их как есть." +
            (connectionName != null ? " Вернуть исходные DNS можно и через reset-dns.cmd." : ""),
            MessageBoxResult.No, isDefault: true));
        root.Children.Add(MakeOption(connectionName != null && tools.Count == 0 ? "Отключиться и выйти" : "Выключить все и выйти",
            (connectionName != null ? "Исходные DNS вернутся" : "") + (connectionName != null && tools.Count > 0 ? ", " : "") +
            (tools.Count > 0 ? string.Join(" и ", tools) + " выключатся" : "") + ", программа закроется.",
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

                BorderThickness = new Thickness(isDefault ? 1.5 : 1),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8),
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            // Кнопка-карточка: скругление как у карточек, подсветка от общего стиля кнопок
            b.Template = CardButtonTemplate();
            b.SetResourceReference(Control.BorderBrushProperty, isDefault ? "Accent" : "Border");
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
