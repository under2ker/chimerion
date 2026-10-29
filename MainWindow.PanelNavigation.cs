using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Dnex;

public partial class MainWindow
{
    private bool _panelNavExpanded = true;
    private bool _panelNavBeforeCompact = true;

    private void PanelCollapse_Click(object sender, RoutedEventArgs e) => SetPanelNavigation(!_panelNavExpanded);

    private void SetPanelNavigation(bool expanded)
    {
        _panelNavExpanded = expanded;
        PanelSidebar.Width = expanded ? 220 : 54;
        PanelCollapseText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        PanelCollapseIcon.Kind = expanded ? "shrink" : "expand";
        PanelCollapseIcon.Margin = new Thickness(0, 0, expanded ? 10 : 0, 0);
        PanelCollapseButton.ToolTip = expanded ? "Свернуть меню" : "Развернуть меню";
        AutomationProperties.SetName(PanelCollapseButton, expanded ? "Свернуть меню" : "Развернуть меню");
        foreach (var control in PanelNavItems.Children.OfType<ContentControl>())
        {
            if (control.Content is not StackPanel row) continue;
            foreach (var item in row.Children)
            {
                if (item is TextBlock text) text.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                else if (item is Dnex.Controls.Icon icon) icon.Margin = new Thickness(0, 0, expanded ? 10 : 0, 0);
            }
        }
        if (IsLoaded) ApplyResponsive();
    }
}
