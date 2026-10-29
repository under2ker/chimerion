using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Dnex.Services;

namespace Dnex;

/// <summary>Действия внутри окон: только идентификаторы элементов, без их текста и содержимого полей.</summary>
internal static class SupportTraceUi
{
    private static readonly DependencyProperty IdentityProperty = DependencyProperty.RegisterAttached(
        "Identity", typeof(string), typeof(SupportTraceUi), new PropertyMetadata(""));
    private static readonly ConditionalWeakTable<Window, object> Windows = new();
    private static readonly ConditionalWeakTable<Selector, object> Gestures = new();
    private static bool _installed;

    public static void Identify(DependencyObject element, string member, int line, string expression = "")
    {
        // Только строковый литерал из исходника. Переменные и подставленные значения не читаем.
        var caption = expression.Length >= 2 && expression[0] == '"' && expression[^1] == '"'
            && !expression[1..^1].Contains('"') ? $"; {expression}" : "";
        element.SetValue(IdentityProperty, $"{member}:{line}{caption}");
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(Click), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(Click), true);
        EventManager.RegisterClassHandler(typeof(Selector), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(Selection), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Loaded), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Mouse), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(Key), true);
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w || !ReferenceEquals(e.OriginalSource, w) || Windows.TryGetValue(w, out _)) return;
        Windows.Add(w, new object());
        SupportTrace.Write($"Окно открыто: {Describe(w)}");
        w.IsVisibleChanged += (_, _) => SupportTrace.Write($"Видимость окна: {Describe(w)}; видно={w.IsVisible}");
        w.StateChanged += (_, _) => SupportTrace.Write($"Состояние окна: {Describe(w)}; {w.WindowState}");
        w.Closing += (_, _) => SupportTrace.Write($"Запрошено закрытие окна: {Describe(w)}");
        w.Closed += (_, _) => SupportTrace.Write($"Окно закрыто: {Describe(w)}");
    }

    private static void Click(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source) || sender is not FrameworkElement element || !element.IsLoaded) return;
        if (element is RepeatButton) return;
        var state = element is ToggleButton t ? $"; включено={t.IsChecked?.ToString() ?? "не определено"}" : "";
        SupportTrace.Write($"Нажатие: {Describe(element)}{state}");
    }

    private static void Mouse(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var chain = Ancestors(e.OriginalSource as DependencyObject).ToList();
        MarkSelection(chain.OfType<Selector>().FirstOrDefault());
        // Кнопки и меню имеют собственное событие Click, а карточки часто работают через MouseDown.
        if (chain.Any(x => x is ButtonBase or MenuItem)) return;
        if (chain.OfType<FrameworkElement>().FirstOrDefault(x => x.Cursor == Cursors.Hand) is { } card)
            SupportTrace.Write($"Нажатие карточки: {Describe(card)}");
    }

    private static void Key(object sender, KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or System.Windows.Input.Key.Left or System.Windows.Input.Key.Right
            or System.Windows.Input.Key.Home or System.Windows.Input.Key.End or System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            MarkSelection(Ancestors(e.OriginalSource as DependencyObject).OfType<Selector>().FirstOrDefault());
        // Обычный ввод текста и комбинации с AltGr не записываются.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is System.Windows.Input.Key.S or System.Windows.Input.Key.F
            or System.Windows.Input.Key.C or System.Windows.Input.Key.A or System.Windows.Input.Key.Z)
            SupportTrace.Write($"Команда клавиатуры: {Describe(sender as DependencyObject)}; Ctrl+{e.Key}");
    }

    private static void MarkSelection(Selector? selector)
    {
        if (selector == null) return;
        var token = new object();
        Gestures.Remove(selector);
        Gestures.Add(selector, token);
        selector.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (Gestures.TryGetValue(selector, out var current) && ReferenceEquals(current, token)) Gestures.Remove(selector);
        }));
    }

    private static void Selection(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not Selector selector || !ReferenceEquals(e.Source, selector) || !Gestures.TryGetValue(selector, out _)) return;
        SupportTrace.Write($"Выбор: {Describe(selector)}; индекс={selector.SelectedIndex}; добавлено={e.AddedItems.Count}; снято={e.RemovedItems.Count}");
    }

    internal static string Describe(DependencyObject? element)
    {
        var parts = new List<string>();
        foreach (var node in Ancestors(element).Take(16))
        {
            var id = (string)node.GetValue(IdentityProperty);
            var name = node is FrameworkElement fe ? fe.Name : "";
            parts.Add(node.GetType().Name + (id.Length > 0 ? $"({id})" : name.Length > 0 ? $"#{name}" : $"[{Index(node)}]"));
            if (node is Window) break;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static int Index(DependencyObject node)
    {
        if (Parent(node) is not { } parent || parent is not Visual) return 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (ReferenceEquals(VisualTreeHelper.GetChild(parent, i), node)) return i;
        return 0;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? node)
    {
        for (int i = 0; node != null && i < 64; i++, node = Parent(node)) yield return node;
    }

    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node)
        ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
