using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Dnex;

/// <summary>
/// Часовой пояс на Главной: какой пояс сейчас у Windows и сколько там времени, кнопка «Выбрать пояс» открывает то же окно
/// «Часовой пояс», что и страница «Аккаунты» (браузер, Claude Code, приложения, вся Windows). Раньше путь был «Аккаунты, Выбрать
/// пояс». ОТКАТ: откатом коммита с пометкой «ОТКАТ: журнал и часовой пояс на Главной» (git revert).
/// </summary>
public partial class MainWindow
{
    private TextBlock _dashboardZoneHint = null!;

    private DockPanel BuildDashboardZone()
    {
        var icon = new Controls.Icon { Kind = "clock", Size = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(Controls.Icon.ForegroundProperty, "Accent");
        var title = new TextBlock { Text = "Часовой пояс", FontSize = 16, FontWeight = FontWeights.SemiBold };
        _dashboardZoneHint = Ui.Hint("", 1).With(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.Wrap; });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(title); titleLine.Children.Add(Ui.HelpMark("Часовой пояс", "Какое время видят Claude, ChatGPT и сайты в браузере. Пояс можно сменить для вкладки браузера, для приложений или для всей Windows, например под страну, из которой нейросети вас видят."));
        texts.Children.Add(titleLine); texts.Children.Add(_dashboardZoneHint);
        var choose = DashboardHeaderButton("Выбрать пояс", () => TimeZoneWindow.Show(Log));
        choose.ToolTip = "Какое время показывать Claude Code, Claude в браузере и всей Windows";
        AutomationProperties.SetName(choose, "Выбрать часовой пояс");

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
        DockPanel.SetDock(choose, Dock.Right); row.Children.Add(choose);
        row.Children.Add(texts);
        return row;
    }

    /// <summary>Пояс Windows и местное время в нем. Вызывается из RefreshDashboard (раз в несколько секунд).</summary>
    private void RefreshDashboardZone()
    {
        if (_dashboardZoneHint == null) return;
        TimeZoneInfo.ClearCachedData(); // пояс могли сменить снаружи (Windows, tzutil)
        var zone = TimeZoneInfo.Local;
        _dashboardZoneHint.Text = $"{zone.DisplayName} · сейчас {TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone):HH:mm}";
    }
}
