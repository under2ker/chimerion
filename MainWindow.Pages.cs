using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Dnex;

/// <summary>
/// Одно левое меню на всю программу. Разделы (диагностика, настройки, журнал, тест скорости...) собираются как раньше,
/// в своем окне, но на экран выводится не окно, а его содержимое: страницей справа от меню. Само окно остается
/// невидимым хозяином страницы: его обработчики клавиш, Loaded и Closed работают как прежде, «Закрыть» и Esc
/// закрывают раздел и возвращают на главную или в панель управления.
/// </summary>
public partial class MainWindow
{
    private enum View { Home, Panel, Page }

    private readonly List<(Window Window, FrameworkElement Page)> _pages = new();
    private Window? _page;
    private bool _pageFromPanel;

    private void InitPages()
    {
        DnexDialog.Embed = EmbedPage;
        MarkNav(HomeButton);
    }

    private void ControlPanel_Click(object sender, RoutedEventArgs e) => ShowControlPanel();

    /// <summary>Главная, панель управления или открытый раздел: видна одна часть, пункт меню подсвечен.</summary>
    private void ShowView(View view)
    {
        if (view != View.Page && _page != null) { _page = null; PageHost.Content = null; }
        RootGrid.Visibility = view == View.Panel ? Visibility.Visible : Visibility.Collapsed;
        DashboardHost.Visibility = view == View.Home ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = view == View.Page ? Visibility.Visible : Visibility.Collapsed;
        if (view == View.Home) MarkNav(HomeButton);
        else if (view == View.Panel) MarkNav(ControlPanelButton);
    }

    /// <summary>Показать раздел страницей. false: главное окно скрыто (в трее), раздел откроется отдельным окном.</summary>
    private bool EmbedPage(Window w)
    {
        if (!IsVisible)
        {
            // Раздел уже страница (содержимое у главного окна): отдельным окном его не показать, выводим главное из трея
            if (!DnexDialog.Embedded.Contains(w)) return false;
            ShowFromTray();
        }
        if (_compact) SetCompact(false); // разделам нужна ширина полного окна
        var entry = _pages.FirstOrDefault(p => ReferenceEquals(p.Window, w));
        if (entry.Window == null)
        {
            // Тот же раздел, собранный заново (окна без единственного экземпляра): прежняя страница закрывается
            var key = DnexDialog.KeyOf(w);
            foreach (var old in _pages.Where(p => DnexDialog.KeyOf(p.Window) == key).ToList()) old.Window.Close();
            entry = (w, BuildPage(w));
            _pages.Add(entry);
            DnexDialog.Embedded.Add(w);
            w.Closed += (_, _) => OnPageClosed(w);
        }
        if (_page == null) _pageFromPanel = RootGrid.Visibility == Visibility.Visible;
        _page = w;
        PageHost.Content = entry.Page;
        ShowView(View.Page);
        MarkNav(NavFor(DnexDialog.KeyOf(w)));
        if (!entry.Page.IsLoaded) w.RaiseEvent(new RoutedEventArgs(LoadedEvent)); // подписчики Loaded окна ждут показа
        return true;
    }

    /// <summary>Предел ширины страницы раздела: шире строки и карточки читаются хуже.</summary>
    internal const double PageMaxWidth = 1400;

    /// <summary>Содержимое окна раздела в рамке страницы: вся ширина и высота справа от меню.</summary>
    private FrameworkElement BuildPage(Window w)
    {
        var content = (UIElement)w.Content;
        w.Content = null;
        // Окно с высотой по содержимому без своей прокрутки: прокручивается страница целиком
        bool scrolls = w.SizeToContent == SizeToContent.Manual || HasScroll(content);
        UIElement body = scrolls ? content : new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // Страница занимает всю область справа от меню, как главная; на очень широком экране строки не растягиваются бесконечно
        var frame = new Border
        {
            Child = body,
            MaxWidth = PageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8, 6, 8, 6),
        };
        if (w.Resources.Count > 0 || w.Resources.MergedDictionaries.Count > 0) frame.Resources = w.Resources; // стили из разметки окна (настройки)
        // Клавиши страницы получает и окно: его Esc, Ctrl+F и другие сочетания работают как в отдельном окне
        frame.PreviewKeyDown += (_, e) => Forward(w, e, Keyboard.PreviewKeyDownEvent);
        frame.KeyDown += (_, e) => Forward(w, e, Keyboard.KeyDownEvent);
        return frame;
    }

    private static void Forward(Window w, KeyEventArgs e, RoutedEvent routed)
    {
        if (e.Handled || !DnexDialog.Embedded.Contains(w) || e.KeyboardDevice == null || e.InputSource == null) return;
        var copy = new KeyEventArgs(e.KeyboardDevice, e.InputSource, e.Timestamp, e.Key) { RoutedEvent = routed };
        w.RaiseEvent(copy);
        if (copy.Handled) e.Handled = true;
    }

    private static bool HasScroll(DependencyObject root)
    {
        if (root is ScrollViewer or ListBox or DataGrid) return true;
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (HasScroll(c)) return true;
        return false;
    }

    private void OnPageClosed(Window w)
    {
        _pages.RemoveAll(p => ReferenceEquals(p.Window, w));
        DnexDialog.Embedded.Remove(w);
        if (!ReferenceEquals(_page, w)) return;
        ShowView(_pageFromPanel ? View.Panel : View.Home);
        if (!_pageFromPanel) RefreshDashboard();
    }

    /// <summary>Пункт меню раздела по его названию (без перевода).</summary>
    private FrameworkElement? NavFor(string key) => key switch
    {
        "Telegram и YouTube" => ToolsToggle,
        "Режим" => ModeToggle,
        "Диагностика" => DiagToggle,
        "Тест скорости" => SpeedButton,
        "Как вас видят сервисы" => AccountRiskButton,
        "Помощь" => HelpToggle,
        "Журнал" => LogToggle,
        "Настройки" => SettingsButton,
        "CHIMERION DNS" => ChimerionDnsButton,
        "Google Flow" => FlowButton,
        _ => null,
    };

    /// <summary>Подсветка открытого пункта меню: мягкий акцентный фон, как у выбранного пункта раньше на главной.</summary>
    private void MarkNav(FrameworkElement? selected)
    {
        foreach (var item in PanelNavItems.Children.OfType<FrameworkElement>())
        {
            bool on = ReferenceEquals(item, selected);
            if (item is ToggleButton toggle) toggle.IsChecked = on;
            else if (item is Button button)
            {
                if (on) button.SetResourceReference(BackgroundProperty, "AccentSoft");
                else button.ClearValue(BackgroundProperty);
            }
            if (item is Control control)
            {
                if (on) control.SetResourceReference(ForegroundProperty, "Accent");
                else control.ClearValue(ForegroundProperty);
            }
        }
    }
}
