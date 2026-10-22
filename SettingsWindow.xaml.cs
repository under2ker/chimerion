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

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        RefDoh.Text = settings.ReferenceDoh;
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        ParallelBox.Text = settings.Parallelism.ToString();
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        TrayMinutesBox.Text = settings.TrayCheckMinutes.ToString();
        WatchdogBox.Text = settings.WatchdogSeconds.ToString();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"DNEX {v?.Major}.{v?.Minor}.{v?.Build}";
        DataPathText.Text = Storage.Dir;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var doh = RefDoh.Text.Trim();
        if (!SourceParser.IsValidDoh(doh, out _)) { ErrorText.Text = "Эталонный DoH: нужен https-адрес без логина"; return; }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 2 || timeout > 30) { ErrorText.Text = "Таймаут: от 2 до 30 секунд"; return; }
        if (!int.TryParse(ParallelBox.Text, out var parallel) || parallel < 1 || parallel > 64) { ErrorText.Text = "Проверок одновременно: от 1 до 64"; return; }
        if (!int.TryParse(TrayMinutesBox.Text, out var trayMinutes) || trayMinutes < 1 || trayMinutes > 60) { ErrorText.Text = "Проверка в трее: от 1 до 60 минут"; return; }
        if (!int.TryParse(WatchdogBox.Text, out var watchdog) || watchdog < 15 || watchdog > 600) { ErrorText.Text = "Сторож DNS: от 15 до 600 секунд"; return; }

        _settings.ReferenceDoh = doh;
        _settings.TimeoutSeconds = timeout;
        _settings.Parallelism = parallel;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _settings.TrayCheckMinutes = trayMinutes;
        _settings.WatchdogSeconds = watchdog;
        try
        {
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

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Storage.Dir) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorText.Text = "Не удалось открыть папку: " + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
