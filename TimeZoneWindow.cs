using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Dnex.Services;

namespace Dnex;

internal static class TimeZoneWindow
{
    internal const string Title = "Часовой пояс";
    private const string SearchLabel = "Поиск часового пояса";

    private enum Scope { Browser, Code, Apps, Windows }
    private sealed record CountryOption(string Code, string Name);

    private sealed record ZoneRow(TimeZonePlaces.Choice Zone, string Offset)
    {
        public string Name => Zone.Name;
        public string Country => Zone.Country;
        public string Id => Zone.Id;
        public string Label => Zone.Label;
    }

    internal static void Show(Action<string> log) => DnexDialog.Present(Build(log));

    private static Window Build(Action<string> log)
    {
        var window = DnexDialog.CreateWindow(Title, 880);
        window.MaxHeight = Ui.WorkAreaOf(window.Owner).Height;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Controls/TimeZoneStyles.xaml", UriKind.Relative),
        });

        var root = Ui.Root();
        var dock = new DockPanel { Margin = root.Margin, LastChildFill = true };
        root.Margin = new Thickness(0);
        root.Children.Add(DnexDialog.Header(DialogKind.Info, Loc.T(Title),
            Loc.T("Посмотрите время в нужном поясе перед применением"), glyph: "globe"));

        Scope scope = Scope.Browser;
        var scopePicker = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -8, 8) };
        var scopeStyle = (Style)window.Resources["TimeZoneScope"];
        var browser = ScopeTile(scopeStyle, "В браузере", "Claude, ChatGPT и Gemini", "globe");
        var code = ScopeTile(scopeStyle, "Claude Code", "На время сеанса", "scroll-text");
        var apps = ScopeTile(scopeStyle, "Приложения Windows", "Через системный пояс", "layout-dashboard");
        var windows = ScopeTile(scopeStyle, "Вся Windows", "Все программы", "settings");
        var scopeOptions = new[] { (browser, Scope.Browser), (code, Scope.Code), (apps, Scope.Apps), (windows, Scope.Windows) };
        foreach (var (option, _) in scopeOptions) scopePicker.Children.Add(option);
        browser.IsChecked = true;
        scopePicker.SizeChanged += (_, e) => scopePicker.Columns = e.NewSize.Width >= 900 ? 4 : 2;
        root.Children.Add(Ui.Section(Loc.T("Где изменить время"), first: true));
        root.Children.Add(scopePicker);

        bool english = Loc.Lang == "en";
        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "ru-RU");
        DateTimeOffset UtcNow() => Storage.TestCopy
            ? new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        TimeZoneInfo CurrentZone() => Storage.TestCopy ? TimeZoneInfo.Utc : TimeZoneInfo.Local;

        var places = TimeZonePlaces.List(english);
        var selected = TimeZonePlaces.Default(places, CurrentZone().Id) ?? places.FirstOrDefault();
        var allCountries = new CountryOption("", Loc.T("Все страны"));
        var countries = places.Where(place => place.CountryCode.Length > 0)
            .Select(place => new CountryOption(place.CountryCode, place.Country))
            .Distinct().OrderBy(option => option.Name, StringComparer.Create(culture, ignoreCase: true));
        var countryPicker = new ComboBox
        {
            ItemsSource = countries.Prepend(allCountries).ToArray(),
            SelectedItem = allCountries,
            DisplayMemberPath = nameof(CountryOption.Name),
            Style = (Style)window.Resources["TimeZoneCountryPicker"],
            Width = 155,
            Margin = new Thickness(8, 0, 0, 0),
        };
        TextSearch.SetTextPath(countryPicker, nameof(CountryOption.Name));
        AutomationProperties.SetName(countryPicker, Loc.T("Страна"));
        AutomationProperties.SetAutomationId(countryPicker, "time-zone-country");
        ZoneRow[] rows = [];
        bool WindowsScope() => scope is Scope.Apps or Scope.Windows;
        var search = new TextBox { Tag = Loc.T("Город, страна или UTC"), Padding = new Thickness(34, 7, 38, 7) };
        AutomationProperties.SetName(search, Loc.T(SearchLabel));
        var searchBox = new Grid { Margin = new Thickness(0, 10, 0, 8) };
        searchBox.Children.Add(search);
        searchBox.Children.Add(new Controls.Icon
        {
            Kind = "search", Size = 15, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false,
        }.Ref(Controls.Icon.ForegroundProperty, "Muted"));

        var clearSearch = Ui.Action("", "Ghost", () => { search.Clear(); search.Focus(); });
        clearSearch.Content = new Controls.Icon { Kind = "x", Size = 14, Animated = false };
        clearSearch.Width = 28;
        clearSearch.MinHeight = 0;
        clearSearch.Padding = new Thickness(0);
        clearSearch.Margin = new Thickness(0, 4, 5, 4);
        clearSearch.HorizontalAlignment = HorizontalAlignment.Right;
        clearSearch.Visibility = Visibility.Collapsed;
        clearSearch.ToolTip = Loc.T("Очистить поиск");
        AutomationProperties.SetName(clearSearch, Loc.T("Очистить поиск"));
        searchBox.Children.Add(clearSearch);

        var zonePicker = new ListBox
        {
            Height = 230,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemContainerStyle = (Style)window.Resources["TimeZoneItem"],
            ItemTemplate = (DataTemplate)window.Resources["TimeZoneRow"],
        }.Ref(Control.ForegroundProperty, "Text");
        ScrollViewer.SetHorizontalScrollBarVisibility(zonePicker, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(zonePicker, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(zonePicker, Loc.T(Title));
        zonePicker.Loaded += (_, _) => ScrollToSelected();

        var empty = Ui.Hint(Loc.T("Пояса не найдены. Попробуйте другой город, страну или смещение UTC."), 0);
        empty.Margin = new Thickness(10, 18, 10, 0);
        empty.Visibility = Visibility.Collapsed;
        var zoneResults = new Grid();
        zoneResults.Children.Add(zonePicker);
        zoneResults.Children.Add(empty);
        var picker = new StackPanel();
        var pickerHeader = new DockPanel();
        DockPanel.SetDock(countryPicker, Dock.Right);
        pickerHeader.Children.Add(countryPicker);
        pickerHeader.Children.Add(Heading("Город и страна").With(label => label.VerticalAlignment = VerticalAlignment.Center));
        picker.Children.Add(pickerHeader);
        picker.Children.Add(searchBox);
        picker.Children.Add(zoneResults);

        var previewName = Heading("");
        var time = new TextBlock { FontSize = 50, FontWeight = FontWeights.SemiBold };
        System.Windows.Documents.Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        var date = Ui.Hint("", 0);
        var localTime = Heading("");
        localTime.HorizontalAlignment = HorizontalAlignment.Right;
        var localRow = new DockPanel();
        DockPanel.SetDock(localTime, Dock.Right);
        localRow.Children.Add(localTime);
        localRow.Children.Add(Ui.Hint(Loc.T("Время в Windows"), 0).With(t => t.VerticalAlignment = VerticalAlignment.Center));

        var clock = new StackPanel();
        clock.Children.Add(Ui.Section(Loc.T("Время в выбранном поясе"), first: true).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
        clock.Children.Add(previewName);
        clock.Children.Add(time);
        clock.Children.Add(date);
        clock.Children.Add(new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 9, 0, 0), Child = localRow,
        }.Ref(Border.BorderBrushProperty, "Border"));

        var guide = Ui.Hint("", 4);
        var guideDetails = Ui.Hint("", 4);
        var preview = new StackPanel();
        preview.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 12, 14, 12), Child = clock,
        }.Ref(Border.BackgroundProperty, "Surface2"));
        preview.Children.Add(Heading("Как применить").With(t => t.Margin = new Thickness(0, 14, 0, 0)));
        preview.Children.Add(guide);
        preview.Children.Add(guideDetails);

        var columns = Ui.Columns(Ui.Card(picker, bottom: 0), Ui.Card(preview, bottom: 0), 625, 1.17, 1);
        root.Children.Add(columns);

        bool busy = false;
        bool filtering = false;
        var status = Ui.Status();
        status.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Button action = null!;
        action = Ui.Action("Скопировать команду", "Primary", () => _ = ApplyAsync());
        var close = Ui.Action(Loc.T("Закрыть"), "Ghost", window.Close).With(b => b.IsCancel = true);
        AutomationProperties.SetName(close, Loc.T("Закрыть выбор пояса"));
        var footer = Ui.Footer(status, action, close);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dock.Children.Add(scroll);
        window.Content = dock;
        dock.SizeChanged += (_, e) => UpdateResultsHeight(e.NewSize.Height);

        void UpdateResultsHeight(double availableHeight)
        {
            int maxRows = availableHeight >= 820 ? 7 : 5;
            zonePicker.Height = 46 * Math.Clamp(zonePicker.Items.Count, 2, maxRows);
        }

        void ScrollToSelected()
        {
            if (zonePicker.SelectedItem is { } item) zonePicker.ScrollIntoView(item);
        }

        void RefreshClock()
        {
            var now = UtcNow();
            var local = TimeZoneInfo.ConvertTime(now, CurrentZone());
            localTime.Text = $"{local:HH:mm}  ·  {FormatOffset(local.Offset)}";
            if (zonePicker.SelectedItem is not ZoneRow row)
            {
                previewName.Text = Loc.T("Выберите пояс в списке");
                time.Text = "";
                date.Text = "";
                return;
            }

            var shown = row.Zone.At(now, WindowsScope());
            previewName.Text = row.Label;
            time.Text = shown.ToString("HH:mm", culture);
            date.Text = $"{shown.ToString("d MMMM yyyy", culture)}  ·  {FormatOffset(shown.Offset)}";
        }

        void RefreshAction()
        {
            var zone = (zonePicker.SelectedItem as ZoneRow)?.Zone;
            action.IsEnabled = scope switch
            {
                Scope.Apps => true,
                Scope.Windows => zone?.Windows != null,
                _ => zone?.IanaId != null,
            };
            action.Content = Loc.T(scope switch
            {
                Scope.Apps => "Выбрать системный пояс",
                Scope.Windows => "Применить в Windows",
                _ => "Скопировать команду",
            });
        }

        void RefreshScope()
        {
            guide.Text = Loc.T(scope switch
            {
                Scope.Browser => "Откройте нужную вкладку в Chrome или Edge. Нажмите F12, затем Ctrl+Shift+P и выберите «Show Protocol monitor».",
                Scope.Apps => "Для Claude Desktop и приложения ChatGPT пояс можно выбрать в настройке Windows. Она действует на все программы.",
                Scope.Windows => "После подтверждения пояс изменится во всех программах. Прежний пояс можно вернуть здесь же.",
                _ => "Вставьте команду в PowerShell. Она передаст выбранный пояс новому сеансу.",
            });
            guideDetails.Text = Loc.T(scope switch
            {
                Scope.Browser => "Вставьте команду внизу и нажмите Enter. Закройте DevTools для сброса.",
                Scope.Code => "Когда спросите текущее время, сеанс сможет прочитать часы в выбранном поясе.",
                _ => "",
            });
            guideDetails.Visibility = guideDetails.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            guide.ToolTip = scope == Scope.Browser
                ? Loc.T("Если пункта нет, включите «Protocol Monitor» в DevTools: F1, раздел «Experiments».")
                : null;
            var now = UtcNow();
            rows = places.Select(zone => new ZoneRow(zone, FormatOffset(zone.At(now, WindowsScope()).Offset))).ToArray();
            RefreshResults();
        }

        void Copy(string text, string success)
        {
            bool copied = Ui.CopyText(text);
            Ui.SetStatus(status, Loc.T(copied ? success : "Не удалось скопировать: буфер обмена занят"), !copied);
        }

        async Task ApplyAsync()
        {
            if (busy || !action.IsEnabled) return;
            if (scope == Scope.Apps) { windows.IsChecked = true; windows.Focus(); return; }
            if (zonePicker.SelectedItem is not ZoneRow row) return;
            var zone = row.Zone;
            busy = true;
            action.IsHitTestVisible = false;
            scopePicker.IsHitTestVisible = false;
            picker.IsHitTestVisible = false;
            try
            {
                if (scope == Scope.Windows && zone.Windows is { } systemZone)
                {
                    if (!DnexDialog.Ask("Изменить часовой пояс Windows?",
                        $"Выбран «{zone.Label}». Новое время будет отображаться во всех программах.",
                        "Изменить пояс", danger: true, windowTitle: Title)) return;
                    Ui.SetStatus(status, Loc.T("Применяю часовой пояс…"));
                    var changed = await TimeZoneSettings.ApplyWindowsAsync(systemZone.Id);
                    RefreshClock();
                    if (!changed) log("Windows не применила часовой пояс");
                    Ui.SetStatus(status, Loc.T(changed ? "Часовой пояс Windows изменен" : "Windows не применила часовой пояс. Проверьте права или настройки времени."), !changed);
                }
                else if (zone.IanaId == null)
                {
                    Ui.SetStatus(status, Loc.T("Этот пояс доступен только в Windows"), true);
                }
                else if (scope == Scope.Browser)
                {
                    Copy(TimeZoneSettings.BrowserCommand(zone.IanaId), "Команда скопирована. Вставьте ее в Protocol monitor.");
                }
                else
                {
                    Copy(TimeZoneSettings.ClaudeCodeCommand(zone.IanaId, zone.Windows?.Id), "Команда скопирована. Вставьте ее в PowerShell.");
                }
            }
            catch (Exception)
            {
                log("Не удалось подготовить или применить часовой пояс");
                Ui.SetStatus(status, Loc.T("Не удалось подготовить или применить часовой пояс"), true);
            }
            finally
            {
                busy = false;
                action.IsHitTestVisible = true;
                scopePicker.IsHitTestVisible = true;
                picker.IsHitTestVisible = true;
            }
        }

        foreach (var (option, target) in scopeOptions)
            option.Checked += (_, _) => { scope = target; RefreshScope(); };

        void RefreshResults()
        {
            var words = TimeZonePlaces.SearchWords(search.Text);
            var country = (countryPicker.SelectedItem as CountryOption)?.Code ?? "";
            var matches = rows.Where(row => (country.Length == 0 || row.Zone.CountryCode == country) &&
                row.Zone.Matches(words, row.Offset)).ToArray();
            filtering = true;
            zonePicker.ItemsSource = matches;
            zonePicker.SelectedItem = matches.FirstOrDefault(row => row.Zone == selected);
            filtering = false;
            UpdateResultsHeight(dock.ActualHeight);
            empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScrollToSelected();
            RefreshSelection();
        }

        void RefreshSelection()
        {
            RefreshClock();
            RefreshAction();
            var zone = (zonePicker.SelectedItem as ZoneRow)?.Zone;
            var message = zone == null ? "" : scope switch
            {
                Scope.Windows when zone.Windows == null => "Для этого места доступна настройка в браузере или в командной строке",
                Scope.Browser or Scope.Code when zone.IanaId == null => "Этот пояс доступен только в Windows",
                _ => "",
            };
            Ui.SetStatus(status, Loc.T(message), message.Length > 0);
        }

        countryPicker.SelectionChanged += (_, _) =>
        {
            search.Tag = Loc.T(countryPicker.SelectedItem is not CountryOption { Code.Length: > 0 } ? "Город, страна или UTC" : "Город или UTC");
            if (search.Text.Length == 0) RefreshResults();
            else search.Clear();
        };

        search.TextChanged += (_, _) =>
        {
            clearSearch.Visibility = search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshResults();
        };
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Down || zonePicker.Items.Count == 0) return;
            if (zonePicker.SelectedItem == null) zonePicker.SelectedIndex = 0;
            ScrollToSelected();
            if (zonePicker.ItemContainerGenerator.ContainerFromItem(zonePicker.SelectedItem) is ListBoxItem item)
                item.Focus();
            else
                zonePicker.Focus();
            e.Handled = true;
        };
        zonePicker.SelectionChanged += (_, _) =>
        {
            if (filtering) return;
            if (zonePicker.SelectedItem is ZoneRow row) selected = row.Zone;
            RefreshSelection();
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
            search.Focus();
            search.SelectAll();
            e.Handled = true;
        };

        var clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        clockTimer.Tick += (_, _) => RefreshClock();
        root.Loaded += (_, _) => RefreshClock();
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible && !Storage.TestCopy)
            {
                RefreshClock();
                clockTimer.Start();
            }
            else clockTimer.Stop();
        };
        root.Unloaded += (_, _) => clockTimer.Stop();
        RefreshScope();
        return window;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = Loc.T(text), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
    };

    private static RadioButton ScopeTile(Style style, string label, string detail, string glyph)
    {
        var icon = new Controls.Icon { Kind = glyph, Size = 19 }.Ref(Controls.Icon.ForegroundProperty, "Accent");
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Child = icon,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        }.Ref(Border.BackgroundProperty, "Surface2");
        DockPanel.SetDock(badge, Dock.Left);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Heading(label));
        labels.Children.Add(Ui.Hint(Loc.T(detail), 2));
        var content = new DockPanel { Margin = new Thickness(0, 0, 23, 0) };
        content.Children.Add(badge);
        content.Children.Add(labels);
        var option = new RadioButton
        {
            Content = content, GroupName = "time-zone", Style = style, Margin = new Thickness(0, 0, 8, 8),
        };
        AutomationProperties.SetName(option, Loc.T(label));
        AutomationProperties.SetHelpText(option, Loc.T(detail));
        return option;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var absolute = offset.Duration();
        return $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{absolute.Hours:00}:{absolute.Minutes:00}";
    }
}
