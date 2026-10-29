using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Сторонние программы, режимы в один клик, диагностика и обновление CHIMERION</summary>
public partial class MainWindow
{
    /// <summary>Проверка новой версии через 10 секунд после запуска, чтобы не мешать старту. Только уведомление.</summary>
    /// <summary>Как часто программа сама проверяет новую версию, пока работает (первый раз через 10 секунд после запуска).</summary>
    internal static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);
    private readonly DispatcherTimer _updateTimer = new();
    /// <summary>Найденная фоном версия: кнопка внизу становится «Обновить до …» и открывает окно сразу, без новой проверки.</summary>
    private UpdateInfo? _pendingUpdate;
    /// <summary>Версия, которую уже предлагали в этом сеансе: окно само не открывается повторно каждые 6 часов.</summary>
    private string? _offeredVersion;

    private void ScheduleUpdateCheck()
    {
        _updateTimer.Interval = TimeSpan.FromSeconds(10);
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = UpdateCheckEvery;
            await BackgroundUpdateCheckAsync();
        };
        _updateTimer.Start();
    }

    /// <summary>
    /// Фоновая проверка. Нашлась новая версия (не пропущенная пользователем): кнопка внизу превращается в
    /// «Обновить до …». Если окно на экране и программа ничем не занята, сразу открывается предложение; если она
    /// в трее, приходит уведомление Windows, и щелчок по нему открывает то же окно.
    /// </summary>
    private async Task BackgroundUpdateCheckAsync()
    {
        if (!_settings.CheckUpdates || Updater.Installed != null) return;
        UpdateInfo? info;
        try { info = await UpdateChecker.CheckAsync(); }
        catch (Exception) { return; }
        if (info == null || _settings.SkippedUpdate == info.Version.ToString()) return;
        ShowUpdateAvailable(info);
        if (_offeredVersion == info.Version.ToString()) return;
        _offeredVersion = info.Version.ToString();
        Log($"Доступна версия {info.Version}{(info.Hotfix != null ? " (быстрое исправление)" : "")}");
        bool onScreen = IsVisible && WindowState != WindowState.Minimized && IsActive;
        if (onScreen && !_busyConn && !System.Windows.Interop.ComponentDispatcher.IsThreadModal && UpdateWindow.Current == null)
            OpenUpdateWindow(info);
        else if (_tray != null)
        {
            Balloon(10000, NoticeTitle(FeatureUpdate) + " " + info.Version.ToString(3),
                Loc.T(info.Hotfix != null ? "Срочное исправление. Нажмите, чтобы обновить" : "Нажмите, чтобы посмотреть, что нового, и обновить"),
                System.Windows.Forms.ToolTipIcon.Info, () => { ShowFromTray(); OpenUpdateWindow(info); });
        }
    }

    /// <summary>Кнопка внизу окна показывает найденную версию.</summary>
    private void ShowUpdateAvailable(UpdateInfo info)
    {
        _pendingUpdate = info;
        CheckUpdateButton.Content = $"Обновить до {info.Version.ToString(3)}";
        CheckUpdateButton.Style = (Style)FindResource("Primary");
    }

    private void ClearUpdateAvailable()
    {
        _pendingUpdate = null;
        CheckUpdateButton.Content = "Проверить обновление";
        CheckUpdateButton.Style = (Style)FindResource("Strong");
    }

    /// <summary>Окно с новой версией. «Пропустить версию» запоминает номер для фоновой проверки.</summary>
    private void OpenUpdateWindow(UpdateInfo info)
    {
        // Версия уже скачана и ждет перезапуска: второй раз не скачиваем
        if (Updater.Installed == info.Version)
        {
            if (DnexDialog.Ask($"Версия {info.Version} уже установлена", "Она заработает после перезапуска программы. Подключение DNS сохранится.",
                    "Перезапустить", "Позже", DialogKind.Ok, windowTitle: "Обновление"))
                RestartForUpdate();
            return;
        }
        UpdateWindow.Show(this, info, onRestart: RestartForUpdate, log: Log, onSkip: () =>
        {
            ClearUpdateAvailable();
            var before = _settings.SkippedUpdate;
            _settings.SkippedUpdate = info.Version.ToString();
            try { Storage.Save("settings.json", _settings); } catch (Exception) { }
            OfferUndo($"Версия {info.Version} пропущена", () =>
            {
                _settings.SkippedUpdate = before;
                try { Storage.Save("settings.json", _settings); } catch (Exception) { }
                Log($"Версия {info.Version} снова будет предлагаться");
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>
    /// Перезапуск CHIMERION с правами администратора (для подбора стратегии zapret). Новая копия ждет, пока эта закроется.
    /// DNS и сторонние программы не трогаем, как при перезапуске для обновления.
    /// </summary>
    private void RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Updater.CurrentExePath!, Updater.RelaunchArg) { UseShellExecute = true, Verb = "runas" })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowToast("Права администратора не выданы", "Warn");
            return;
        }
        Log("Перезапуск с правами администратора");
        _restarting = true;
        _exitRequested = true;
        _closeConfirmed = true;
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>Ручной возврат прежней версии из настроек: файлы меняются местами, программа перезапускается.</summary>
    private void RollbackAndRestart()
    {
        try { Updater.SwapWithPrevious(); }
        catch (UpdateException ex)
        {
            Log("Вернуть прежнюю версию не удалось: " + ex.Message);
            DnexDialog.Show("Не удалось вернуть версию", ex.Message, DialogKind.Warn, "Возврат версии");
            return;
        }
        Log("Возвращаю прежнюю версию и перезапускаю программу");
        Relaunch(afterUpdate: false);
    }


    private readonly List<Tool> _tools = new() { new TgProxyTool(), new ZapretTool() };
    private bool _restarting;


    private enum Mode { AiOnly, All, Games, Off }

    private void ModeToggle_Click(object sender, RoutedEventArgs e)
    {
        ModeToggle.IsChecked = false;
        var menu = new ContextMenu { PlacementTarget = ModeToggle, Placement = System.Windows.Controls.Primitives.PlacementMode.Right };
        foreach (var (mode, title, hint) in Modes)
        {
            var mi = new MenuItem { Header = title, ToolTip = hint };
            mi.Click += async (_, _) => await ApplyModeAsync(mode);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private bool _modeBusy;

    /// <summary>
    /// Режим: набор состояний DNS, Telegram-прокси и zapret. Шаги идут по очереди; неудачный шаг пишется в журнал,
    /// остальные все равно выполняются. Свою копию TG WS Proxy пользователя режим не выключает.
    /// </summary>
    private async Task ApplyModeAsync(Mode mode)
    {
        if (_modeBusy || _busyConn) { ShowToast("Подождите: предыдущее действие еще идет", "Warn"); return; }
        _modeBusy = true;
        var title = Modes.First(m => m.Mode == mode).Title;
        Log($"Режим «{title}»");
        var failed = new List<string>();
        var tg = _tools.OfType<TgProxyTool>().First();
        var zapret = _tools.OfType<ZapretTool>().First();
        var silent = new Progress<string>(_ => { });

        async Task Step(string name, Func<Task> action)
        {
            try { await action(); }
            catch (ToolException ex) { failed.Add(name); Log($"Режим «{title}»: {name}: {ex.Message}"); }
            catch (Exception ex) { failed.Add(name); Log($"Режим «{title}»: {name}: ошибка {ex.Message}"); }
        }
        async Task DnsOn()
        {
            if (DnsSwitcher.GetActive() != null) return;
            var best = _servers.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && !s.IsJunk && Usable(s)).OrderBy(s => s.Rank).FirstOrDefault()
                       ?? throw new ToolException("нет рабочих серверов: сначала проверьте или найдите серверы");
            if (!await ConnectToAsync(best, auto: true)) throw new ToolException("подключиться не удалось, подробности выше");
        }
        async Task ToolOff(Tool t)
        {
            if (t is TgProxyTool p && p.ForeignCopyRunning()) return; // своя копия пользователя: не трогаем
            if (t.IsInstalled && (t.State.Enabled || t.IsRunning())) await t.DisableAsync();
        }
        async Task ZapretOn(bool games)
        {
            var restart = zapret.State.Games != games && zapret.IsInstalled && zapret.IsRunning();
            zapret.State.Games = games;
            zapret.SaveState();
            if (restart) await zapret.StopAsync();
            await zapret.EnableAsync(silent, CancellationToken.None);
        }

        try
        {
            switch (mode)
            {
                case Mode.AiOnly:
                    await Step("DNS", DnsOn);
                    await Step("Telegram", () => ToolOff(tg));
                    await Step("zapret", () => ToolOff(zapret));
                    break;
                case Mode.All:
                    await Step("DNS", DnsOn);
                    await Step("Telegram", () => tg.EnableAsync(silent, CancellationToken.None));
                    await Step("zapret", () => ZapretOn(games: false));
                    break;
                case Mode.Games:
                    await Step("zapret", () => ZapretOn(games: true));
                    break;
                case Mode.Off:
                    await Step("Telegram", () => ToolOff(tg));
                    await Step("zapret", () => ToolOff(zapret));
                    if (DnsSwitcher.GetActive() != null)
                        await Step("DNS", async () => { if (!await DisconnectAsync("режим «Все выключить»")) throw new ToolException("отключиться не удалось"); });
                    break;
            }
        }
        finally
        {
            _modeBusy = false;
            UpdateToolsTray();
        }
        if (failed.Count == 0) ShowToast($"Режим «{title}» включен");
        else ShowToast($"Режим «{title}»: не получилось ({string.Join(", ", failed)}), подробности в журнале", "Warn");
    }

    private void DiagToggle_Click(object sender, RoutedEventArgs e)
    {
        DiagToggle.IsChecked = false;
        DiagnosticsWindow.Show(_settings, _tools, LogForReport, Log, DiagFixAsync);
    }

    private void ToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        ToolsToggle.IsChecked = false; // это кнопка, а не переключатель панели: окно открывается отдельно
        ToolsWindow.Show(_tools, Log, UpdateToolsTray, RelaunchElevated, SendToSmartDns);
    }

    /// <summary>При запуске CHIMERION поднимает включенные программы и смотрит, нет ли у них новых версий.</summary>
    private async Task StartToolsAsync()
    {
        foreach (var t in _tools.Where(t => t.State.Enabled && t.IsInstalled))
        {
            // Запуск вместе с Windows идет без прав администратора, и UAC спрашивается только ради возврата DNS.
            // Обход zapret без прав не запустить: не показываем запрос при каждом входе, а говорим, как включить
            if (t.NeedsAdmin && !DnsSwitcher.IsAdmin() && AutoStart.StartedHidden && !t.IsRunning())
            {
                Log($"{t.Title}: ждет разрешения. Откройте «Telegram и YouTube» и нажмите «Включить», Windows спросит права");
                ShowToast($"{t.Title}: обход ждет разрешения администратора. Включается в окне «Telegram и YouTube»", "Warn");
                continue;
            }
            try
            {
                if (!t.IsRunning()) { await t.StartAsync(); Log($"{t.Title}: включено"); }
            }
            catch (ToolException ex) { Log($"{t.Title}: не включилось. {ex.Message}"); ShowToast($"{t.Title}: не включилось, подробности в журнале", "Warn"); }
        }
        UpdateToolsTray();
        if (!_settings.CheckUpdates) return;
        foreach (var t in _tools.Where(t => t.IsInstalled))
        {
            try
            {
                await t.CheckAsync();
                if (t.UpdateAvailable)
                {
                    Log($"{t.Title}: доступна версия {t.Latest!.Version}, обновить можно в окне «Telegram и YouTube»");
                    ShowToast($"{t.Title}: доступна версия {t.Latest!.Version}", "Warn");
                }
            }
            catch (ToolException) { /* фоновая проверка: без сети или лимит GitHub — молча */ }
        }
    }

    /// <summary>
    /// Выход из CHIMERION выключает программы, которые она включала. Не трогаем их при выходе из Windows
    /// (система и так все закроет) и при перезапуске для обновления.
    /// </summary>
    private void StopToolsOnExit()
    {
        if (_sessionEnding || _restarting || _keepToolsOnExit) return;
        // Все инструменты выключаются одновременно и не дольше 6 секунд в сумме: выход не должен висеть
        var stops = _tools.Where(t => t.State.Enabled && t.IsInstalled).Select(t => Task.Run(t.StopAsync)).ToArray();
        try { Task.WaitAll(stops, TimeSpan.FromSeconds(6)); } catch (Exception) { }
    }

    /// <summary>Пункты трея «Telegram» и «YouTube и Discord»: текст по текущему состоянию.</summary>
    private void UpdateToolsTray()
    {
        void Set(System.Windows.Forms.ToolStripMenuItem? item, Tool t, string name)
        {
            if (item == null) return;
            var foreign = t is TgProxyTool tg && tg.ForeignCopyRunning();
            var on = foreign || (t.IsInstalled && t.IsRunning());
            item.Text = foreign ? $"{name}: работает ваша копия" : on ? $"{name}: выключить" : $"{name}: включить";
            item.Enabled = !foreign;
        }
        UpdateHeaderStats();
        Set(_trayTg, _tools[0], "Telegram");
        Set(_trayZapret, _tools[1], "YouTube и Discord");
    }

    private async Task ToggleToolFromTrayAsync(Tool t)
    {
        try
        {
            if (t.IsInstalled && t.IsRunning()) { await t.DisableAsync(); Log($"{t.Title}: выключено"); ShowToast($"{t.Title}: выключено"); }
            else
            {
                ShowToast($"{t.Title}: включаю…");
                await t.EnableAsync(new Progress<string>(_ => { }), CancellationToken.None);
                Log($"{t.Title}: включено");
                ShowToast($"{t.Title}: включено");
            }
        }
        catch (ToolException ex) { Log($"{t.Title}: {ex.Message}"); ShowToast($"{t.Title}: {ex.Message}", "Bad"); }
        UpdateToolsTray();
    }

    /// <summary>
    /// Перезапуск после обновления: новая версия стартует и ждет, пока эта копия закроется. Выход без вопроса
    /// и без отключения: DNS остается, новая копия продолжает с того же подключения (как при выходе из Windows).
    /// </summary>
    private void RestartForUpdate() => Relaunch(afterUpdate: true);

    /// <summary>Перезапуск программы: новая копия ждет, пока эта закроется. DNS и программы Telegram/zapret остаются.</summary>
    private void Relaunch(bool afterUpdate)
    {
        try { Updater.Restart(afterUpdate); }
        catch (Exception e)
        {
            Log("Не удалось запустить новую версию: " + e.Message);
            DnexDialog.Show("Не удалось перезапустить", "Закройте программу и запустите ее снова: новая версия уже на месте.", DialogKind.Warn, "Обновление");
            return;
        }
        Log("Перезапуск для обновления");
        _restarting = true; // Telegram-прокси и zapret не останавливаем: новая копия продолжит с ними
        _exitRequested = true;
        _closeConfirmed = true;
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>Кнопка в полосе заголовка: проверить сейчас и показать результат.</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Фон уже нашел версию: окно сразу, без повторной проверки
        if (_pendingUpdate is { } found) { OpenUpdateWindow(found); return; }
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Проверяю…";
        try
        {
            var r = await UpdateChecker.CheckDetailedAsync();
            switch (r.Status)
            {
                case UpdateStatus.Available:
                    Log($"Доступна версия {r.Info!.Version}");
                    _offeredVersion = r.Info!.Version.ToString();
                    ShowUpdateAvailable(r.Info!);
                    ShowDashboardUpdateResult($"Доступна версия {r.Info!.Version}", "Ok", null);
                    OpenUpdateWindow(r.Info!);
                    break;
                case UpdateStatus.UpToDate:
                    // Итог рядом с кнопкой, а не всплывающим окном: отменять нечего, и ничего не перекрывается
                    ShowUpdateResult($"У вас последняя версия {UpdateChecker.CurrentVersion}", "Ok", null);
                    break;
                default:
                    Log("Проверка обновления не удалась: " + r.Reason);
                    ShowUpdateResult("Не удалось проверить обновление", "Warn", r.Reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("Проверка обновления не удалась: " + ex.Message);
            ShowUpdateResult("Не удалось проверить обновление", "Warn", ex.Message);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            if (_pendingUpdate == null) CheckUpdateButton.Content = "Проверить обновление";
        }
    }

    private readonly DispatcherTimer _updateResultTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>Итог проверки обновления на месте подписи с версией слева от кнопки; через 6 с подпись возвращается.</summary>
    private void ShowUpdateResult(string text, string colorKey, string? reason)
    {
        ShowDashboardUpdateResult(text, colorKey, reason);
        HeaderVersion.Text = text;
        HeaderVersion.Foreground = (Brush)FindResource(colorKey);
        UpdateResultIcon.Kind = colorKey == "Ok" ? "circle-check" : "triangle-alert";
        UpdateResultIcon.Foreground = (Brush)FindResource(colorKey);
        UpdateResultIcon.Visibility = Visibility.Visible;
        UpdateResultIcon.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(200)));
        HeaderVersion.ToolTip = reason;
        HeaderVersion.Visibility = Visibility.Visible;
        HeaderVersion.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(200)));
        _updateResultTimer.Stop();
        _updateResultTimer.Tick -= ResetUpdateResult;
        _updateResultTimer.Tick += ResetUpdateResult;
        _updateResultTimer.Start();
        if (!IsVisible || WindowState == WindowState.Minimized) ShowToast(text + (reason != null ? ": " + reason : ""), colorKey);
    }

    private void ResetUpdateResult(object? sender, EventArgs e)
    {
        _updateResultTimer.Stop();
        HeaderVersion.Text = $"версия {UpdateChecker.CurrentVersion}";
        HeaderVersion.ClearValue(TextBlock.ForegroundProperty);
        HeaderVersion.ToolTip = null;
        UpdateResultIcon.Visibility = Visibility.Collapsed;
        ApplyResponsive(); // на узком окне подпись снова скрывается
    }

    /// <summary>
    /// Домен из «Сайтов для обхода» в Smart DNS: добавляется в свои домены «Два DNS». Возвращает пояснение для подсказки.
    /// Если «Два DNS» включены и есть подключение, правила обновляются сразу.
    /// </summary>
    private string SendToSmartDns(string host)
    {
        var list = _settings.SplitDns.ExtraAiDomains;
        if (!list.Contains(host, StringComparer.OrdinalIgnoreCase)) list.Add(host);
        try { Storage.Save("settings.json", _settings); } catch (Exception e) { Log("Не удалось сохранить настройки: " + e.Message); }
        Log($"{host}: отправлен в Smart DNS («Два DNS» → свои домены)");
        if (!_settings.SplitDns.Enabled)
        {
            ShowToast($"{host} добавлен в Smart DNS. Заработает, когда включите «Два DNS» в настройках", "Warn");
            return "Добавлен в свои домены «Два DNS». Включите «Два DNS» в настройках";
        }
        if (DnsSwitcher.GetActive() != null) _ = ApplySplitChangeAsync();
        ShowToast($"{host} теперь идет через Smart DNS");
        return "Добавлен в свои домены «Два DNS»";
    }

    /// <summary>Исправления из «Диагностики», которые делает главное окно.</summary>
    private async Task DiagFixAsync(string action)
    {
        switch (action)
        {
            case "connect":
                var best = _servers.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && Usable(s)).OrderBy(s => s.Rank).FirstOrDefault();
                if (best == null) { ShowToast("Рабочих серверов нет: нажмите «Найти новые серверы»", "Warn"); return; }
                await ConnectToAsync(best);
                break;
            case "reconnect":
                if (DnsSwitcher.GetActive() is { } a && _servers.FirstOrDefault(s => s.Id == a.ServerId) is { } cur)
                    await ConnectToAsync(cur); // ApplyAsync заново ставит DNS и правила на адаптеры
                break;
            case "switch": await SwitchToBestAsync(); break;
            case "update": CheckUpdate_Click(this, new RoutedEventArgs()); break;
            case "tools": ToolsToggle_Click(this, new RoutedEventArgs()); break;
            case "split":
                if (!new SplitDnsWindow(_settings, this).ShowDialog()) return;
                try { Storage.Save("settings.json", _settings); } catch (Exception e) { Log("Не удалось сохранить настройки: " + e.Message); }
                if (DnsSwitcher.GetActive() != null) await ApplySplitChangeAsync();
                break;
        }
    }
}
