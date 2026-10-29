using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>Окно «На телефон»: тот же Smart DNS на iPhone (профиль по QR-коду или файлом) и Android («Частный DNS»).</summary>
internal sealed class PhoneWindow
{
    private static PhoneWindow? _open;
    /// <summary>Содержимое вкладки «На телефоне» окна «Помощь».</summary>
    public FrameworkElement View { get; }
    private readonly IReadOnlyList<DnsServer> _servers;
    private readonly StackPanel _body = new();
    private readonly WrapPanel _pick = new();
    private DnsServer? _selected;
    private readonly Action<string> _log;

    /// <summary>Страница для окна «Помощь» (одна на окно).</summary>
    public static FrameworkElement Create(IReadOnlyList<DnsServer> servers, Action<string> log) => (_open = new PhoneWindow(servers, log)).View;

    /// <summary>Окно «Помощь» закрыто: страница больше не нужна.</summary>
    public static void Forget() => _open = null;

    private PhoneWindow(IReadOnlyList<DnsServer> servers, Action<string> log)
    {
        _servers = servers; _log = log;
        var root = new StackPanel();

        var withDoh = servers.Where(s => !s.IsJunk && PhoneSetup.Doh(s) != null && s.Status is ServerStatus.Ok or ServerStatus.Partial)
                             .OrderBy(s => s.Status == ServerStatus.Ok ? 0 : 1).ThenByDescending(s => s.AllStability ?? 0).ToList();
        var best = PhoneSetup.Pick(servers, DnsSwitcher.GetActive()?.ServerId);
        // Выбор сервера кнопками-вкладками, как в остальных окнах программы: до восьми лучших с DoH
        _selected = best;
        foreach (var s in withDoh.Take(8))
        {
            var server = s;
            var rb = new RadioButton
            {
                Content = $"{DnsServer.Clip(s.DisplayName, 26)}{(s.AllStability is { } st ? $" · {st}%" : "")}", GroupName = "phone",
                IsChecked = s == best, Style = (Style)Application.Current.Resources["Tab"], Margin = new Thickness(0, 0, 6, 6),
            };
            rb.Checked += (_, _) => { _selected = server; Render(); };
            _pick.Children.Add(rb);
        }

        var choose = new StackPanel();
        choose.Children.Add(new TextBlock { Text = "Сервер для телефона", FontSize = 16, FontWeight = FontWeights.SemiBold });
        choose.Children.Add(Ui.Hint("Нейросети на iPhone и Android через тот же Smart DNS. Подходят только серверы с DoH", 2));
        choose.Children.Add(withDoh.Count == 0
            ? Ui.Hint("Рабочих серверов с DoH нет. Нажмите «Найти новые серверы» или проверьте список, затем откройте вкладку снова", 10, "Warn")
            : _pick.With(p => p.Margin = new Thickness(0, 12, 0, 0)));
        root.Children.Add(Ui.Card(choose));
        root.Children.Add(_body);
        View = root;
        Render();
    }

    private static Image QrImage(string text)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(PhoneSetup.Qr(text));
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        return new Image { Source = bmp, Width = 180, Height = 180, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 18, 0) };
    }

    private void Render()
    {
        _body.Children.Clear();
        if (_selected is not { } s) return;
        var host = PhoneSetup.DotHost(s)!;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // iPhone: профиль по QR-коду или файлом
        var ios = new StackPanel();
        ios.Children.Add(PlatformTitle("iPhone и iPad", "Профиль DNS, работает в Wi-Fi и мобильной сети"));
        ios.Children.Add(Qr(PhoneSetup.ProfileUrl(s)));
        ios.Children.Add(StepList(
            "Наведите камеру на QR-код и откройте ссылку в Safari: профиль скачается",
            "Настройки → «Профиль загружен» → «Установить». Предупреждение о подписи: это нормально",
            "Готово. Убрать: Основные → VPN и управление устройством"));
        ios.Children.Add(Ui.Action("Сохранить профиль файлом", "Strong", () =>
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = PhoneSetup.SaveProfile(s, dir);
            _log($"Профиль для iPhone сохранен: {path}");
            DnexDialog.Show("Профиль сохранен", $"{Path.GetFileName(path)} на рабочем столе. Отправьте его на iPhone через AirDrop, Telegram или почту и откройте там.", DialogKind.Info, "На телефон");
        }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 12, 0, 0); }));
        ios.Children.Add(Ui.Hint("QR-код не открывается: сохраните профиль файлом", 6).With(t => t.FontSize = 11));
        grid.Children.Add(Ui.Card(ios, bottom: 0).With(c => c.Padding = new Thickness(18, 16, 18, 16)));

        // Android: «Частный DNS» с именем хоста
        var android = new StackPanel();
        android.Children.Add(PlatformTitle("Android 9 и новее", "«Частный DNS» для всего телефона"));
        android.Children.Add(Qr(host));
        var hostRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var copy = Ui.Action("Скопировать", "Strong", () => { try { Clipboard.SetText(host); } catch (Exception) { } }).With(b => b.Margin = new Thickness(8, 0, 0, 0));
        DockPanel.SetDock(copy, Dock.Right); hostRow.Children.Add(copy);
        hostRow.Children.Add(new TextBox { Text = host, IsReadOnly = true, FontSize = 14 });
        android.Children.Add(hostRow);
        android.Children.Add(StepList(
            "Настройки → Сеть и интернет → «Частный DNS» (Samsung: Подключения → Другие настройки)",
            "«Имя хоста провайдера»: впишите адрес выше или отсканируйте QR-код",
            "Сохраните. DNS работает для всего телефона"));
        var state = Ui.Hint("Проверяю, отвечает ли сервер по DNS-over-TLS…", 8).With(t => t.FontSize = 11);
        android.Children.Add(state);
        var aCard = Ui.Card(android, bottom: 0).With(c => c.Padding = new Thickness(18, 16, 18, 16));
        Grid.SetColumn(aCard, 2); grid.Children.Add(aCard);
        _body.Children.Add(grid);
        _ = CheckDotAsync(host, state);
    }

    private static FrameworkElement PlatformTitle(string title, string sub)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Controls.Icon { Kind = "smartphone", Size = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, "Accent"));
        row.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(row);
        p.Children.Add(Ui.Hint(sub, 2).With(t => t.FontSize = 11));
        return p;
    }

    private static Border Qr(string text) => new()
    {
        Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center,
        Child = QrImage(text).With(i => i.Margin = new Thickness(0)),
    };

    /// <summary>Шаги номерами в кружках.</summary>
    private static FrameworkElement StepList(params string[] steps)
    {
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < steps.Length; i++)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var num = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
                                   Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Accent") }
                .Ref(Border.BackgroundProperty, "AccentSoft");
            g.Children.Add(num);
            var t = new TextBlock { Text = steps[i], TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18 };
            Grid.SetColumn(t, 1); g.Children.Add(t);
            list.Children.Add(g);
        }
        return list;
    }

    private static async Task CheckDotAsync(string host, TextBlock state)
    {
        bool ok = await PhoneSetup.DotWorks(host, CancellationToken.None);
        state.Text = ok ? $"{host} отвечает по DNS-over-TLS: Android его примет"
                        : $"{host} не ответил по DNS-over-TLS (порт 853). Android может не принять этот адрес: выберите другой сервер";
        state.Foreground = Ui.B(ok ? "Ok" : "Warn");
    }
}
