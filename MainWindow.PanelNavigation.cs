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
        PanelCollapseIcon.Margin = new Thickness(0); // в кнопке одна иконка: отступ справа сдвигал ее от центра рамки
        PanelCollapseButton.ToolTip = expanded ? "Свернуть меню" : "Развернуть меню";
        AutomationProperties.SetName(PanelCollapseButton, expanded ? "Свернуть меню" : "Развернуть меню");
        foreach (var control in PanelNavItems.Children.OfType<ContentControl>())
        {
            if (control.Content is not StackPanel row) continue;
            foreach (var item in row.Children)
            {
                // Подпись не скрывается (Visibility): показанная заново внутри скрытого пункта, она не перерисовывалась.
                // В узком меню ее обрезает край панели и делает прозрачной
                if (item is TextBlock text) { text.Visibility = Visibility.Visible; text.Opacity = expanded ? 1 : 0; }
            }
        }
        SetUpdateCardsExpanded(expanded);
        if (IsLoaded) ApplyResponsive();
    }
}
