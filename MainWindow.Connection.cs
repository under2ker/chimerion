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

/// <summary>Подключение и отключение, сторож подключения и отмена действий</summary>
public partial class MainWindow
{
    private SolidColorBrush? _connBrush;    // собственная заливка кнопки «Подключить», чтобы менять цвет плавно


    private void UpdateSwitchButton()
    {
        var active = DnsSwitcher.GetActive();
        SwitchButton.Visibility = active != null && ServerGrid.SelectedItem is DnsServer s && s.Id != active.ServerId
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Изменились настройки «Два DNS». Если подключение есть, переподключаемся к тому же серверу: правила NRPT и общий
    /// DNS ставятся (или снимаются) сразу. Без подключения режим заработает при следующем подключении.
    /// </summary>
    private async Task ApplySplitChangeAsync()
    {
        var s = _settings.SplitDns;
        Log(s.Enabled ? $"Два DNS: включено, общий DNS «{s.GeneralName}»" : "Два DNS: выключено");
        if (DnsSwitcher.GetActive() is not { } active)
        {
            ShowToast(s.Enabled ? "Два DNS заработают при подключении к серверу" : "Два DNS выключены");
            return;
        }
        if (_servers.FirstOrDefault(x => x.Id == active.ServerId) is not { } server) return;
        if (await ConnectToAsync(server, auto: true))
            ShowToast(s.Enabled ? $"Два DNS включены: ИИ через {server.DisplayName}, остальное через «{s.GeneralName}»" : "Два DNS выключены, подключение обычное");
        else
            ShowToast("Применить два DNS не удалось, подробности в журнале", "Bad");
    }

    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly ConnectionMonitor _monitor = new();
    /// <summary>Последнее отключение не удалось. До нового подключения сторож не возвращает DNS: иначе отключиться было бы нельзя.</summary>
    private bool _disconnectFailed;
    /// <summary>Пользователь отказал в правах администратора на автоматическое действие. Сторож и замена сервера ждут его.</summary>
    private bool _uacDeclined;
    private bool _busyConn;

    /// <summary>Главная кнопка: не подключено → подключить к выбранному (или к лучшему); подключено → отключить.</summary>
    private async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (DnsSwitcher.GetActive() != null) { await DisconnectByUserAsync(); return; }
        SelectBest();
        if (ServerGrid.SelectedItem is DnsServer s) await ConnectToAsync(s);
        else Log("Рабочих серверов нет. Нажмите «Найти новые серверы».");
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer s) await ConnectToAsync(s);
    }

    /// <summary>Двойной щелчок / Enter / меню: подключиться или переключиться на выбранный.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ServerGrid.SelectedItem is DnsServer s && DnsSwitcher.GetActive()?.ServerId != s.Id) await ConnectToAsync(s);
    }

    /// <summary>Подключение к серверу. auto — вызывает сторож: без вопросов и окон, только журнал и уведомления.
    /// Возвращает true, если после смены DNS сайты открываются.</summary>
    private async Task<bool> ConnectToAsync(DnsServer s, bool auto = false)
    {
        if (_busyConn) return false;
        // Работает сторонний VPN: DNS из CHIMERION при нем обычно не действует. Предупреждаем до запроса прав, а не после
        if (!auto && SystemCheck.ActiveVpns() is { Count: > 0 } vpnsNow &&
            !DnexDialog.Ask("Работает VPN", $"Включен {string.Join(", ", vpnsNow)}. Пока он работает, запросы идут через него, " +
                "и DNS из CHIMERION, скорее всего, не будет действовать. Лучше сначала выключить VPN.", "Подключить все равно", windowTitle: "Подключение"))
            return false;
        bool connected = false;
        // Для отмены: к какому серверу было подключение до этого (если было)
        var prevActive = DnsSwitcher.GetActive();
        var prevServer = prevActive == null ? null : _servers.FirstOrDefault(x => x.Id == prevActive.ServerId);
        _busyConn = true;
        BeginWork();
        SetConnectContent("Подключаю…");
        try
        {
            ShowBusyBar(); // подключение: сколько осталось, неизвестно, поэтому полоса бегущая
            // 1. Проверка перед подключением — если последняя была давно или неудачной (свежую не повторяем, это экономит время).
            if (s.Status is not (ServerStatus.Ok or ServerStatus.Partial) || s.LastChecked == null ||
                DateTime.Now - s.LastChecked > TimeSpan.FromMinutes(10))
            {
                ProgressText.Text = $"Проверяю «{s.DisplayName}»…";
                await new ServerChecker(_settings).CheckAsync(s, CancellationToken.None);
            }
            if (s.Status == ServerStatus.Fail && (auto ||
                !DnexDialog.Ask($"«{s.DisplayName}» сейчас не работает", $"Последняя проверка: {s.LastError}\n\nВсе равно подключиться?",
                    "Подключиться", windowTitle: "Подключение")))
                return false;

            // Снимок «до»: внешний IP, DNS, куда сейчас резолвятся ИИ-сервисы и открываются ли они.
            ProgressText.Text = "Запоминаю состояние до подключения…";
            _after = null;
            _before = await ConnectionProbe.TakeAsync(_settings);
            ShowCompare();

            // 2. Смена DNS (UAC).
            ProgressText.Text = "Меняю DNS…";
            await DnsSwitcher.ApplyAsync(s, _settings, Log);
            _disconnectFailed = false; // новое подключение: сторож снова следит за ним
            _uacDeclined = false;
            if (!auto) _monitor.ForgetRestores();

            // 3. Проверка, что интернет после смены жив. Если нет — автоматический откат.
            ProgressText.Text = "Проверяю интернет…";
            await Task.Delay(800);
            if (!await DnsSwitcher.SystemDnsWorksAsync(ProbeHosts, TimeSpan.FromSeconds(8)))
            {
                Log("После подключения сайты не открываются. Откатываю настройки.");
                await DnsSwitcher.RestoreAsync(Log);
                ShowToast("Откат: сервер не отвечает", "Bad");
                if (!auto)
                    DnexDialog.Show("Сервер не отвечает", "После подключения сайты не открывались, поэтому настройки DNS возвращены как были.",
                        DialogKind.Warn, "Подключение");
            }
            else
            {
                // Снимок «после» — по нему видно, действительно ли трафик ИИ пошел через Smart DNS.
                ProgressText.Text = "Проверяю результат подключения…";
                _after = await ConnectionProbe.TakeAsync(_settings, leakTest: true);
                ShowCompare();
                SetTraySnapshot(_after);
                LogSnapshot("После подключения", _after);
                ShowToast($"Подключено · {s.DisplayName}\n{RestartHint}");
                Log(RestartHint);
                connected = true;
            }
        }
        catch (UacDeclinedException)
        {
            // Пользователь отказал в правах (UAC): DNS не менялся. Автоматика больше не спрашивает, пока он сам
            // не подключится: иначе сторож и замена сервера показывали бы запрос снова и снова
            _uacDeclined = true;
            Log(auto ? "Права администратора не выданы: подключение не возвращено. Нажмите «Подключить», когда будет удобно"
                     : "Права администратора не выданы: DNS не изменен");
        }
        catch (Exception ex) { Log("Ошибка подключения: " + ex.Message); }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
        if (connected) ReportSystemIssues(auto);
        if (connected && !auto)
        {
            var target = s;
            if (prevServer != null && prevServer != s)
                OfferUndo($"Переключено на {s.DisplayName}", hint: RestartHint, undo: async () =>
                {
                    if (!StillConnectedTo(target)) return;
                    await ConnectToAsync(prevServer);
                });
            else if (prevActive == null)
                OfferUndo($"Подключено к {s.DisplayName}", hint: RestartHint, undo: async () =>
                {
                    if (!StillConnectedTo(target)) return;
                    await DisconnectByUserAsync();
                });
        }
        return connected;
    }

    /// <summary>
    /// После подключения: что в системе мешает DNS из CHIMERION (VPN, безопасный DNS браузера, прокси, hosts,
    /// чужие DNS на других адаптерах). Пишет в журнал и показывает, что отключить. Сторожу окно не показывается.
    /// </summary>
    private void ReportSystemIssues(bool quiet)
    {
        var addrs = DnsSwitcher.GetActive()?.OnAdapters ?? new List<string>();
        var issues = SystemCheck.Run(addrs);
        if (issues.Count == 0) { Log("Помех в системе не найдено"); return; }
        foreach (var i in issues) Log($"Помеха: {i.Title}. {i.Fix}");
        if (quiet) { ShowToast($"Найдено помех: {issues.Count}, подробности в журнале", "Warn"); return; }
        DnexDialog.Show("Подключение работает, но ему может мешать",
            string.Join("\n\n", issues.Select(i => $"• {i.Title}\n{i.Fix}")),
            DialogKind.Warn, "Что отключить");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await DisconnectByUserAsync();

    /// <summary>Отключение по действию пользователя: после него можно отменить и подключиться обратно.</summary>
    private async Task DisconnectByUserAsync()
    {
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        if (await DisconnectAsync("по запросу") && srv != null)
            OfferUndo($"Отключено от {srv.DisplayName}", hint: RestartHint, undo: async () =>
            {
                if (DnsSwitcher.GetActive() != null) { Log("Отмена не выполнена: подключение уже изменилось"); return; }
                await ConnectToAsync(srv);
            });
    }

    /// <summary>Отмена подключения безопасна, только пока подключение то же самое: его мог сменить сторож или пользователь.</summary>
    private bool StillConnectedTo(DnsServer s)
    {
        if (DnsSwitcher.GetActive()?.ServerId == s.Id) return true;
        Log("Отмена не выполнена: подключение уже изменилось");
        return false;
    }

    /// <summary>
    /// Отключение: сначала возвращаются исходные DNS, проверки идут потом. probe=false при выходе из программы:
    /// только возврат DNS, без проверок. Возвращает true, если исходные DNS восстановлены.
    /// </summary>
    private async Task<bool> DisconnectAsync(string reason, bool probe = true)
    {
        if (_busyConn) return false;
        _busyConn = true;
        bool restored = false;
        BeginWork();
        try
        {
            SetConnectContent("Отключаю…");
            ProgressText.Text = "Отключаю…";
            ShowBusyBar();
            // «До» — последняя проверка в подключенном виде. Новая шла бы через DNS, который мог уже умереть,
            // и задержала бы возврат интернета на десятки секунд
            _before = _after ?? _traySnap;
            _after = null;
            await DnsSwitcher.RestoreAsync(Log);
            restored = true;
            _disconnectFailed = false;
            Log($"Отключено ({reason})");
            if (DnsSwitcher.Ipv6NotRestored.Count > 0)
            {
                var names = string.Join(", ", DnsSwitcher.Ipv6NotRestored);
                Log($"IPv6 не включился обратно на адаптере {names}. Включите вручную: Параметры → Сеть и Интернет → " +
                    "Дополнительные сетевые параметры → адаптер → Свойства → «IP версии 6 (TCP/IPv6)». CHIMERION попробует снова при следующем отключении");
                ShowToast($"IPv6 не включился обратно ({names}): как включить вручную, написано в журнале", "Warn");
            }
            ShowToast($"Отключено\n{RestartHint}", "Idle");
            Log(RestartHint);
            if (probe)
            {
                _ = DnsSwitcher.ReportStateAsync(Log); // что осталось в системе: в журнал, уведомление его не ждет
                await Task.Delay(500);
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    _after = await ConnectionProbe.TakeAsync(_settings, limit.Token, leakTest: true);
                    ShowCompare(disconnected: true);
                    LogSnapshot("После отключения", _after);
                }
                catch (OperationCanceledException) { Log("Проверка после отключения не уложилась в 20 секунд"); }
            }
        }
        catch (Exception ex)
        {
            Log("Ошибка отключения: " + ex.Message);
            // Пользователь хотел отключиться: сторож не должен «чинить» подключение обратно, пока его не включат снова
            _disconnectFailed = true;
            ShowToast(ex is UacDeclinedException
                ? "Права администратора не выданы: DNS остался прежним. Нажмите «Отключить» еще раз и разрешите изменения"
                : "Отключиться не удалось, подробности в журнале. Если повторное отключение не поможет, запустите reset-dns.cmd рядом с программой", "Bad");
        }
        finally
        {
            _busyConn = false;
            EndWork();
            await RefreshConnectionAsync();
        }
        return restored;
    }

    /// <summary>Обновляет плашку и кнопки по фактическому состоянию (файл подключения + системный DNS).</summary>
    private async Task RefreshConnectionAsync()
    {
        await RefreshCurrentDnsAsync();
        var active = DnsSwitcher.GetActive();
        bool on = active != null;
        // Стиль кнопки не меняем: цвет плавно переходит от синего к красному (см. AnimateConnectColor).
        var connText = on ? "Отключить" : "Подключить";
        ConnectButton.Content = connText;
        CompactConnect.Content = connText;
        ConnectButton.ToolTip = on ? "Вернуть исходные настройки DNS" : "Подключиться к выбранному серверу. По умолчанию выбирается лучший";
        AnimateConnectColor(on ? "BadButton" : "AccentButton");
        UpdateSwitchButton();
        var activeName = on ? (_servers.FirstOrDefault(x => x.Id == active!.ServerId)?.ShortName ?? DnsServer.Clip(active!.Name)) : null;
        ConnText.Text = on ? $"Подключено · {activeName}" : "Не подключено: нажмите «Подключить»";
        UpdateTray(on ? _servers.FirstOrDefault(x => x.Id == active!.ServerId) : null, activeName);
        foreach (var srv in _servers) srv.IsConnected = on && srv.Id == active!.ServerId;
        _view?.Refresh(); // подключенный сервер поднимается наверх
        UpdateTrayServices();
        UpdateHeaderStats();
        // По ссылке на палитру, а не снимком: при смене темы карточка перекрашивается вместе со всем окном
        ConnPill.SetResourceReference(Border.BorderBrushProperty, on ? "Ok" : "Border");
        ConnPill.SetResourceReference(Border.BackgroundProperty, on ? "OkSoft" : "Surface");
        _monitor.Reset();
        if (on) _watchdog.Start(); else _watchdog.Stop();
        UpdateConnectionLocation();
        UpdateTopList(); // флаги и страны появились: блок «Лучшие по стабильности» перерисуется с ними
        UpdateStatusVisuals();
    }


    /// <summary>
    /// После запуска (обычно вместе с Windows): если было подключение, проверяет, что DNS на месте, и возвращает его.
    /// Статический DNS обычно переживает перезагрузку, но его сбрасывают обновления Windows, новый адаптер
    /// (другой Wi-Fi, USB-модем) или другие программы. Сеть после загрузки поднимается не сразу: ждем до 90 секунд.
    /// </summary>
    private async Task EnsureConnectionAfterStartAsync()
    {
        if (DnsSwitcher.GetActive() is not { } active) return;
        // Копия данных для тестов и снимков: подключение в ней чужое, DNS этого компьютера не трогаем
        if (Environment.GetEnvironmentVariable("DNEX_NO_AUTORESTORE") == "1") return;
        // Режим «Два DNS»: кроме адаптеров проверяем правила NRPT (их мог снять откат системы или другая программа)
        if (ConnectionMonitor.Mine(active).Count == 0 && !active.Split) return;

        var waitUntil = DateTime.Now.AddSeconds(90);
        bool waited = false;
        while (!await SystemCheck.NetworkUpAsync())
        {
            if (DateTime.Now > waitUntil)
            {
                Log("После запуска сети нет. Подключение проверит сторож, когда сеть появится");
                return;
            }
            if (!waited) Log("Жду сеть, чтобы проверить подключение…");
            waited = true;
            await Task.Delay(3000);
        }

        var current = await DnsSwitcher.GetCurrentAddressesAsync();
        if (!ConnectionMonitor.NeedsRestore(active, current, SplitDns.RulesPresent(), out bool rulesLost))
        {
            Log($"DNS «{active.Name}» на месте");
            return;
        }
        Log(rulesLost ? $"После запуска правила для доменов ИИ пропали. Возвращаю «{active.Name}»"
                      : $"После запуска DNS системы другой ({string.Join(", ", current)}). Возвращаю «{active.Name}»");
        if (_servers.FirstOrDefault(x => x.Id == active.ServerId) is not { } server)
        {
            Log($"Сервера «{active.Name}» больше нет в списке. Подключаюсь к лучшему по стабильности");
            await SwitchToNextBestAsync(active);
            return;
        }
        if (await ConnectToAsync(server, auto: true))
        {
            Log($"Подключение «{server.DisplayName}» возвращено");
            return;
        }
        if (_uacDeclined) return; // запрос прав отклонен: замену не ищем, это были бы новые запросы
        Log($"«{server.DisplayName}» не отвечает. Ищу замену");
        await SwitchToNextBestAsync(active);
    }

    /// <summary>
    /// Сторож, пока подключено:
    /// 1) сети нет вообще (сон, загрузка, смена Wi-Fi) — ждет, неудачей это не считается;
    /// 2) DNS системы сбросила другая программа или Windows — возвращает подключение;
    /// 3) DNS не отвечает 3 раза подряд — переключает на следующий лучший по стабильности сервер.
    ///    Отключается и возвращает исходные DNS, только если замена не нашлась.
    /// </summary>
    private async void Watchdog_Tick(object? sender, EventArgs e)
    {
        if (Watchdog.IsChecked != true || _busyConn) return;
        if (_disconnectFailed) return; // пользователь пытался отключиться: подключение не возвращаем
        if (_uacDeclined) return;      // в правах отказали: без действия пользователя запрос не повторяем
        var active = DnsSwitcher.GetActive();
        if (active == null) { _watchdog.Stop(); return; }

        bool up = await SystemCheck.NetworkUpAsync();
        var current = up ? await DnsSwitcher.GetCurrentAddressesAsync() : new List<IPAddress>();
        bool rulesPresent = !active.Split || SplitDns.RulesPresent();
        var vpns = up ? SystemCheck.ActiveVpns() : new List<string>();
        var verdict = await _monitor.TickAsync(active, up, current, rulesPresent,
            () => DnsSwitcher.SystemDnsWorksAsync(ProbeHosts, TimeSpan.FromSeconds(8)), vpns.Count > 0);
        // Проверки шли несколько секунд: за это время пользователь мог начать подключение или отключиться.
        // Тогда решение сторожа устарело: чинить чужое действие нельзя, иначе он сообщил бы о ложном сбое
        if (WatchdogStale(active)) return;
        switch (verdict)
        {
            case ConnectionMonitor.Verdict.WaitNetwork:
                if (_monitor.FirstNoNetwork) Log("Сторож: нет сети, жду. Подключение не трогаю");
                return;
            case ConnectionMonitor.Verdict.Restore:
                // Защита от сброса: на адаптерах должны стоять адреса подключения, в «Двух DNS» и правила NRPT
                Log(!rulesPresent ? $"Сторож: пропали правила для доменов ИИ. Возвращаю «{active.Name}»"
                                  : $"Сторож: DNS системы сброшен ({string.Join(", ", current)}). Возвращаю «{active.Name}»");
                HideUndo();
                if (_servers.FirstOrDefault(x => x.Id == active.ServerId) is { } same && await ConnectToAsync(same, auto: true))
                    ShowToast($"DNS был сброшен. Подключение «{same.DisplayName}» возвращено", "Warn");
                else
                    ShowToast("DNS был сброшен, вернуть подключение не удалось: подробности в журнале", "Bad");
                return;
            case ConnectionMonitor.Verdict.VpnHolds:
                if (_monitor.FirstVpnHold)
                {
                    Log($"Сторож: работает VPN ({string.Join(", ", vpns)}), DNS сейчас определяет он. Пока VPN включен, " +
                        "CHIMERION не возвращает DNS и не меняет сервер. Когда VPN выключат, подключение проверится и вернется само");
                    ShowToast("Работает VPN: пока он включен, CHIMERION не спорит с ним за DNS", "Warn");
                }
                return;
            case ConnectionMonitor.Verdict.Conflict:
                // DNS сбрасывают следом за каждым возвратом: со сторожем спорит другая программа. Дальше он не вмешивается,
                // иначе спор шел бы бесконечно, а без прав администратора еще и с запросом UAC каждую минуту
                _uacDeclined = true;
                Log($"Сторож: DNS сбрасывают снова и снова ({ConnectionMonitor.MaxRestores} раза за {ConnectionMonitor.RestoreWindow.TotalMinutes:0} минут), " +
                    $"сейчас стоит {string.Join(", ", current)}. Похоже, его меняет другая программа: VPN, антивирус, «оптимизатор» сети. " +
                    "Сторож больше не возвращает подключение. Закройте такую программу и нажмите «Подключить»");
                ShowToast("DNS постоянно меняет другая программа: сторож перестал спорить, подробности в журнале", "Bad");
                return;
            case ConnectionMonitor.Verdict.Failing:
                Log($"Сторож: DNS не отвечает ({_monitor.Fails}/{ConnectionMonitor.FailsBeforeSwitch})");
                return;
            case ConnectionMonitor.Verdict.SwitchServer:
                Log($"Сторож: DNS не отвечает ({ConnectionMonitor.FailsBeforeSwitch}/{ConnectionMonitor.FailsBeforeSwitch})");
                await SwitchToNextBestAsync(active);
                return;
        }
    }

    /// <summary>Подключение сменилось или занято другим действием, пока сторож проверял сеть.</summary>
    private bool WatchdogStale(DnsSwitcher.ActiveConnection seen) =>
        _busyConn || DnsSwitcher.GetActive() is not { } now || now.ServerId != seen.ServerId || now.Since != seen.Since;

    /// <summary>Сервер умер: пробуем до трех следующих по стабильности (при равенстве избранные и с меньшим пингом).</summary>
    private async Task SwitchToNextBestAsync(DnsSwitcher.ActiveConnection active)
    {
        HideUndo(); // подключение меняется без пользователя: прежняя отмена больше не про то же состояние
        var next = ConnectionMonitor.Replacements(_servers, active);
        foreach (var s in next)
        {
            if (_uacDeclined) return; // в правах отказали: ни следующих серверов, ни отключения без пользователя
            // Между попытками подключение меняют сами попытки (неудачная откатывает DNS), поэтому здесь проверяется
            // только, не занялся ли подключением пользователь
            if (_busyConn) return;
            Log($"Сторож: «{active.Name}» не отвечает. Переключаюсь на следующий по стабильности: «{s.DisplayName}»");
            if (await ConnectToAsync(s, auto: true))
            {
                ShowToast($"Сервер не отвечал. Переключено на {s.DisplayName}", "Warn");
                return;
            }
        }
        if (_busyConn || _uacDeclined) return; // пользователь уже что-то делает сам или отказал в правах
        Log("Сторож: замена не нашлась. Отключаюсь и возвращаю исходные DNS");
        await DisconnectAsync("сервер перестал отвечать, замена не нашлась");
        DnexDialog.Show($"«{active.Name}» перестал отвечать",
            "Рабочей замены не нашлось. CHIMERION отключилась и вернула исходные настройки DNS.", DialogKind.Warn, "Сторож");
    }


    private HashSet<string> _vpnSeen = new();

    /// <summary>Следит за сетью: если при подключенном CHIMERION включили VPN, предупреждает.</summary>
    private void WatchVpn()
    {
        _vpnSeen = SystemCheck.ActiveVpns().ToHashSet();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(OnNetworkChanged);
    }

    private void OnNetworkChanged()
    {
        var now = SystemCheck.ActiveVpns().ToHashSet();
        var added = now.Except(_vpnSeen).ToList();
        _vpnSeen = now;
        if (added.Count == 0 || DnsSwitcher.GetActive() == null) return;
        var text = $"Включен VPN: {string.Join(", ", added)}. Пока он работает, DNS из CHIMERION может не действовать";
        Log(text);
        ShowToast(text, "Warn");
        // Окно может быть спрятано в трей: уведомление Windows видно всегда
        _tray?.ShowBalloonTip(8000, Loc.T("CHIMERION: включен VPN"), Loc.T("Пока VPN работает, DNS из CHIMERION может не действовать. Отключите VPN или CHIMERION."),
            System.Windows.Forms.ToolTipIcon.Warning);
    }
    private Func<Task>? _undo;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _undoDeadline;
    private TimeSpan _undoLeft;
    private bool _undoPaused;

    /// <summary>
    /// Панель «Отменить» на 8 секунд: что сделано, вторая строка с пояснением, полоска и секунды до исчезновения.
    /// Отменить можно кнопкой или Ctrl+Z. Новое действие заменяет предыдущее. Уведомление справа внизу при этом
    /// не показывается, чтобы внизу не висели две панели.
    /// </summary>
    private void OfferUndo(string text, Func<Task> undo, string? hint = null)
    {
        _undo = undo;
        UndoText.Text = text;
        SetUndoHint(hint);
        HideToast();
        UndoBar.Visibility = Visibility.Visible;
        UndoBar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion(180)));
        _undoPaused = false;
        StartUndoCountdown(UndoTime);
    }

    private void SetUndoHint(string? hint)
    {
        UndoHint.Text = hint ?? "";
        UndoHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartUndoCountdown(TimeSpan left)
    {
        _undoDeadline = DateTime.Now + left;
        UndoTimeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(left.TotalSeconds / UndoTime.TotalSeconds, 0, new Duration(left)));
        UndoSeconds.Text = $"{Math.Ceiling(left.TotalSeconds)} с";
        _undoTimer.Start();
    }

    private void UndoTick()
    {
        var left = _undoDeadline - DateTime.Now;
        if (left <= TimeSpan.Zero) { HideUndo(); return; }
        UndoSeconds.Text = $"{Math.Ceiling(left.TotalSeconds)} с";
    }

    // Под мышью отсчет стоит: можно спокойно прочитать и нажать
    private void UndoBar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_undo == null || _undoPaused) return;
        _undoPaused = true;
        _undoLeft = _undoDeadline - DateTime.Now;
        _undoTimer.Stop();
        var now = UndoTimeScale.ScaleX;
        UndoTimeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        UndoTimeScale.ScaleX = now;
    }

    private void UndoBar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_undo == null || !_undoPaused) return;
        _undoPaused = false;
        StartUndoCountdown(_undoLeft > TimeSpan.FromSeconds(1.5) ? _undoLeft : TimeSpan.FromSeconds(1.5));
    }

    private void HideUndo()
    {
        _undo = null;
        _undoPaused = false;
        _undoTimer.Stop();
        if (UndoBar.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, Motion(160));
        fade.Completed += (_, _) => { if (_undo == null) UndoBar.Visibility = Visibility.Collapsed; };
        UndoBar.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private async Task RunUndoAsync()
    {
        // Во время подключения или отключения отмена подождет: иначе две смены DNS пошли бы одновременно
        if (_busyConn) { Log("Отменить можно после окончания подключения или отключения"); return; }
        var undo = _undo;
        HideUndo();
        if (undo == null) return;
        try { await undo(); }
        catch (Exception ex) { Log("Отменить не удалось: " + ex.Message); }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunUndoAsync();
    private void UndoClose_Click(object sender, RoutedEventArgs e) => HideUndo();

    private async Task RefreshCurrentDnsAsync()
    {
        try { CurrentDns.Text = await DnsSwitcher.GetCurrentAsync(); }
        catch (Exception ex) { CurrentDns.Text = "ошибка: " + ex.Message; }
    }

    /// <summary>Запоминает свежий снимок для трея и перезапускает отсчет до следующей проверки.</summary>
    private readonly EgressWatch _egressWatch = new();

    /// <summary>
    /// Страна, из которой сервис видит подключение, сменилась без смены сервера: прокси перевел трафик на другой узел.
    /// Предупреждение в журнал, на экран и в трей (если окно скрыто). Выход в страну, где сервис закрыт, отмечается красным.
    /// </summary>
    private void CheckEgressChange(NetSnapshot snap)
    {
        var active = DnsSwitcher.GetActive();
        var changes = _egressWatch.Update(active?.ServerId.ToString(), snap.Services.Select(s => (s.Name, s.Egress?.Country)));
        foreach (var c in changes)
        {
            var text = $"{c.Service} теперь видит вас из страны {GeoLocation.CountryName(c.To)}, раньше {GeoLocation.CountryName(c.From)}";
            if (c.ToClosed) text += $". В этой стране {c.Service} не работает: попробуйте другой сервер";
            Log("Смена страны выхода: " + text);
            ShowToast(text, c.ToClosed ? "Bad" : "Warn");
            if (!IsVisible)
                _tray?.ShowBalloonTip(8000, Loc.T("CHIMERION: сменилась страна выхода"), Loc.T(text),
                    c.ToClosed ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Переключает на самый надежный из рабочих серверов, кроме текущего.</summary>
    private async Task SwitchToBestAsync()
    {
        if (_busyConn) { ShowToast("Подождите: идет подключение", "Warn"); return; }
        var active = DnsSwitcher.GetActive();
        var best = _servers
            .Where(s => (s.Status == ServerStatus.Ok || s.Status == ServerStatus.Partial) && s.Id != active?.ServerId)
            .OrderBy(s => s.Rank)
            .FirstOrDefault();
        if (best == null)
        {
            ShowToast("Другого рабочего сервера нет. Проверьте список.", "Warn");
            return;
        }
        Log($"Переключение на лучший сервер: {best.DisplayName}");
        await ConnectToAsync(best);
    }

    /// <summary>Крутящийся значок: штриховой круг, вращается бесконечно.</summary>
    private static System.Windows.Shapes.Ellipse MakeSpinner(double size, Brush brush)
    {
        var rot = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, StrokeThickness = 2, Stroke = brush,
            StrokeDashArray = new DoubleCollection { 2.2, 1.6 },
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = rot,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return ring;
    }

    /// <summary>На кнопке «Подключить» — значок и текст текущего шага.</summary>
    private void SetConnectContent(string text)
    {
        ConnectButton.Content = BuildConnectContent(text);
        CompactConnect.Content = BuildConnectContent(text);
    }

    /// <summary>Содержимое кнопки подключения: значок и текст текущего шага.</summary>
    private StackPanel BuildConnectContent(string text)
    {
        // Цвет надписи кнопки (подобран по контрасту с ее заливкой): белый терялся на лаймовой кнопке
        var fg = ConnectButton.Foreground as Brush ?? Brushes.White;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(MakeSpinner(13, fg));
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(9, 0, 0, 0), FontWeight = FontWeights.SemiBold, Foreground = fg });
        return sp;
    }

    private void AnimateConnectColor(string key)
    {
        var target = ((SolidColorBrush)FindResource(key)).Color;
        // Текст по контрасту с заливкой: свой цвет кнопок может быть светлым
        ConnectButton.Foreground = CompactConnect.Foreground = new SolidColorBrush(ThemeColors.OnFill(target));
        if (_connBrush == null)
        {
            _connBrush = new SolidColorBrush(target);
            ConnectButton.Background = _connBrush;
            CompactConnect.Background = _connBrush;
            return;
        }
        var anim = new ColorAnimation(target, Motion(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            _connBrush!.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _connBrush.Color = target;
        };
        _connBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>Короткое уведомление в углу; само гаснет через пару секунд.</summary>
    /// <summary>
    /// Приложения Claude и ChatGPT (Electron) держат открытые соединения и свой кэш адресов, поэтому после смены DNS
    /// могут продолжать ходить по старым адресам. Программа их не трогает: закрытие чужих соединений обрывало бы
    /// и другие программы. Пользователь перезапускает приложения сам.
    /// </summary>
    private const string RestartHint = "Если Claude или ChatGPT открыты, перезапустите их: приложения помнят старые адреса";
}
