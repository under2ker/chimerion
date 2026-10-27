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
    private readonly Window _w;
    private readonly IReadOnlyList<DnsServer> _servers;
    private readonly StackPanel _body = new();
    private readonly WrapPanel _pick = new();
    private DnsServer? _selected;
    private readonly Action<string> _log;

    public static void Show(IReadOnlyList<DnsServer> servers, Window? owner, Action<string> log)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new PhoneWindow(servers, owner, log);
        _open._w.Show();
    }

    private PhoneWindow(IReadOnlyList<DnsServer> servers, Window? owner, Action<string> log)
    {
        _servers = servers; _log = log;
        _w = DnexDialog.CreateWindow("На телефон", Ui.Wide);
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        _w.Closed += (_, _) => _open = null;

        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Smart DNS на телефоне",
            "Нейросети на iPhone и Android через тот же сервер. Нужен сервер с DoH", glyph: "▯"));

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

        root.Children.Add(Ui.Section("Сервер", first: true));
        root.Children.Add(withDoh.Count == 0
            ? Ui.Hint("Рабочих серверов с DoH нет. Нажмите «Найти новые серверы» или проверьте список, затем откройте это окно снова", 0)
            : _pick);
        root.Children.Add(Ui.Section("Как поставить"));
        root.Children.Add(Ui.Card(_body, bottom: 0));
        var close = Ui.Action("Закрыть", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(null, close));
        _w.Content = Ui.WindowContent(root);
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

        // iPhone
        var ios = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var qr = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = QrImage(PhoneSetup.ProfileUrl(s)), Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(qr, Dock.Left);
        ios.Children.Add(qr);
        var iosText = new StackPanel();
        iosText.Children.Add(new TextBlock { Text = "iPhone и iPad", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        iosText.Children.Add(Ui.Steps("1. Наведите камеру на QR-код и откройте ссылку в Safari, профиль скачается.\n" +
                                      "2. Настройки → «Профиль загружен» → «Установить». iPhone предупредит, что профиль без подписи: это нормально.\n" +
                                      "3. Готово, DNS работает и в Wi-Fi, и в мобильной сети. Убрать: Основные → VPN и управление устройством."));
        var save = Ui.Action("Сохранить профиль файлом", "Ghost", () =>
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = PhoneSetup.SaveProfile(s, dir);
            _log($"Профиль для iPhone сохранен: {path}");
            DnexDialog.Show("Профиль сохранен", $"{Path.GetFileName(path)} на рабочем столе. Отправьте его на iPhone через AirDrop, Telegram или почту и откройте там.", DialogKind.Info, "На телефон");
        }).With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(-10, 8, 0, 0); });
        iosText.Children.Add(save);
        iosText.Children.Add(Ui.Hint("Если QR-код не открывается (ссылка ведет на сервер общей карты на workers.dev), сохраните профиль файлом", 6).With(t => t.FontSize = 11));
        ios.Children.Add(iosText);
        _body.Children.Add(ios);
        _body.Children.Add(Ui.Separator());

        // Android
        var android = new DockPanel();
        var qr2 = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = QrImage(host), Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(qr2, Dock.Left);
        android.Children.Add(qr2);
        var aText = new StackPanel();
        aText.Children.Add(new TextBlock { Text = "Android 9 и новее", FontWeight = FontWeights.SemiBold, FontSize = 15 });
        var hostBox = new TextBox { Text = host, IsReadOnly = true, Margin = new Thickness(0, 8, 0, 0), FontSize = 14 };
        aText.Children.Add(hostBox);
        aText.Children.Add(Ui.Steps("1. Настройки → Сеть и интернет → «Частный DNS» (на Samsung: Подключения → Другие настройки).\n" +
                                    "2. Выберите «Имя хоста провайдера» и впишите адрес выше или отсканируйте QR-код и скопируйте текст.\n" +
                                    "3. Сохраните. DNS работает для всего телефона."));
        var state = Ui.Hint("Проверяю, отвечает ли сервер по DNS-over-TLS…", 6).With(t => t.FontSize = 11);
        aText.Children.Add(state);
        var copy = Ui.Action("Скопировать адрес", "Ghost", () => { try { Clipboard.SetText(host); } catch (Exception) { } })
                     .With(b => { b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(-10, 6, 0, 0); });
        aText.Children.Add(copy);
        android.Children.Add(aText);
        _body.Children.Add(android);
        _ = CheckDotAsync(host, state);
    }

    private static async Task CheckDotAsync(string host, TextBlock state)
    {
        bool ok = await PhoneSetup.DotWorks(host, CancellationToken.None);
        state.Text = ok ? $"{host} отвечает по DNS-over-TLS: Android его примет"
                        : $"{host} не ответил по DNS-over-TLS (порт 853). Android может не принять этот адрес: выберите другой сервер";
        state.Foreground = Ui.B(ok ? "Ok" : "Warn");
    }
}
