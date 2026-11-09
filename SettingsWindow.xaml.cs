using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>Окно настроек. Сохраняет settings.json; «Отмена» ничего не меняет.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>true, если настройки сохранены.</summary>
    public bool Saved { get; private set; }
    /// <summary>Пользователь попросил вернуть прежнюю версию: главное окно поменяет файлы и перезапустит программу.</summary>
    public bool RollbackRequested { get; private set; }
    /// <summary>«export» или «import»: окно закрыто ради переноса настроек, его выполняет главное окно (там список серверов).</summary>
    public string? TransferRequested { get; private set; }

    private void Export_Click(object sender, RoutedEventArgs e) { TransferRequested = "export"; Close(); }
    private void Import_Click(object sender, RoutedEventArgs e) { TransferRequested = "import"; Close(); }
    /// <summary>Настройки «Два DNS» сохранены: главное окно применит их к текущему подключению.</summary>
    public bool SplitChanged { get; private set; }

    private void ShowSplitStatus()
    {
        var s = _settings.SplitDns;
        SplitStatus.Text = s.Enabled ? $"Включено: общий DNS «{s.GeneralName}», ИИ через выбранный сервер" : "Выключено";
    }

    private void SplitDns_Click(object sender, RoutedEventArgs e)
    {
        if (!new SplitDnsWindow(_settings, this).ShowDialog()) return;
        SplitChanged = true;
        ShowSplitStatus();
    }

    /// <summary>
    /// Высота окна по открытой странице: короткая без пустоты снизу, длинная («Сторож и трей», «О программе») целиком,
    /// пока помещается на экран. Окно меняет высоту вокруг своего центра.
    /// </summary>
    private void FitPage()
    {
        if (!IsLoaded || Pages.Children.OfType<FrameworkElement>().FirstOrDefault(p => p.Visibility == Visibility.Visible) is not { } page) return;
        double width = BodyScroll.ViewportWidth > 0 ? BodyScroll.ViewportWidth : BodyScroll.ActualWidth;
        if (width <= 0) return;
        page.Measure(new Size(width, double.PositiveInfinity));
        var area = Ui.WorkAreaOf(this);
        double height = Math.Clamp(ActualHeight + page.DesiredSize.Height - BodyScroll.ViewportHeight, MinHeight, area.Height - 40);
        if (Math.Abs(height - ActualHeight) < 2) return;
        double top = Top - (height - ActualHeight) / 2;
        Height = height;
        Top = Math.Clamp(top, area.Top + 20, Math.Max(area.Top + 20, area.Bottom - 20 - height));
    }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ShowRollback();
        SourceInitialized += (_, _) => Services.DarkTitle.Apply(this);
        Icon = Controls.Brand.WindowIcon;
        // Широкое окно с вкладками; на маленьком экране оно не больше рабочей области, вкладка прокручивается
        Width = Math.Min(Width, Ui.WorkAreaOf(null).Width - 40);
        Height = Math.Min(Height, Ui.WorkAreaOf(null).Height - 40);
        Loaded += (_, _) => Dispatcher.BeginInvoke(FitPage, System.Windows.Threading.DispatcherPriority.Loaded);
        _settings = settings;
        ShowSplitStatus();
        RefDoh.Text = settings.ReferenceDoh;
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        ParallelBox.Text = settings.Parallelism.ToString();
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        CheckUpdatesBox.IsChecked = settings.CheckUpdates;
        AutoStartBox.IsChecked = AutoStart.IsEnabled(); // источник правды — автозагрузка Windows, а не файл настроек
        TrayMinutesBox.Text = settings.TrayCheckMinutes.ToString();
        WatchdogBox.Text = settings.WatchdogSeconds.ToString();
        PingBox.Text = settings.PingRefreshMinutes.ToString();
        PrivacyBox.IsChecked = settings.PrivacyMode && settings.PrivacyHideWindows;
        MaskOnlyBox.IsChecked = settings.PrivacyMode && !settings.PrivacyHideWindows;
        ShowStreamerState();
        StartCompactBox.IsChecked = settings.StartCompact;
        AutoSwitchBox.IsChecked = settings.AutoSwitchOnServiceDown;
        BackupBox.IsChecked = settings.UseBackupServer;
        AllowSwitchBox.IsChecked = settings.AllowServerSwitch;
        AllowSwitch_Changed(this, new RoutedEventArgs());
        SafetyBox.IsChecked = settings.AccountSafetyGuard;
        _country = settings.EgressCountry;
        BuildCountryPicker();
        SafetyBox.Checked += (_, _) => CountryRow.IsEnabled = true;
        SafetyBox.Unchecked += (_, _) => CountryRow.IsEnabled = false;
        CountryRow.IsEnabled = settings.AccountSafetyGuard;
        CrowdBox.IsChecked = settings.CrowdConsent == true;
        CrowdSitesBox.IsChecked = settings.CrowdSites == true;
        CrowdSitesBox.IsEnabled = CrowdBox.IsChecked == true;
        CrowdBox.Checked += (_, _) => CrowdSitesBox.IsEnabled = true;
        CrowdBox.Unchecked += (_, _) => CrowdSitesBox.IsEnabled = false;
        SiteWatchBox.IsChecked = settings.SiteWatch;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Версия {v?.Major}.{v?.Minor}.{v?.Build} · Windows 10 и 11 · .NET 8";
        // ID устройства раньше стоял в заголовке главного окна. Тот же ID, что уходит в счетчик пользователей
        DeviceIdValue.Text = Environment.GetEnvironmentVariable("DNEX_NO_AUTORESTORE") == "1" ? "a1b2c3d4e5f60718293a4b5c6d7e8f90" : UsageCounter.DeviceId(_settings);
        SideVersion.Text = $"CHIMERION {v?.Major}.{v?.Minor}.{v?.Build}";
        // В режиме скрытия данных путь без имени пользователя Windows
        DataPathText.Text = PrivacyGuard.Enabled ? Storage.Dir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase) : Storage.Dir;
        BuildColors();
        (_settings.Language == "en" ? LangEn : LangRu).IsChecked = true;
        // Открывается вкладка, на которой окно закрыли в прошлый раз
        ((System.Windows.Controls.RadioButton)Nav.Children[Math.Clamp(_lastTab, 0, Nav.Children.Count - 1)]).IsChecked = true;
    }

    /// <summary>Последняя открытая вкладка (на время работы программы).</summary>
    private static int _lastTab;

    /// <summary>Вкладка: страница с тем же именем видна, остальные скрыты. Заголовок и пояснение берутся из Tag страницы.</summary>
    private string _tab = "General";

    /// <summary>Пояснение справа только на «Основных» и только если рядом с карточками хватает места.</summary>
    private string? _sideTab;

    /// <summary>
    /// Выноска справа: заголовок раздела над карточкой, внутри пункты «заголовок + пояснение» (стили InfoHead, InfoBody).
    /// Без повторов текста карточек слева (кроме «Основных»). У «Приватности» и «Данных» вместо выноски свой блок (PrivacyExtra, DataExtra).
    /// </summary>
    private static readonly Dictionary<string, (string Title, (string Head, string Text)[] Items)> SideNotes = new()
    {
        ["General"] = ("Как работает запуск", new[]
        {
            ("В трее", "Крестик прячет окно, а подключение продолжает работать. Программа открывается щелчком по значку в трее"),
            ("Права администратора", "Запуск с Windows идет без них. Права нужны, только чтобы сменить или вернуть DNS Windows"),
            ("Подключение сохраняется", "После перезапуска программа сама восстанавливает последнее подключение"),
        }),
        ["Check"] = ("Как проверяется сервер", new[]
        {
            ("Сравнение ответов", "Программа спрашивает адрес ChatGPT у честного DNS и у проверяемого сервера. Разные ответы значат, что сервер ведет нейросети через себя"),
            ("Таймаут", "Сколько ждать ответа. Слишком мало: рабочие, но далекие серверы попадут в нерабочие"),
            ("Проверок одновременно", "Больше значит быстрее, но на слабой сети проверка мешает другим программам"),
        }),
        ["Watch"] = ("Как работает сторож", new[]
        {
            ("Следит за DNS", "Пока вы подключены, сторож следит за DNS. Если его сбросили, вернет подключение, а если сервер умер, переключит на другой, когда смена сервера разрешена"),
            ("Не спорит с VPN", "Со сторонним VPN сторож не спорит. Если DNS сбрасывают снова и снова, он отступит и подскажет, что делать."),
        }),
        ["Split"] = ("Зачем два DNS", new[]
        {
            ("Свои домены", "Домены, которые вы добавите в «Настроить два DNS», тоже пойдут через Smart DNS"),
        }),
    };

    private readonly List<(string Tab, FrameworkElement Block, System.Windows.Controls.Panel Home, int Index)> _extras = new();

    /// <summary>Вкладки без правой колонки: их содержимое само занимает всю ширину.</summary>
    private static readonly HashSet<string> WideTabs = new() { "About", "Look" };

    /// <summary>
    /// Правая колонка до края страницы: выноска или собственный блок вкладки. На узкой странице колонки нет,
    /// блоки возвращаются на свое место. «О программе» занимает всю ширину.
    /// </summary>
    private void UpdateSide()
    {
        if (SidePanel == null || BodyScroll == null || SideColumn == null || Pages == null) return;
        if (_extras.Count == 0)
            foreach (var (tab, block) in new (string, FrameworkElement)[] { ("Privacy", PrivacyExtra), ("Watch", WatchServices), ("Watch", WatchSites), ("Data", DataExtra) })
            {
                var home = (System.Windows.Controls.Panel)block.Parent;
                _extras.Add((tab, block, home, home.Children.IndexOf(block)));
            }
        bool about = WideTabs.Contains(_tab);
        bool notes = SideNotes.ContainsKey(_tab);
        bool show = !about && (notes || _extras.Any(e => e.Tab == _tab)) && BodyScroll.ActualWidth >= 1000;
        SidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SideNotesBox.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        SideColumn.Width = show || about ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        System.Windows.Controls.Grid.SetColumnSpan(Pages, about ? 2 : 1);

        // Обратно в обратном порядке: индексы на странице восстанавливаются точно
        for (int i = _extras.Count - 1; i >= 0; i--)
        {
            var (tab, block, home, index) = _extras[i];
            if (show && tab == _tab || ReferenceEquals(block.Parent, home)) continue;
            ((System.Windows.Controls.Panel)block.Parent).Children.Remove(block);
            home.Children.Insert(Math.Min(index, home.Children.Count), block);
        }
        foreach (var (tab, block, home, _) in _extras)
        {
            if (!(show && tab == _tab) || ReferenceEquals(block.Parent, SideExtras)) continue;
            home.Children.Remove(block);
            SideExtras.Children.Add(block);
        }

        if (!show || !notes || _sideTab == _tab) return;
        _sideTab = _tab;
        var (title, items) = SideNotes[_tab];
        SideTitle.Text = title;
        SideBody.Children.Clear();
        foreach (var (head, text) in items)
        {
            var h = new System.Windows.Controls.TextBlock { Text = head, Style = (Style)FindResource("InfoHead") };
            if (SideBody.Children.Count == 0) h.Margin = new Thickness(0);
            SideBody.Children.Add(h);
            SideBody.Children.Add(new System.Windows.Controls.TextBlock { Text = text, Style = (Style)FindResource("InfoBody") });
        }
    }

    private void BodyScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSide();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string name } rb) return;
        _lastTab = Nav.Children.IndexOf(rb);
        _tab = name;
        UpdateSide();
        foreach (FrameworkElement page in Pages.Children)
        {
            bool show = page.Name == "Page" + name;
            page.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;
            var parts = (page.Tag as string ?? "").Split('|');
            PageTitle.Text = parts[0];
            // Рекомендуемые значения есть только у страниц с полями; на «Два DNS», «Данных» и «О программе» кнопка ничего бы не сделала
            DefaultsButton.Visibility = name is "General" or "Privacy" or "Check" or "Watch" ? Visibility.Visible : Visibility.Collapsed;
            PageHint.Text = parts.Length > 1 ? parts[1] : "";
            BodyScroll.ScrollToTop();
            Dispatcher.BeginInvoke(FitPage, System.Windows.Threading.DispatcherPriority.Loaded);
            if (Ui.Animate)
            {
                var t = new System.Windows.Media.TranslateTransform(0, 10);
                page.RenderTransform = t;
                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
                t.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            }
        }
    }

    /// <summary>Открыть вкладку по имени страницы (General, Check, Watch...).</summary>
    private void ShowPage(string name)
    {
        foreach (var rb in Nav.Children.OfType<System.Windows.Controls.RadioButton>())
            if (rb.Tag as string == name) rb.IsChecked = true;
    }

    /// <summary>Ошибка в поле: показать вкладку с ним, текст ошибки и поставить курсор в поле.</summary>
    private void Fail(string page, string text, System.Windows.Controls.Control? field = null)
    {
        ShowPage(page);
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
        ErrorText.Text = text;
        field?.Focus();
        if (field is System.Windows.Controls.TextBox tb) tb.SelectAll();
    }

    public const string SiteUrl = "https://chimerion.dev";

    // Страна выхода: null — любая (по истории входов). Список стран, где обычно стоят прокси Smart DNS
    private string? _country;
    private static readonly string[] Countries = { "NL", "DE", "FI", "FR", "GB", "SE", "PL", "LT", "LV", "EE", "CZ", "AT", "CH", "US", "CA", "KZ", "TR", "AE", "JP", "SG" };

    private void BuildCountryPicker()
    {
        string Title(string? c) => c == null ? "Любая" : Services.GeoLocation.CountryName(c);
        CountryHost.Content = Ui.Picker(Title(_country), b =>
        {
            var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var c in Countries.Prepend(null).Append(_country is { } cur && !Countries.Contains(cur) ? cur : null).Distinct().Where((c, i) => i == 0 || c != null))
            {
                var mi = new System.Windows.Controls.MenuItem { Header = Title(c), IsCheckable = true, IsChecked = c == _country,
                    ToolTip = c == null ? "Без закрепления: программа держит страну, из которой вас уже видели" : null };
                mi.Click += (_, _) => { _country = c; BuildCountryPicker(); };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        });
    }
    private void Site_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(SiteUrl);

    private void DeviceId_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        DeviceIdLabel.Text = Ui.CopyText(DeviceIdValue.Text) ? "ID скопирован" : "Не удалось скопировать: буфер обмена занят";
    private void Repo_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}");
    private void License_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}/blob/main/LICENSE");
    private void Changelog_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}/blob/main/CHANGELOG.md");
    private void Issues_Click(object sender, RoutedEventArgs e) =>
        ReportWindow.Show(() => null, () => AutoStart.OpenUrl(Diagnostics.IssuesUrl), _ => { });

    // Telegram: канал, чат и его темы. Ссылки на темы (t.me/c/...) открываются у тех, кто уже вступил в чат
    private const string TgChannel = "https://t.me/chimeriondns", TgChat = "https://t.me/+rXN4fR7HWcxlMTYy", TgChatTopic = "https://t.me/c/4330210118/";
    private static readonly Dictionary<string, int> TgTopics = new() { ["bugs"] = 10, ["ideas"] = 14, ["help"] = 13 };

    private void TgChannel_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(TgChannel);
    private void TgChat_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(TgChat);
    private void TgTopic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && TgTopics.TryGetValue(key, out var id)) AutoStart.OpenUrl(TgChatTopic + id);
    }

    /// <summary>Автопереключение недоступно без разрешения менять сервер; настройку запасного можно изменить заранее.</summary>
    private void AllowSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (AllowSwitchBox == null || BackupBox == null || AutoSwitchBox == null) return;
        UpdateSwitchOptions(AllowSwitchBox, BackupBox, AutoSwitchBox);
    }

    internal static void UpdateSwitchOptions(System.Windows.Controls.CheckBox allow, System.Windows.Controls.CheckBox backup, System.Windows.Controls.CheckBox autoSwitch)
    {
        backup.IsEnabled = true;
        autoSwitch.IsEnabled = allow.IsChecked == true;
    }

    /// <summary>Два варианта скрытия данных взаимоисключающие: включили один, второй выключается.</summary>
    private void Privacy_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == PrivacyBox) MaskOnlyBox.IsChecked = false;
        else PrivacyBox.IsChecked = false;
        ShowStreamerState();
    }

    /// <summary>Пояснение «что скрывается» по выбранному режиму: окна от записи прячет только стример-режим.</summary>
    private void ShowStreamerState()
    {
        if (StreamerState == null) return;
        bool on = PrivacyBox.IsChecked == true;
        StreamerState.Text = on ? "Сейчас включено" : "Сейчас выключено: окна на записи видны как обычно";
        StreamerOnly.Opacity = on ? 1 : .45;
    }

    private void CrowdCode_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}/blob/main/backend/crowd/worker.js");
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(LogStore.Dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogStore.Dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ErrorText.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    /// <summary>
    /// Язык интерфейса: применяется сразу во всех окнах и сохраняется в файл тут же, не дожидаясь «Сохранить»
    /// (иначе после «Отмена» интерфейс остался бы на языке, который не записан).
    /// </summary>
    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string lang } || !IsLoaded && _settings.Language == lang) return;
        if (_settings.Language == lang && Loc.Lang == lang) return;
        _settings.Language = lang;
        try { Storage.Save("settings.json", _settings); } catch (Exception) { /* язык все равно сменится, запишется при «Сохранить» */ }
        Loc.SetLanguage(lang);
    }

    /// <summary>Рекомендуемые значения во все поля (как у новой установки). Пишутся в файл только по «Сохранить».</summary>
    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        RefDoh.Text = d.ReferenceDoh;
        TimeoutBox.Text = d.TimeoutSeconds.ToString();
        ParallelBox.Text = d.Parallelism.ToString();
        TrayMinutesBox.Text = d.TrayCheckMinutes.ToString();
        WatchdogBox.Text = d.WatchdogSeconds.ToString();
        PingBox.Text = d.PingRefreshMinutes.ToString();
        CloseToTrayBox.IsChecked = CheckUpdatesBox.IsChecked = AutoStartBox.IsChecked = BackupBox.IsChecked = SafetyBox.IsChecked = SiteWatchBox.IsChecked = AllowSwitchBox.IsChecked = true;
        _country = null; BuildCountryPicker();
        StartCompactBox.IsChecked = AutoSwitchBox.IsChecked = false;
        // Скрытие данных и общая карта — личный выбор: не сбрасываем (выключить стример мод посреди трансляции опасно)
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        ErrorText.Text = "Рекомендуемые значения расставлены. Нажмите «Сохранить»";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
        var doh = RefDoh.Text.Trim();
        if (!SourceParser.IsValidDoh(doh, out _)) { Fail("Check", "Эталонный DoH: нужен https-адрес без логина", RefDoh); return; }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 2 || timeout > 30) { Fail("Check", "Таймаут: от 2 до 30 секунд", TimeoutBox); return; }
        if (!int.TryParse(ParallelBox.Text, out var parallel) || parallel < 1 || parallel > 64) { Fail("Check", "Проверок одновременно: от 1 до 64", ParallelBox); return; }
        if (!int.TryParse(TrayMinutesBox.Text, out var trayMinutes) || trayMinutes < 1 || trayMinutes > 60) { Fail("Watch", "Проверка в трее: от 1 до 60 минут", TrayMinutesBox); return; }
        if (!int.TryParse(WatchdogBox.Text, out var watchdog) || watchdog < 15 || watchdog > 600) { Fail("Watch", "Сторож DNS: от 15 до 600 секунд", WatchdogBox); return; }
        if (!int.TryParse(PingBox.Text, out var pingMinutes) || pingMinutes < 1 || pingMinutes > 60) { Fail("Check", "Обновление пинга: от 1 до 60 минут", PingBox); return; }

        _settings.ReferenceDoh = doh;
        _settings.TimeoutSeconds = timeout;
        _settings.Parallelism = parallel;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.CheckUpdates = CheckUpdatesBox.IsChecked == true;
        if (AutoStartBox.IsChecked != AutoStart.IsEnabled())
        {
            IsEnabled = false; // пока идет запрос прав и планировщик, второй раз не нажать
            ErrorText.Text = "Настраиваю автозапуск…";
            var ok = await AutoStart.SetAsync(AutoStartBox.IsChecked == true);
            IsEnabled = true;
            ErrorText.Text = "";
            if (!ok)
            {
                ShowPage("General");
                ErrorText.Text = "Не удалось изменить автозапуск: Windows не дала записать его или убрать прежнее задание с правами администратора";
                return;
            }
        }
        _settings.TrayCheckMinutes = trayMinutes;
        _settings.WatchdogSeconds = watchdog;
        _settings.PingRefreshMinutes = pingMinutes;
        _settings.PrivacyMode = PrivacyBox.IsChecked == true || MaskOnlyBox.IsChecked == true;
        if (_settings.PrivacyMode) _settings.PrivacyHideWindows = PrivacyBox.IsChecked == true;
        _settings.StartCompact = StartCompactBox.IsChecked == true;
        _settings.AutoSwitchOnServiceDown = AutoSwitchBox.IsChecked == true;
        _settings.UseBackupServer = BackupBox.IsChecked == true;
        _settings.AllowServerSwitch = AllowSwitchBox.IsChecked == true;
        _settings.AccountSafetyGuard = SafetyBox.IsChecked == true;
        _settings.EgressCountry = _country;
        _settings.SiteWatch = SiteWatchBox.IsChecked == true;
        // Не спрашивали и галочку не трогали: вопрос при запуске остается. Иначе выбор в настройках и есть ответ
        if (_settings.CrowdConsent != null || CrowdBox.IsChecked == true) _settings.CrowdConsent = CrowdBox.IsChecked == true;
        if (_settings.CrowdSites != null || CrowdSitesBox.IsChecked == true) _settings.CrowdSites = CrowdSitesBox.IsChecked == true;
        PrivacyGuard.Set(_settings.PrivacyMode, _settings.PrivacyHideWindows);
        try
        {
            SaveColors();
            Storage.Save("settings.json", _settings);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось сохранить: " + ex.Message;
            return;
        }
        Saved = true;
        Close();
    }

    /// <summary>Кнопка возврата прежней версии: видна, только если после обновления осталась CHIMERION.prev.exe.</summary>
    private void ShowRollback()
    {
        if (Services.Updater.PreviousVersion() is not { } prev) return;
        RollbackPanel.Visibility = Visibility.Visible;
        RollbackButton.Content = $"Вернуть версию {prev.ToString(3)}";
        RollbackHint.Text = $"Сейчас {UpdateChecker.CurrentVersion.ToString(3)}. Если после обновления что-то стало хуже, можно вернуть {prev.ToString(3)}: " +
                            "исправления новых версий пропадут, настройки, серверы и подключение сохранятся";
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var prev = Services.Updater.PreviousVersion()?.ToString(3);
        var cur = UpdateChecker.CurrentVersion.ToString(3);
        if (!DnexDialog.AskRisk($"Вернуть {prev} вместо {cur}?",
                $"Сейчас установлена {cur}, вернется {prev}.\n\nСохранятся: настройки, список серверов, журнал и подключение DNS.\n" +
                $"Пропадут: все исправления и новое, что появилось после {prev}.\n\nПрограмма перезапустится. Вернуться на {cur} можно той же кнопкой или обновлением.",
                "Понимаю, что исправления новых версий пропадут", "Вернуть " + prev, "Отмена", windowTitle: "Возврат версии"))
            return;
        RollbackRequested = true;
        Close();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.Dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ErrorText.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
