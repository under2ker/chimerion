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

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        ShowRollback();
        SourceInitialized += (_, _) => Services.DarkTitle.Apply(this);
        Icon = Controls.Brand.WindowIcon;
        // Широкое окно с вкладками; на маленьком экране оно не больше рабочей области, вкладка прокручивается
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
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
        StartCompactBox.IsChecked = settings.StartCompact;
        AutoSwitchBox.IsChecked = settings.AutoSwitchOnServiceDown;
        BackupBox.IsChecked = settings.UseBackupServer;
        AllowSwitchBox.IsChecked = settings.AllowServerSwitch;
        AllowSwitch_Changed(this, new RoutedEventArgs());
        SafetyBox.IsChecked = settings.AccountSafetyGuard;
        CrowdBox.IsChecked = settings.CrowdConsent == true;
        SiteWatchBox.IsChecked = settings.SiteWatch;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Версия {v?.Major}.{v?.Minor}.{v?.Build} · Windows 10 и 11 · .NET 8";
        SideVersion.Text = $"CHIMERION {v?.Major}.{v?.Minor}.{v?.Build}";
        DataPathText.Text = Storage.Dir;
        BuildColors();
        (_settings.Language == "en" ? LangEn : LangRu).IsChecked = true;
        // Открывается вкладка, на которой окно закрыли в прошлый раз
        ((System.Windows.Controls.RadioButton)Nav.Children[Math.Clamp(_lastTab, 0, Nav.Children.Count - 1)]).IsChecked = true;
    }

    /// <summary>Последняя открытая вкладка (на время работы программы).</summary>
    private static int _lastTab;

    /// <summary>Вкладка: страница с тем же именем видна, остальные скрыты. Заголовок и пояснение берутся из Tag страницы.</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string name } rb) return;
        _lastTab = Nav.Children.IndexOf(rb);
        foreach (FrameworkElement page in Pages.Children)
        {
            bool show = page.Name == "Page" + name;
            page.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) continue;
            var parts = (page.Tag as string ?? "").Split('|');
            PageTitle.Text = parts[0];
            PageHint.Text = parts.Length > 1 ? parts[1] : "";
            BodyScroll.ScrollToTop();
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

    private void Repo_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}");
    private void License_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}/blob/main/LICENSE");
    private void Changelog_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl($"https://github.com/{UpdateChecker.Repo}/blob/main/CHANGELOG.md");
    private void Issues_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(Diagnostics.IssuesUrl);

    // Telegram: канал, чат и его темы. Ссылки на темы (t.me/c/...) открываются у тех, кто уже вступил в чат
    private const string TgChannel = "https://t.me/chimeriondns", TgChat = "https://t.me/+rXN4fR7HWcxlMTYy", TgChatTopic = "https://t.me/c/4330210118/";
    private static readonly Dictionary<string, int> TgTopics = new() { ["bugs"] = 10, ["ideas"] = 14, ["help"] = 13 };

    private void TgChannel_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(TgChannel);
    private void TgChat_Click(object sender, RoutedEventArgs e) => AutoStart.OpenUrl(TgChat);
    private void TgTopic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key } && TgTopics.TryGetValue(key, out var id)) AutoStart.OpenUrl(TgChatTopic + id);
    }

    /// <summary>Без права менять сервер запасной сервер и автопереключение не действуют: их галочки неактивны.</summary>
    private void AllowSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (AllowSwitchBox == null || BackupBox == null || AutoSwitchBox == null) return;
        BackupBox.IsEnabled = AutoSwitchBox.IsEnabled = AllowSwitchBox.IsChecked == true;
    }

    /// <summary>Два варианта скрытия данных взаимоисключающие: включили один, второй выключается.</summary>
    private void Privacy_Checked(object sender, RoutedEventArgs e)
    {
        if (sender == PrivacyBox) MaskOnlyBox.IsChecked = false;
        else PrivacyBox.IsChecked = false;
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
        _settings.SiteWatch = SiteWatchBox.IsChecked == true;
        // Не спрашивали и галочку не трогали: вопрос при запуске остается. Иначе выбор в настройках и есть ответ
        if (_settings.CrowdConsent != null || CrowdBox.IsChecked == true) _settings.CrowdConsent = CrowdBox.IsChecked == true;
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
        RollbackHint.Text = $"Если после обновления что-то стало хуже, можно вернуть прежнюю версию {prev.ToString(3)}. " +
                            "Программа перезапустится, подключение DNS сохранится. Вперед можно вернуться той же кнопкой";
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (!DnexDialog.Ask($"Вернуть версию {Services.Updater.PreviousVersion()?.ToString(3)}?",
                "Программа перезапустится с прежней версией. Подключение DNS и настройки сохранятся.", "Вернуть", windowTitle: "Возврат версии"))
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
