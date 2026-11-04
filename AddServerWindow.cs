using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dnex.Models;

namespace Dnex;

/// <summary>Какое поле формы добавления сервера заполнено неверно.</summary>
internal enum AddServerField { None, Primary, Secondary, Doh }

/// <summary>Проверка полей формы добавления сервера: без окна, чтобы проверять ее тестами.</summary>
internal static class AddServerForm
{
    /// <summary>Сервер из полей формы или текст ошибки и поле, в котором она.</summary>
    public static (DnsServer? Server, string? Error, AddServerField Field) Validate(string? name, string? primary, string? secondary, string? doh)
    {
        primary = primary?.Trim() ?? "";
        secondary = secondary?.Trim() ?? "";
        doh = doh?.Trim() ?? "";
        bool hasIp = IPAddress.TryParse(primary, out _);
        bool hasDoh = Uri.TryCreate(doh, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        if (!hasIp && !hasDoh)
        {
            return (null,
                primary.Length == 0 && doh.Length == 0 ? "Укажите основной IP (например 203.0.113.10) или адрес DoH"
                : primary.Length > 0 ? $"«{primary}» не похож на IP-адрес: нужен вид 203.0.113.10, без порта и запятых"
                : "Адрес DoH должен начинаться с https://",
                primary.Length > 0 || doh.Length == 0 ? AddServerField.Primary : AddServerField.Doh);
        }
        if (secondary.Length > 0 && !IPAddress.TryParse(secondary, out _))
            return (null, $"«{secondary}» не похож на IP-адрес", AddServerField.Secondary);
        return (new DnsServer
        {
            Name = string.IsNullOrWhiteSpace(name) ? (hasIp ? primary : u!.Host) : name.Trim(),
            Primary = hasIp ? primary : "",
            Secondary = secondary.Length > 0 ? secondary : null,
            DohUrl = hasDoh ? doh : null,
            Manual = true,
        }, null, AddServerField.None);
    }
}

/// <summary>Окно «Добавить сервер»: название, IP и адрес DoH. Ошибка ввода видна в самом окне, данные не теряются.</summary>
internal static class AddServerWindow
{
    /// <summary>Показывает окно. Возвращает проверенный сервер или null, если окно закрыли.</summary>
    public static DnsServer? Ask()
    {
        DnsServer? result = null;
        Build(s => result = s).ShowDialog();
        return result;
    }

    internal static Window Build(Action<DnsServer> added)
    {
        var w = DnexDialog.CreateWindow("Добавить сервер", Ui.Narrow);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Добавить сервер", "Нужен IP, адрес DoH или оба. Название можно не указывать", glyph: "plus"));

        var name = new TextBox { Tag = "Например, мой DNS" };
        var primary = new TextBox { Tag = "203.0.113.10" };
        var secondary = new TextBox { Tag = "Необязательно" };
        var doh = new TextBox { Tag = "https://…/dns-query" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0), Foreground = Ui.B("Warn"), Visibility = Visibility.Collapsed };

        root.Children.Add(Field("Название", name, 0));
        var ips = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        ips.ColumnDefinitions.Add(new ColumnDefinition());
        ips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ips.ColumnDefinitions.Add(new ColumnDefinition());
        var first = Field("Основной IP", primary, 0);
        var second = Field("Резервный IP", secondary, 0);
        Grid.SetColumn(second, 2);
        ips.Children.Add(first);
        ips.Children.Add(second);
        root.Children.Add(ips);
        root.Children.Add(Field("Адрес DoH", doh, 12));
        root.Children.Add(error);

        void Submit()
        {
            var (server, text, field) = AddServerForm.Validate(name.Text, primary.Text, secondary.Text, doh.Text);
            if (server == null)
            {
                error.Text = text;
                error.Visibility = Visibility.Visible;
                var box = field switch { AddServerField.Secondary => secondary, AddServerField.Doh => doh, _ => primary };
                box.Focus();
                box.SelectAll();
                return;
            }
            added(server);
            w.Close();
        }

        var add = Ui.Action("Добавить", "Primary", Submit);
        var cancel = Ui.Action("Отмена", "Ghost", w.Close).With(b => b.IsCancel = true);
        var footer = Ui.Footer(null, add, cancel);
        footer.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(footer);

        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.OriginalSource is Button) return;
            e.Handled = true;
            Submit();
        };
        w.Loaded += (_, _) => name.Focus();
        w.Content = root;
        return w;
    }

    private static StackPanel Field(string label, TextBox box, double top)
    {
        var panel = new StackPanel { Margin = new Thickness(0, top, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Ui.B("Muted"), Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(box);
        return panel;
    }
}
