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
        // На низком экране окно не выше рабочей области: шапка и кнопки внизу видны, середина прокручивается
        BodyScroll.MaxHeight = Math.Min(620, SystemParameters.WorkArea.Height - 230);
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

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"CHIMERION {v?.Major}.{v?.Minor}.{v?.Build}";
        DataPathText.Text = Storage.Dir;
        BuildColors();
        (_settings.Language == "en" ? LangEn : LangRu).IsChecked = true;
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
        CloseToTrayBox.IsChecked = CheckUpdatesBox.IsChecked = AutoStartBox.IsChecked = true;
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
        ErrorText.Text = "Рекомендуемые значения расставлены. Нажмите «Сохранить»";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
        var doh = RefDoh.Text.Trim();
        if (!SourceParser.IsValidDoh(doh, out _)) { ErrorText.Text = "Эталонный DoH: нужен https-адрес без логина"; return; }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 2 || timeout > 30) { ErrorText.Text = "Таймаут: от 2 до 30 секунд"; return; }
        if (!int.TryParse(ParallelBox.Text, out var parallel) || parallel < 1 || parallel > 64) { ErrorText.Text = "Проверок одновременно: от 1 до 64"; return; }
        if (!int.TryParse(TrayMinutesBox.Text, out var trayMinutes) || trayMinutes < 1 || trayMinutes > 60) { ErrorText.Text = "Проверка в трее: от 1 до 60 минут"; return; }
        if (!int.TryParse(WatchdogBox.Text, out var watchdog) || watchdog < 15 || watchdog > 600) { ErrorText.Text = "Сторож DNS: от 15 до 600 секунд"; return; }
        if (!int.TryParse(PingBox.Text, out var pingMinutes) || pingMinutes < 1 || pingMinutes > 60) { ErrorText.Text = "Обновление пинга: от 1 до 60 минут"; return; }

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
                ErrorText.Text = "Не удалось изменить автозапуск: Windows не дала записать его или убрать прежнее задание с правами администратора";
                return;
            }
        }
        _settings.TrayCheckMinutes = trayMinutes;
        _settings.WatchdogSeconds = watchdog;
        _settings.PingRefreshMinutes = pingMinutes;
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
