using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Два DNS» одной карточкой на Главной: переключатель, выбор общего DNS и переход к подробным настройкам рядом. Раньше путь
/// был «Настройки, вкладка, отдельное окно, пресет, галочка, Сохранить» (6 действий), теперь выбор и включение прямо здесь.
/// Само изменение делает SetSplitDnsAsync (MainWindow.Palette.cs): предупреждение о групповых политиках, «Отменить»,
/// переподключение. ОТКАТ: откатом коммита с пометкой «ОТКАТ: карточка Два DNS» (git revert).
/// </summary>
public partial class MainWindow
{
    private Controls.Switch _dashboardSplitSwitch = null!;
    private Button _dashboardSplitPicker = null!;
    private Button _dashboardSplitMore = null!;
    private TextBlock _dashboardSplitHint = null!;

    private Border BuildDashboardSplit()
    {
        var icon = new Controls.Icon { Kind = "arrow-left-right", Size = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(Controls.Icon.ForegroundProperty, "Accent");
        var title = new TextBlock { Text = "Два DNS", FontSize = 15, FontWeight = FontWeights.SemiBold };
        _dashboardSplitHint = Ui.Hint("", 1).With(t => { t.FontSize = 12; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        texts.Children.Add(title); texts.Children.Add(_dashboardSplitHint);

        _dashboardSplitPicker = Ui.Picker("", OpenSplitPresetMenu).With(b =>
        {
            b.VerticalAlignment = VerticalAlignment.Center; b.Padding = new Thickness(12, 4, 12, 4); b.MinHeight = 30;
            b.ToolTip = "Какой DNS использовать для всего остального интернета. Нейросети идут через Smart DNS";
            AutomationProperties.SetName(b, "Общий DNS");
        });
        _dashboardSplitMore = DashboardHeaderButton("Подробнее", () => OpenSettingsTab("Split"));
        _dashboardSplitMore.Margin = new Thickness(8, 0, 0, 0);
        _dashboardSplitMore.ToolTip = "Свои домены и готовые наборы сервисов";
        _dashboardSplitSwitch = new Controls.Switch { Width = 44, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Включить или выключить «Два DNS»" };
        AutomationProperties.SetName(_dashboardSplitSwitch, "Два DNS");
        _dashboardSplitSwitch.Click += async (_, _) =>
        {
            if (_dashboardSyncing) return;
            if (_busyConn) { RefreshDashboardSplit(); return; } // занято: переключатель вернется на место
            try { await SetSplitDnsAsync(_dashboardSplitSwitch.IsChecked == true, null); }
            finally { RefreshDashboard(); }
        };

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        DockPanel.SetDock(_dashboardSplitSwitch, Dock.Right); row.Children.Add(_dashboardSplitSwitch);
        DockPanel.SetDock(_dashboardSplitMore, Dock.Right); row.Children.Add(_dashboardSplitMore);
        DockPanel.SetDock(_dashboardSplitPicker, Dock.Right); row.Children.Add(_dashboardSplitPicker);
        row.Children.Add(texts);
        var card = Ui.Card(row, bottom: 0);
        card.Padding = new Thickness(14, 8, 14, 8);
        card.Margin = new Thickness(0, 8, 0, 0);
        return card;
    }

    /// <summary>Меню общего DNS: шесть готовых вариантов, отмечен выбранный. Выбор у выключенного режима только запоминается.</summary>
    private void OpenSplitPresetMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var p in SplitDns.Presets)
        {
            var preset = p;
            var item = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Name == _settings.SplitDns.GeneralName, ToolTip = p.Note };
            item.Click += async (_, _) =>
            {
                try { await SetSplitDnsAsync(_settings.SplitDns.Enabled, preset); }
                finally { RefreshDashboard(); }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Состояние карточки по настройкам: включено ли, какой общий DNS, что это значит. Вызывается из RefreshDashboard.</summary>
    private void RefreshDashboardSplit()
    {
        if (_dashboardSplitSwitch == null) return;
        var s = _settings.SplitDns;
        _dashboardSyncing = true;
        _dashboardSplitSwitch.IsChecked = s.Enabled;
        _dashboardSyncing = false;
        _dashboardSplitSwitch.IsHitTestVisible = !_busyConn;
        var general = s.GeneralAddresses.Count == 0 ? "DNS вашей сети" : s.GeneralName;
        if (_dashboardSplitPicker.Content is StackPanel { Children.Count: > 0 } line && line.Children[0] is TextBlock text) text.Text = "Общий DNS: " + general;
        _dashboardSplitHint.Text = s.Enabled ? "Нейросети идут через Smart DNS, остальное через общий DNS" : "Выключено: весь интернет идет через один DNS";
    }
}
