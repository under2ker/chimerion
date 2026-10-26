using System.Windows;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>Фоновая проверка ИИ-сервисов и самого DNS-сервера с уведомлениями о падении и восстановлении</summary>
public partial class MainWindow
{
    private readonly ServiceHealth _health = new();
    /// <summary>Подключение, к которому относится история _health (сервер и время подключения).</summary>
    private string? _healthFor;
    /// <summary>Избранные серверы, которые не в подключении: отвечают ли они на DNS (по фоновому обновлению пинга).</summary>
    private readonly ServiceHealth _favHealth = new();

    /// <summary>
    /// После каждой проверки подключения (по таймеру трея, после подключения): что с каждым ИИ-сервисом и отвечает
    /// ли сам DNS-сервер. О падении — уведомление в окне и в Windows (щелчок предлагает сменить сервер), по желанию
    /// автоматическое переключение. О восстановлении — короткое уведомление. Пока есть неподтвержденная неудача,
    /// следующая проверка идет через минуту, а не через обычный интервал.
    /// </summary>
    private async Task CheckServiceHealthAsync(NetSnapshot snap)
    {
        if (DnsSwitcher.GetActive() is not { } active || _busyConn || snap.Services.Count == 0) return;
        var key = $"{active.ServerId}|{active.Since:O}";
        if (_healthFor != key) { _health.Reset(); _healthFor = key; }
        // Работает сторонний VPN: запросы идут через него, и неудачи не про сервер. Ложных тревог не поднимаем
        if (SystemCheck.ActiveVpns().Count > 0) return;
        var server = _servers.FirstOrDefault(x => x.Id == active.ServerId);
        var name = server?.ShortName ?? DnsServer.Clip(active.Name);

        var changes = _health.Update(snap.Services.Select(x => (x.Name, ServiceHealth.Classify(x))));
        bool? serverChange = null;
        if (server != null)
        {
            // Прямой запрос к серверу: отвечает ли он сам (Windows при молчащем сервере тихо уходит на запасной DNS)
            var ping = await new ServerChecker(_settings).MeasurePingAsync(server, CancellationToken.None);
            if (DnsSwitcher.GetActive()?.ServerId != active.ServerId) return; // пока ждали, подключение сменилось
            serverChange = _health.UpdateServer(ping != null);
        }

        if (serverChange == true)
        {
            // Есть запасной сервер и он отвечает: Windows уже работает через него, сервисы живы. Сообщаем мягче и
            // предлагаем сделать запасной основным (тогда подберется новый запасной)
            var backup = active.BackupId is { } bid ? _servers.FirstOrDefault(x => x.Id == bid) : null;
            if (backup != null && await new ServerChecker(_settings).MeasurePingAsync(backup, CancellationToken.None) != null)
                ReportBackupInUse(active, name, backup);
            else
                ReportDown(active, name, $"{name} не отвечает на DNS-запросы. Windows перешла на запасной DNS, ИИ-сервисы через Smart DNS не работают");
        }
        var down = changes.Where(x => x.Down).ToList();
        if (down.Count > 0 && serverChange != true) ReportDown(active, name, ServiceHealth.DownText(name, down));
        var up = changes.Where(x => !x.Down).ToList();
        if (serverChange == false) Notify($"{name} снова отвечает на DNS-запросы", "Ok");
        if (up.Count > 0) Notify(ServiceHealth.UpText(name, up), "Ok");

        // Неподтвержденная неудача: перепроверить через минуту, чтобы быстрее узнать о падении или о том, что все в порядке
        var normal = TimeSpan.FromMinutes(Math.Clamp(_settings.TrayCheckMinutes, 1, 60));
        var next = _health.Suspicious ? TimeSpan.FromMinutes(1) : normal;
        if (_trayTimer.Interval != next) { _trayTimer.Stop(); _trayTimer.Interval = next; _trayTimer.Start(); }
    }

    /// <summary>Сервисы или сервер упали: журнал, уведомление и, если включено, переключение на лучший рабочий сервер.</summary>
    private void ReportDown(DnsSwitcher.ActiveConnection active, string name, string text)
    {
        Log("Проверка сервисов: " + text);
        if (_settings.AutoSwitchOnServiceDown && _settings.AllowServerSwitch && !_uacDeclined)
        {
            Notify(text + ". Переключаюсь на лучший рабочий сервер", "Bad");
            _ = SwitchToNextBestAsync(active);
            return;
        }
        ShowToast(text, "Bad");
        if (_tray != null)
        {
            _balloonClick = () =>
            {
                ShowFromTray();
                if (DnsSwitcher.GetActive() is { } now && now.ServerId == active.ServerId &&
                    DnexDialog.Ask("Сменить сервер?", text + ".\n\nCHIMERION подключится к лучшему рабочему серверу из списка.", "Сменить", windowTitle: "Проверка сервисов"))
                    _ = SwitchToNextBestAsync(now, byUser: true);
            };
            _tray.ShowBalloonTip(10000, Loc.T("CHIMERION: сервисы перестали открываться"), PrivacyGuard.Mask(Loc.T(text)) + Loc.T(". Нажмите, чтобы сменить сервер"),
                System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Основной сервер молчит, работает запасной: уведомление и предложение сделать запасной основным.</summary>
    private void ReportBackupInUse(DnsSwitcher.ActiveConnection active, string name, DnsServer backup)
    {
        var text = $"{name} не отвечает, работает запасной сервер {backup.ShortName}";
        Log("Проверка сервисов: " + text);
        if (_settings.AutoSwitchOnServiceDown && _settings.AllowServerSwitch && !_uacDeclined)
        {
            Notify(text + ". Делаю его основным", "Warn");
            _ = ConnectToAsync(backup, auto: true);
            return;
        }
        ShowToast(text, "Warn");
        if (_tray != null)
        {
            _balloonClick = () =>
            {
                ShowFromTray();
                if (DnsSwitcher.GetActive() is { } now && now.ServerId == active.ServerId &&
                    DnexDialog.Ask("Сделать запасной сервер основным?", text + ".\n\nCHIMERION подключится к нему и подберет новый запасной.", "Сделать основным", windowTitle: "Проверка сервисов"))
                    _ = ConnectToAsync(backup);
            };
            _tray.ShowBalloonTip(8000, "CHIMERION", PrivacyGuard.Mask(Loc.T(text)) + Loc.T(". Нажмите, чтобы сделать его основным"), System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Уведомление в окне и, если окно спрятано, в Windows.</summary>
    private void Notify(string text, string kind)
    {
        Log("Проверка сервисов: " + text);
        ShowToast(text, kind);
        if (!IsVisible && _tray != null)
            _tray.ShowBalloonTip(6000, "CHIMERION", PrivacyGuard.Mask(Loc.T(text)),
                kind == "Bad" ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// Избранные серверы (кроме того, к которому подключено) после фонового обновления пинга: перестал ли какой-то
    /// отвечать на DNS. Сообщаем один раз, когда перестал, и один раз, когда снова отвечает.
    /// </summary>
    private void CheckFavorites(IReadOnlyCollection<DnsServer> answered)
    {
        var activeId = DnsSwitcher.GetActive()?.ServerId;
        var favs = _servers.Where(x => x.IsFavorite && x.Id != activeId).ToList();
        if (favs.Count == 0) return;
        var byKey = favs.ToDictionary(x => x.Id.ToString());
        var changes = _favHealth.Update(favs.Select(x => (x.Id.ToString(), answered.Contains(x) ? ServiceHealth.Problem.None : ServiceHealth.Problem.NoDns)));
        foreach (var ch in changes)
        {
            if (!byKey.TryGetValue(ch.Service, out var srv)) continue;
            Notify(ch.Down ? $"Избранный сервер {srv.ShortName} перестал отвечать на DNS-запросы" : $"Избранный сервер {srv.ShortName} снова отвечает", ch.Down ? "Warn" : "Ok");
        }
    }
}
