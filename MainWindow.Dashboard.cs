using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

public partial class MainWindow
{
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TextBlock _dashboardTitle = null!, _dashboardDescription = null!, _dashboardDns = null!, _dashboardNetwork = null!;
    private TextBlock _dashboardAi = null!, _dashboardZapret = null!, _dashboardTelegram = null!;
    private Button _dashboardConnect = null!, _dashboardZapretButton = null!, _dashboardTelegramButton = null!;
    private bool _dashboardToolBusy;

    private void SetupDashboard()
    {
        var page = new StackPanel { Margin = new Thickness(32, 24, 32, 28), MaxWidth = 1280, HorizontalAlignment = HorizontalAlignment.Stretch };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        var panel = Ui.Action("Панель управления", "Strong", ShowControlPanel);
        DockPanel.SetDock(panel, Dock.Right); top.Children.Add(panel);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Главная", FontSize = 28, FontWeight = FontWeights.Bold });
        heading.Children.Add(Ui.Hint("Состояние подключения и быстрые действия", 4));
        top.Children.Add(heading); page.Children.Add(top);

        var hero = new DockPanel();
        _dashboardConnect = Ui.Action("Подключить", "Primary", () => _ = DashboardConnectAsync());
        _dashboardConnect.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_dashboardConnect, Dock.Right); hero.Children.Add(_dashboardConnect);
        var heroText = new StackPanel();
        heroText.Children.Add(Ui.Section("Smart DNS", true));
        _dashboardTitle = new TextBlock { FontSize = 25, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        _dashboardDescription = Ui.Hint("", 6); heroText.Children.Add(_dashboardTitle); heroText.Children.Add(_dashboardDescription);
        hero.Children.Add(heroText); page.Children.Add(Ui.Card(hero, bottom: 4));

        page.Children.Add(Ui.Section("Подключение"));
        var connection = new Grid(); connection.ColumnDefinitions.Add(new ColumnDefinition()); connection.ColumnDefinitions.Add(new ColumnDefinition());
        _dashboardDns = DashboardCard(connection, 0, "DNS-сервер", "globe");
        _dashboardNetwork = DashboardCard(connection, 1, "Состояние сети", "activity");
        page.Children.Add(connection);
        page.Children.Add(Ui.Section("Доступ к нейросетям"));
        var ai = new StackPanel();
        _dashboardAi = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 25, FontSize = 14 };
        ai.Children.Add(_dashboardAi);
        var check = Ui.Action("Проверить подключение", "Strong", () => Probe_Click(this, new RoutedEventArgs()));
        check.Margin = new Thickness(0, 14, 0, 0); check.HorizontalAlignment = HorizontalAlignment.Left; ai.Children.Add(check);
        page.Children.Add(Ui.Card(ai, bottom: 0));
        page.Children.Add(Ui.Section("Дополнительные сервисы"));
        var tools = new StackPanel();
        _dashboardZapretButton = Ui.Action("Включить", "Strong", () => _ = DashboardToolAsync(_tools.OfType<ZapretTool>().First()));
        _dashboardZapret = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tools.Children.Add(DashboardFeature("YouTube и Discord", _dashboardZapret, _dashboardZapretButton));
        tools.Children.Add(Ui.Separator());
        _dashboardTelegramButton = Ui.Action("Включить", "Strong", () => _ = DashboardToolAsync(_tools.OfType<TgProxyTool>().First()));
        _dashboardTelegram = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tools.Children.Add(DashboardFeature("Telegram", _dashboardTelegram, _dashboardTelegramButton));
        page.Children.Add(Ui.Card(tools, bottom: 0));
        page.Children.Add(Ui.Section("Диагностика"));
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Action("Открыть диагностику", "Strong", () => DiagToggle_Click(this, new RoutedEventArgs())));
        actions.Children.Add(Ui.Action("Настройки", "Ghost", () => Settings_Click(this, new RoutedEventArgs())));
        actions.Children.Add(Ui.Action("Управление обходом", "Ghost", () => ToolsToggle_Click(this, new RoutedEventArgs())));
        page.Children.Add(Ui.Card(actions, bottom: 0));
        DashboardHost.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
        _dashboardTimer.Tick += (_, _) => RefreshDashboard();
        _dashboardTimer.Start();
        RefreshDashboard();
    }

    private static TextBlock DashboardCard(Grid grid, int column, string label, string icon)
    {
        var content = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Controls.Icon { Kind = icon, Size = 17, Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(Ui.Hint(label, 0)); content.Children.Add(header);
        var value = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(value);
        var card = Ui.Card(content, bottom: 0); card.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 0 ? 6 : 0, 0);
        Grid.SetColumn(card, column); grid.Children.Add(card); return value;
    }

    private static Grid DashboardFeature(string name, TextBlock status, Button button)
    {
        var content = new StackPanel(); content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
        status.Margin = new Thickness(0, 5, 0, 0); status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); content.Children.Add(status);
        return Ui.Row("", null, button).With(g => { g.Children.RemoveAt(0); g.Children.Insert(0, content); });
    }

    private void RefreshDashboard()
    {
        if (!IsVisible || DashboardHost.Visibility != Visibility.Visible) return;
        var active = DnsSwitcher.GetActive(); var snap = _traySnap ?? _after ?? _before;
        _dashboardTitle.Text = _busyConn ? "Подключение меняется" : active == null ? "Smart DNS отключен" : "Smart DNS подключен";
        _dashboardDescription.Text = active == null ? "Выберите рабочий сервер и подключитесь для доступа к сервисам." : "Состояние сервисов показано по последней проверке.";
        _dashboardConnect.Content = _busyConn ? "Подождите…" : active == null ? "Подключить" : "Отключить";
        _dashboardConnect.IsEnabled = !_busyConn;
        _dashboardDns.Text = active == null ? "Не подключен" : PrivacyGuard.Mask(active.Name + " · " + string.Join(", ", active.Addresses));
        _dashboardNetwork.Text = snap == null ? "Еще не проверялось" : PrivacyGuard.Mask("DNS системы: " + snap.SystemDns);
        _dashboardAi.Text = snap == null ? "ChatGPT  ·  еще не проверялось\nClaude  ·  еще не проверялось\nGemini  ·  еще не проверялось" :
            string.Join("\n", new[] { "ChatGPT", "Claude", "Gemini" }.Select(name => { var s = snap.Services.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); return name + "  ·  " + (s?.Access ?? "еще не проверялось"); })) + "\n" + $"Проверено: {snap.Taken:HH:mm}";
        var z = _tools.OfType<ZapretTool>().First(); var tg = _tools.OfType<TgProxyTool>().First();
        RefreshTool(z, _dashboardZapret, _dashboardZapretButton); RefreshTool(tg, _dashboardTelegram, _dashboardTelegramButton);
        _dashboardZapret.Text += "\n" + SiteStatus("YouTube", "youtube.com") + "  ·  " + SiteStatus("Discord", "discord.com");
    }

    private string SiteStatus(string name, string host)
    {
        if (!_sites.Sites.TryGetValue(host, out var site) || site.LastChecked == null) return name + ": не проверялся";
        return name + ": " + (site.State == SiteWatch.Verdict.Ok ? "доступен" : site.State == SiteWatch.Verdict.Unknown ? "нет результата" : "есть проблема") + $" ({site.LastChecked:dd.MM HH:mm})";
    }

    private void RefreshTool(Tool tool, TextBlock status, Button button)
    {
        bool running = tool.IsRunning();
        status.Text = tool is TgProxyTool proxy && proxy.ForeignCopyRunning() ? "Запущена сторонняя копия · управляется отдельно" : running ? "Инструмент запущен · доступность не проверялась" : tool.IsInstalled ? "Инструмент выключен" : "Инструмент не установлен";
        button.Content = running ? "Выключить" : "Включить"; button.IsEnabled = !_dashboardToolBusy && !(tool is TgProxyTool proxyTool && proxyTool.ForeignCopyRunning());
    }

    private async Task DashboardConnectAsync()
    {
        if (_busyConn) return;
        if (DnsSwitcher.GetActive() != null) await DisconnectByUserAsync();
        else { SelectBest(); if (ServerGrid.SelectedItem is DnsServer s) await ConnectToAsync(s); else { ShowControlPanel(); ShowToast("Сначала найдите рабочий сервер", "Warn"); } }
        RefreshDashboard();
    }
    private async Task DashboardToolAsync(Tool tool)
    {
        if (_dashboardToolBusy) return;
        _dashboardToolBusy = true; RefreshDashboard();
        try { await ToggleToolFromTrayAsync(tool); }
        catch (Exception ex) { Log($"{tool.Title}: {ex.Message}"); DnexDialog.Show("Не удалось изменить состояние", ex.Message, DialogKind.Warn); }
        finally { _dashboardToolBusy = false; RefreshDashboard(); }
    }
    private void ShowControlPanel() { DashboardHost.Visibility = Visibility.Collapsed; RootGrid.Visibility = Visibility.Visible; }
    private void DashboardHome_Click(object sender, RoutedEventArgs e) { if (_compact) SetCompact(false); RootGrid.Visibility = Visibility.Collapsed; DashboardHost.Visibility = Visibility.Visible; RefreshDashboard(); }
}
