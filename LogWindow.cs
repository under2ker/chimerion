using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Журнал»: записи программы с цветной меткой уровня, фильтр, поиск, копирование выбранных строк, одной строки
/// (кнопка у строки, двойной щелчок) и всего журнала за 3 дня, сохранение в файл. Внизу подсказка по сочетаниям
/// клавиш. Записи берутся из общего списка главного окна: новые появляются сами.
/// </summary>
internal sealed class LogWindow
{
    /// <summary>
    /// Строка окна: запись без служебной метки уровня ([x], [!]) и подряд идущие одинаковые записи одной строкой со
    /// счетчиком и временем последней. В файл, отчет и копию за 3 дня идут исходные записи.
    /// </summary>
    internal sealed class LogRow : INotifyPropertyChanged
    {
        public LogRow(LogEntry e) { Last = e; Text = Clean(e.Text); Level = e.Level; }
        public LogEntry Last { get; private set; }
        public int Count { get; private set; } = 1;
        public string Text { get; }
        public LogLevel Level { get; }
        public string DateText => Last.DateText;
        public string CountText => Count > 1 ? $"×{Count}" : "";
        public string CountTip => Count > 1 ? $"Повторилось {Count} раз подряд, последний раз в {Last.Time:HH:mm:ss}" : "";
        public bool Same(LogEntry e) => e.Level == Level && Clean(e.Text) == Text;
        public void Add(LogEntry e)
        {
            Last = e; Count++;
            foreach (var p in new[] { nameof(DateText), nameof(CountText), nameof(CountTip) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
        /// <summary>Для копии: последняя запись и сколько раз она повторилась.</summary>
        public LogEntry ForCopy => Count > 1 ? new LogEntry(Last.Time, Last.Text + $" (повторилось {Count} раз)") : Last;
        public event PropertyChangedEventHandler? PropertyChanged;

        public static string Clean(string text)
        {
            var t = text.Trim();
            foreach (var mark in new[] { "[x]", "[!]", "[ок]", "[i]" })
                if (t.StartsWith(mark)) return t[mark.Length..].TrimStart();
            return t;
        }
    }

    private static LogWindow? _open;

    private readonly Window _w;
    private readonly ObservableCollection<LogEntry> _log;
    private readonly Func<List<LogEntry>> _stored;
    private readonly Action _clear;
    private readonly ObservableCollection<LogRow> _rows = new();
    private readonly ListCollectionView _view;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Tag = "Поиск по журналу", MinWidth = 220 };
    private readonly TextBlock _count = Ui.Hint("", 0);
    private readonly TextBlock _done = Ui.Hint("", 0, "Ok");
    private readonly Button _copySelected;
    private readonly List<(RadioButton Tab, int Filter, string Title)> _tabs = new();
    private readonly System.Windows.Threading.DispatcherTimer _doneTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _filter; // 0 все, 1 успех, 2 предупреждения, 3 ошибки

    public static void Show(Window? owner, ObservableCollection<LogEntry> log, Func<List<LogEntry>> stored, Action clear)
    {
        if (_open != null)
        {
            if (_open._w.WindowState == WindowState.Minimized) _open._w.WindowState = WindowState.Normal;
            _open._w.Activate();
            return;
        }
        _open = new LogWindow(owner, log, stored, clear);
        _open._w.Show();
        _open.ScrollToEnd();
    }

    /// <summary>Строки для буфера обмена и файла: время (с датой для журнала за 3 дня) и текст, личные данные скрыты.</summary>
    public static string Format(IEnumerable<LogEntry> items, bool withDate) =>
        PrivacyGuard.ForExport(string.Join(Environment.NewLine,
            items.Select(x => $"[{(withDate ? x.Time.ToString("dd.MM HH:mm:ss") : x.TimeText)}] {x.Text}")));

    private LogWindow(Window? owner, ObservableCollection<LogEntry> log, Func<List<LogEntry>> stored, Action clear)
    {
        _log = log; _stored = stored; _clear = clear;
        _w = DnexDialog.CreateWindow("Журнал", 1000);
        _w.SizeToContent = SizeToContent.Manual;
        _w.Height = Math.Min(720, SystemParameters.WorkArea.Height - 60);
        _w.ResizeMode = ResizeMode.CanResize;
        _w.MinWidth = 760; _w.MinHeight = 480;
        _w.ShowInTaskbar = true; // окно держат открытым рядом с другими: его должно быть видно в панели задач
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        _w.Closed += (_, _) => { _log.CollectionChanged -= OnLogChanged; _open = null; };

        Rebuild();
        _view = new ListCollectionView(_rows) { Filter = o => o is LogRow r && Matches(r.Level, r.Text) };
        _log.CollectionChanged += OnLogChanged;

        var root = new Grid { Margin = new Thickness(24, 20, 24, 18) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });

        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Журнал",
            "Что делала программа за последние 3 дня. IP-адреса, город и имя пользователя Windows из записей удаляются", glyph: "scroll-text"));

        // Фильтр, поиск и число записей
        var bar = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        _count.VerticalAlignment = VerticalAlignment.Center; _count.Margin = new Thickness(14, 0, 0, 0);
        DockPanel.SetDock(_count, Dock.Right); bar.Children.Add(_count);
        _search.VerticalAlignment = VerticalAlignment.Center; _search.Margin = new Thickness(12, 0, 0, 0);
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _search.Text = ""; _list.Focus(); } };
        DockPanel.SetDock(_search, Dock.Right); bar.Children.Add(_search);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (title, f) in new[] { ("Все", 0), ("Успех", 1), ("Предупреждения", 2), ("Ошибки", 3) })
        {
            var rb = new RadioButton { GroupName = "logwin", IsChecked = f == 0, Content = title, Style = (Style)Application.Current.Resources["Tab"] };
            var filter = f;
            rb.Checked += (_, _) => { _filter = filter; Refresh(); };
            tabs.Children.Add(rb);
            _tabs.Add((rb, f, title));
        }
        bar.Children.Add(new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = tabs, HorizontalAlignment = HorizontalAlignment.Left }
            .Ref(Border.BackgroundProperty, "Surface2").Ref(Border.BorderBrushProperty, "Border"));
        Grid.SetRow(bar, 1); root.Children.Add(bar);

        // Записи
        _list.ItemsSource = _view;
        _list.SelectionMode = SelectionMode.Extended;
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        System.Windows.Automation.AutomationProperties.SetName(_list, "Записи журнала");
        _list.ItemTemplate = (DataTemplate)XamlReader.Parse(ItemXaml);
        _list.ItemContainerStyle = (Style)XamlReader.Parse(ContainerXaml);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem { DataContext: LogRow one })
                Copy(new[] { one.ForCopy }, false, "Запись скопирована");
        };
        // Кнопка «скопировать» у строки
        _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is Button { Tag: "copy", DataContext: LogRow one }) { Copy(new[] { one.ForCopy }, false, "Запись скопирована"); e.Handled = true; }
        }));
        var empty = Ui.Hint("Записей нет", 0).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; t.Visibility = Visibility.Collapsed; });
        var listLayer = new Grid();
        listLayer.Children.Add(_list); listLayer.Children.Add(empty);
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => empty.Visibility = _view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var listCard = Ui.Card(listLayer, bottom: 0);
        listCard.Padding = new Thickness(8, 8, 4, 8);
        Grid.SetRow(listCard, 2); root.Children.Add(listCard);

        // Действия
        _copySelected = Ui.Action("Скопировать выбранные", "Strong", CopySelected);
        var actions = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(IconButton("Сохранить в файл", "download", "Ghost", SaveToFile));
        right.Children.Add(IconButton("Папка журналов", "external-link", "Ghost", () => AutoStart.OpenUrl(LogStore.Dir)));
        right.Children.Add(IconButton("Очистить", "x", "Ghost", () => { _clear(); Done("Журнал очищен, вернуть можно в главном окне"); }).With(b => b.Margin = new Thickness(0)));
        DockPanel.SetDock(right, Dock.Right); actions.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(IconButton("Скопировать журнал за 3 дня", "copy", "Primary", CopyAll)
            .With(b => b.ToolTip = "Все записи за 3 дня из всех запусков программы, с учетом фильтра и поиска. Повторы не схлопываются"));
        left.Children.Add(_copySelected);
        _done.VerticalAlignment = VerticalAlignment.Center; _done.Opacity = 0;
        left.Children.Add(_done);
        actions.Children.Add(left);
        Grid.SetRow(actions, 3); root.Children.Add(actions);

        // Подсказка по клавишам
        var keys = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (combo, what) in new[]
                 {
                     (new[] { "Ctrl", "C" }, "скопировать выбранные"),
                     (new[] { "Ctrl", "Shift", "C" }, "весь журнал за 3 дня"),
                     (new[] { "Ctrl", "A" }, "выделить все"),
                     (new[] { "Ctrl", "F" }, "поиск"),
                     (new[] { "Shift", "щелчок" }, "выделить подряд"),
                     (new[] { "Ctrl", "щелчок" }, "выделить по одной"),
                     (new[] { "Двойной щелчок" }, "скопировать строку"),
                 })
            keys.Children.Add(Shortcut(combo, what));
        var keysBox = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 4, 0, 0), Child = keys }.Ref(Border.BorderBrushProperty, "Border");
        Grid.SetRow(keysBox, 4); root.Children.Add(keysBox);

        _w.Content = root;
        _w.PreviewKeyDown += OnKey;
        _doneTimer.Tick += (_, _) => { _doneTimer.Stop(); _done.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, Ui.Motion(400))); };
        Refresh();
    }

    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                              "xmlns:c='clr-namespace:Dnex.Controls;assembly=CHIMERION'";

    // Строка: время, метка уровня, текст, кнопка копирования (видна при наведении и у выбранной)
    private const string ItemXaml = "<DataTemplate " + Ns + @">
  <Grid Margin='0,1,0,1'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='118'/><ColumnDefinition Width='60'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='34'/>
    </Grid.ColumnDefinitions>
    <TextBlock Text='{Binding DateText}' FontFamily='Cascadia Mono, Consolas' FontSize='12' Foreground='{DynamicResource Muted}' Margin='0,3,0,0' VerticalAlignment='Top'/>
    <Border x:Name='chip' Grid.Column='1' CornerRadius='5' Padding='6,1,6,2' HorizontalAlignment='Left' VerticalAlignment='Top' Margin='0,2,0,0' Background='{DynamicResource Surface2}'>
      <TextBlock x:Name='tag' Text='INFO' FontFamily='Cascadia Mono, Consolas' FontSize='10.5' FontWeight='SemiBold' Foreground='{DynamicResource Muted}'/>
    </Border>
    <TextBlock x:Name='txt' Grid.Column='2' Text='{Binding Text}' TextWrapping='Wrap' TextTrimming='WordEllipsis' MaxHeight='40' FontFamily='Cascadia Mono, Consolas' FontSize='13' LineHeight='20'
               Foreground='{DynamicResource Text}' Margin='6,1,8,1'/>
    <Border Grid.Column='3' CornerRadius='6' Padding='6,1,6,2' Margin='0,2,6,0' VerticalAlignment='Top' Background='{DynamicResource Surface2}' ToolTip='{Binding CountTip}'>
      <Border.Style><Style TargetType='Border'><Style.Triggers><DataTrigger Binding='{Binding CountText}' Value=''><Setter Property='Visibility' Value='Collapsed'/></DataTrigger></Style.Triggers></Style></Border.Style>
      <TextBlock Text='{Binding CountText}' FontSize='11.5' FontWeight='SemiBold' Foreground='{DynamicResource Muted}'/>
    </Border>
    <Button x:Name='copy' Grid.Column='4' Tag='copy' ToolTip='Скопировать запись' Visibility='Hidden' Style='{DynamicResource Ghost}'
            Padding='5' MinHeight='0' MinWidth='0' Margin='0' VerticalAlignment='Top' Cursor='Hand'>
      <c:Icon Kind='copy' Size='14'/>
    </Button>
  </Grid>
  <DataTemplate.Triggers>
    <DataTrigger Binding='{Binding Level}' Value='Ok'>
      <Setter TargetName='chip' Property='Background' Value='{DynamicResource ChipOk}'/>
      <Setter TargetName='tag' Property='Text' Value='OK'/>
      <Setter TargetName='tag' Property='Foreground' Value='{DynamicResource Ok}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Level}' Value='Warn'>
      <Setter TargetName='chip' Property='Background' Value='{DynamicResource ChipWarn}'/>
      <Setter TargetName='tag' Property='Text' Value='WARN'/>
      <Setter TargetName='tag' Property='Foreground' Value='{DynamicResource Warn}'/>
      <Setter TargetName='txt' Property='Foreground' Value='{DynamicResource Warn}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding Level}' Value='Error'>
      <Setter TargetName='chip' Property='Background' Value='{DynamicResource ChipBad}'/>
      <Setter TargetName='tag' Property='Text' Value='ERR'/>
      <Setter TargetName='tag' Property='Foreground' Value='{DynamicResource Bad}'/>
      <Setter TargetName='txt' Property='Foreground' Value='{DynamicResource Bad}'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
      <Setter TargetName='copy' Property='Visibility' Value='Visible'/>
    </DataTrigger>
    <DataTrigger Binding='{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}' Value='True'>
      <Setter TargetName='copy' Property='Visibility' Value='Visible'/>
      <Setter TargetName='txt' Property='MaxHeight' Value='100000'/>
    </DataTrigger>
  </DataTemplate.Triggers>
</DataTemplate>";

    private const string ContainerXaml = "<Style TargetType='ListBoxItem' " + Ns + @">
  <Setter Property='Padding' Value='0'/>
  <Setter Property='Margin' Value='0'/>
  <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ListBoxItem'>
        <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='8,3,4,3'>
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='{DynamicResource Surface2}'/>
          </Trigger>
          <Trigger Property='IsSelected' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='{DynamicResource Selection}'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    private bool Matches(LogLevel lvl, string text)
    {
        bool level = _filter switch
        {
            1 => lvl == LogLevel.Ok,
            2 => lvl is LogLevel.Warn,
            3 => lvl == LogLevel.Error,
            _ => true,
        };
        var q = _search.Text.Trim();
        return level && (q.Length == 0 || text.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Строки окна заново из записей: подряд идущие одинаковые одной строкой.</summary>
    private void Rebuild()
    {
        _rows.Clear();
        foreach (var e in _log) Append(e);
    }

    private void Append(LogEntry e)
    {
        if (_rows.Count > 0 && _rows[^1].Same(e)) _rows[^1].Add(e);
        else _rows.Add(new LogRow(e));
    }

    private void Refresh()
    {
        _view.Refresh();
        UpdateCounts();
        UpdateButtons();
    }

    private void UpdateCounts()
    {
        foreach (var (tab, f, title) in _tabs)
        {
            int n = f switch
            {
                1 => _log.Count(x => x.Level == LogLevel.Ok),
                2 => _log.Count(x => x.Level == LogLevel.Warn),
                3 => _log.Count(x => x.Level == LogLevel.Error),
                _ => _log.Count,
            };
            tab.Content = n > 0 && f != 0 ? $"{title} {n}" : title;
        }
        // Сколько строк видно сейчас и сколько записей в этом запуске: копия за 3 дня берет больше, это сказано у кнопки
        _count.Text = _view.Count == _rows.Count ? $"{_rows.Count} строк · {_log.Count} записей в этом запуске" : $"Показано {_view.Count} из {_rows.Count} строк";
    }

    private void UpdateButtons()
    {
        int n = _list.SelectedItems.Count;
        _copySelected.IsEnabled = n > 0;
        _copySelected.Content = n > 1 ? $"Скопировать выбранные ({n})" : "Скопировать выбранные";
    }

    /// <summary>Новые записи: счетчики обновляются, список едет вниз, если его не листали вверх.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var sv = FindScroll(_list);
        bool atEnd = sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 24;
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null) foreach (LogEntry n in e.NewItems) Append(n);
        else Rebuild();
        UpdateCounts();
        if (atEnd && e.Action == NotifyCollectionChangedAction.Add) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_view.Count > 0) _list.ScrollIntoView(_view.GetItemAt(_view.Count - 1));
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.C) { CopyAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && !_search.IsKeyboardFocused) { CopySelected(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A && !_search.IsKeyboardFocused) { _list.SelectAll(); _list.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape && !_search.IsKeyboardFocused) _w.Close();
    }

    /// <summary>Выбранные строки, которые сейчас видны, в порядке журнала.</summary>
    private void CopySelected()
    {
        var sel = _view.Cast<LogRow>().Where(x => _list.SelectedItems.Contains(x)).Select(x => x.ForCopy).ToList();
        if (sel.Count == 0) { Done("Выберите строки щелчком или Ctrl+A", warn: true); return; }
        Copy(sel, false, sel.Count == 1 ? "Запись скопирована" : $"Скопировано записей: {sel.Count}");
    }

    /// <summary>Весь журнал с диска за 3 дня (все запуски программы), с учетом фильтра и поиска.</summary>
    private void CopyAll()
    {
        var all = AllForExport();
        Copy(all, true, $"Журнал за 3 дня скопирован: {all.Count} строк");
    }

    private List<LogEntry> AllForExport()
    {
        var all = _stored();
        if (all.Count == 0) all = _log.ToList();
        return all.Where(x => Matches(x.Level, x.Text)).ToList();
    }

    private void Copy(IReadOnlyCollection<LogEntry> items, bool withDate, string done)
    {
        var text = Format(items, withDate);
        if (text.Length == 0) { Done("Копировать нечего", warn: true); return; }
        if (Ui.CopyText(text)) Done(done);
        else Done("Буфер обмена занят другой программой: сохраните журнал в файл", warn: true);
    }

    private void SaveToFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"chimerion-log-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", Filter = "Текст (*.txt)|*.txt", DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(_w) != true) return;
        try
        {
            var all = AllForExport();
            File.WriteAllText(dlg.FileName, Format(all, true));
            Done($"Сохранено строк: {all.Count}");
        }
        catch (Exception ex) { Done("Не удалось сохранить: " + ex.Message, warn: true); }
    }

    /// <summary>Итог действия рядом с кнопками: появляется и через 3 с гаснет.</summary>
    private void Done(string text, bool warn = false)
    {
        _done.Text = text;
        _done.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Ok");
        _done.Margin = new Thickness(6, 0, 0, 0);
        _done.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Ui.Motion(180)));
        _doneTimer.Stop(); _doneTimer.Start();
    }

    private static Button IconButton(string text, string icon, string style, Action click)
    {
        var b = Ui.Action(text, style, click);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Controls.Icon { Kind = icon, Size = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        b.Content = row;
        return b;
    }

    /// <summary>Сочетание клавиш «клавишами» и что оно делает.</summary>
    private static FrameworkElement Shortcut(string[] keys, string what)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 18, 0) };
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0) row.Children.Add(Ui.Hint("+", 0).With(t => { t.Margin = new Thickness(4, 0, 4, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = keys[i], FontSize = 11, FontWeight = FontWeights.SemiBold },
            }.Ref(Border.BackgroundProperty, "Surface2").Ref(Border.BorderBrushProperty, "ControlBorder"));
        }
        row.Children.Add(Ui.Hint(what, 0).With(t => { t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        return row;
    }
}
