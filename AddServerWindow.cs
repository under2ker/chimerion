using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
    private static readonly Regex Ipv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// IP-адрес в полной записи. IPAddress.TryParse сам по себе принимает и сокращенные «1.2.3» (это 1.2.0.3) или «10»,
    /// поэтому опечатка с пропущенной частью адреса проходила как верный адрес. Здесь IPv4 только из четырех чисел, IPv6 с двоеточиями.
    /// </summary>
    public static bool IsIp(string s) =>
        IPAddress.TryParse(s, out var ip) &&
        (ip.AddressFamily == AddressFamily.InterNetwork ? Ipv4.IsMatch(s) : ip.AddressFamily == AddressFamily.InterNetworkV6 && s.Contains(':') && !s.Contains('%'));

    /// <summary>Сервер из полей формы или текст ошибки и поле, в котором она.</summary>
    public static (DnsServer? Server, string? Error, AddServerField Field) Validate(string? name, string? primary, string? secondary, string? doh)
    {
        primary = primary?.Trim() ?? "";
        secondary = secondary?.Trim() ?? "";
        doh = doh?.Trim() ?? "";
        if (primary.Length == 0 && doh.Length == 0)
            return (null, "Укажите основной IP или адрес DoH", AddServerField.Primary);
        // Заполненное поле с ошибкой — всегда ошибка, даже если другое поле верное: иначе неверный IP или DoH молча терялся
        if (primary.Length > 0 && !IsIp(primary))
            return (null, "Нужен IP вида 203.0.113.10, без порта", AddServerField.Primary);
        if (secondary.Length > 0 && !IsIp(secondary))
            return (null, "Нужен IP вида 203.0.113.10, без порта", AddServerField.Secondary);
        Uri? u = null;
        if (doh.Length > 0 && !(Uri.TryCreate(doh, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Length > 0))
            return (null, "Адрес DoH должен начинаться с https://", AddServerField.Doh);
        bool hasIp = primary.Length > 0;
        return (new DnsServer
        {
            Name = string.IsNullOrWhiteSpace(name) ? (hasIp ? primary : u!.Host) : name.Trim(),
            Primary = primary,
            Secondary = secondary.Length > 0 ? secondary : null,
            DohUrl = doh.Length > 0 ? doh : null,
            Manual = true,
        }, null, AddServerField.None);
    }
}

/// <summary>
/// Окно «Добавить сервер»: название, IP и адрес DoH. Сетка как у остальных окон: поля окна Ui.Root (24 по бокам),
/// 16 между блоками, 6 от подписи до поля. Ошибка у своего поля: красная рамка (FieldError) и текст прямо под полем,
/// исчезают, как только поле начинают исправлять. Введенное не теряется. Кнопки в нижней строке: «Добавить» основная справа.
/// </summary>
internal static class AddServerWindow
{
    private const double BlockGap = 16, LabelGap = 6;

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
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Добавить сервер", "Нужен IP, адрес DoH или оба", glyph: "plus"));

        var name = new TextBox { Tag = "Необязательно" };
        var primary = new TextBox { Tag = "Например, 203.0.113.10" };
        var secondary = new TextBox { Tag = "Необязательно" };
        var doh = new TextBox { Tag = "Например, https://dns.example.com/dns-query" };

        var nameField = new Field("Название", name);
        var primaryField = new Field("Основной IP", primary);
        var secondaryField = new Field("Резервный IP", secondary);
        var dohField = new Field("Адрес DoH", doh, Ui.HelpMark("Адрес DoH",
            "DNS через HTTPS: адрес из описания сервера, начинается с https://. Можно указать вместо IP или вместе с ним."));

        nameField.Panel.Margin = new Thickness(0, BlockGap, 0, 0);
        root.Children.Add(nameField.Panel);
        var ips = new Grid { Margin = new Thickness(0, BlockGap, 0, 0) };
        ips.ColumnDefinitions.Add(new ColumnDefinition());
        ips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ips.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(secondaryField.Panel, 2);
        ips.Children.Add(primaryField.Panel);
        ips.Children.Add(secondaryField.Panel);
        root.Children.Add(ips);
        dohField.Panel.Margin = new Thickness(0, BlockGap, 0, 0);
        root.Children.Add(dohField.Panel);

        var fields = new[] { primaryField, secondaryField, dohField };
        void Submit()
        {
            var (server, text, which) = AddServerForm.Validate(name.Text, primary.Text, secondary.Text, doh.Text);
            foreach (var f in fields) f.SetError(null);
            if (server == null)
            {
                var bad = which switch { AddServerField.Secondary => secondaryField, AddServerField.Doh => dohField, _ => primaryField };
                bad.SetError(text);
                bad.Box.Focus();
                bad.Box.SelectAll();
                return;
            }
            added(server);
            w.Close();
        }

        var add = Ui.Action("Добавить", "Primary", Submit);
        var cancel = Ui.Action("Отмена", "Ghost", w.Close).With(b => b.IsCancel = true);
        var footer = Ui.Footer(null, add, cancel);
        footer.Margin = new Thickness(0, BlockGap + 4, 0, 0);
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

    /// <summary>Подпись (и при желании знак пояснения), поле и место для ошибки под полем.</summary>
    private sealed class Field
    {
        public readonly StackPanel Panel = new();
        public readonly TextBox Box;
        private readonly TextBlock _error;

        public Field(string label, TextBox box, FrameworkElement? help = null)
        {
            Box = box;
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, LabelGap) };
            caption.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
            if (help != null) caption.Children.Add(help);
            Panel.Children.Add(caption);
            Panel.Children.Add(box);
            _error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, LabelGap, 0, 0), Visibility = Visibility.Collapsed }
                .Ref(TextBlock.ForegroundProperty, "Bad");
            Panel.Children.Add(_error);
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            // Начали исправлять — ошибка уходит, чтобы красная рамка не висела над уже другим текстом
            box.TextChanged += (_, _) => { if (_error.Visibility == Visibility.Visible) SetError(null); };
        }

        public void SetError(string? text)
        {
            _error.Text = text ?? "";
            _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            Controls.FieldError.SetIsError(Box, text != null);
            System.Windows.Automation.AutomationProperties.SetHelpText(Box, text ?? "");
        }
    }
}
