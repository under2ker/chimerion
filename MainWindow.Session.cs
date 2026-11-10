using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Защищенный сеанс»: пока он включен, программа не меняет маршрут сама. Не переходит на другой сервер, не
/// возвращается на прежний, не ставит запасной (Windows иногда разово уходит на него, и сервис видит другой адрес).
/// Если сервер перестал отвечать, сообщает и предлагает замену, а при смене сервера вручную объясняет, что изменится.
/// Граница: если чужой DNS-сервер сам сменит свой выход, запрет переключений внутри программы этого не предотвратит.
/// </summary>
public partial class MainWindow
{
    private System.Windows.Forms.ToolStripMenuItem? _traySession;
    /// <summary>Сколько раз за сеанс сменился сервер (только с согласия человека).</summary>
    private int _sessionSwitches;

    private bool SessionOn => _settings.ProtectedSession;
    /// <summary>Программа может сама сменить сервер: разрешено в настройках и не идет защищенный сеанс.</summary>
    private bool AutoSwitchOk => _settings.AllowServerSwitch && !SessionOn;
    /// <summary>Ставить запасной сервер при подключении.</summary>
    private bool BackupOk => _settings.UseBackupServer && AutoSwitchOk;

    private void SetSession(bool on)
    {
        if (_settings.ProtectedSession == on) return;
        _settings.ProtectedSession = on;
        _sessionSwitches = 0;
        try { Storage.Save("settings.json", _settings); } catch (Exception) { }
        Log(on ? "Защищенный сеанс включен: сервер сам не меняется, запасной не ставится"
               : "Защищенный сеанс выключен: смена сервера снова по настройкам «Сторож и трей»");
        if (on && DnsSwitcher.GetActive() is { BackupId: not null })
            ShowToast("Защищенный сеанс включен. Запасной сервер снимется после переподключения");
        else
            ShowToast(on ? "Защищенный сеанс включен: сервер сам не меняется" : "Защищенный сеанс выключен");
        UpdateSessionTray();
    }

    private void UpdateSessionTray()
    {
        if (_traySession == null) return;
        _traySession.Text = SessionOn ? "Защищенный сеанс: выключить" : "Защищенный сеанс: включить";
        _traySession.ToolTipText = SessionOn
            ? $"Сервер сам не меняется. Смен за сеанс: {_sessionSwitches}"
            : "Не менять подключение, пока вы работаете: сервер сам не меняется, запасной не ставится";
    }

    /// <summary>
    /// Смена сервера вручную во время сеанса: объяснить, что Claude увидит другой адрес. Если страна выхода другая,
    /// об этом уже спросила защита аккаунтов, второй вопрос не задаем. true — менять.
    /// </summary>
    private bool ConfirmSessionSwitch(DnsServer target, DnsSwitcher.ActiveConnection? current)
    {
        if (!SessionOn || current == null || current.ServerId == target.Id) return true;
        if (_settings.AccountSafetyGuard && _safety.Compare(AccountSafety.EgressOf(target)).Count > 0) return true;
        var claude = target.EgressBy.TryGetValue("Claude", out var e) && e.FreshAt(DateTime.Now) ? e : null;
        var text = $"Идет защищенный сеанс: Claude видит вас через «{DnsServer.Clip(current.Name)}». После смены на «{target.DisplayName}» адрес выхода изменится" +
                   (claude != null ? $" (страна по последней проверке: {GeoLocation.CountryName(claude.Country)})" : ", страна выхода этого сервера еще не измерена") +
                   ".\n\nЧастая смена адреса повышает риск проверок аккаунта. Менять, только если текущий сервер не работает.";
        bool yes = DnexDialog.Ask("Сменить сервер во время сеанса?", text, "Сменить сервер", windowTitle: "Защищенный сеанс");
        Log(yes ? $"Защищенный сеанс: смену на «{target.DisplayName}» подтвердили" : "Защищенный сеанс: смену сервера отменили");
        return yes;
    }

    /// <summary>Подключение сменило сервер во время сеанса: считаем смены (видно в подсказке пункта трея).</summary>
    private void CountSessionSwitch(DnsSwitcher.ActiveConnection? before, DnsServer now)
    {
        if (!SessionOn || before == null || before.ServerId == now.Id) return;
        _sessionSwitches++;
        UpdateSessionTray();
    }

    private DateTime _sessionDownNotified = DateTime.MinValue;

    /// <summary>
    /// Сервер перестал отвечать во время сеанса: сами не меняем, сообщаем раз в 30 минут и предлагаем замену щелчком.
    /// </summary>
    private void ReportSessionDown(DnsSwitcher.ActiveConnection active, string why)
    {
        if (DateTime.Now - _sessionDownNotified < TimeSpan.FromMinutes(30)) return;
        _sessionDownNotified = DateTime.Now;
        var text = $"{why}. Идет защищенный сеанс: сервер сам не меняю";
        Log("Защищенный сеанс: " + text);
        if (IsVisible && WindowState != System.Windows.WindowState.Minimized) ShowToast(text, "Bad", FeatureWatchdog);
        Balloon(10000, NoticeTitle(FeatureWatchdog), PrivacyGuard.Mask(Loc.T(text)) + Loc.T(". Нажмите, чтобы выбрать замену"),
            System.Windows.Forms.ToolTipIcon.Warning, () =>
        {
            ShowFromTray();
            if (DnsSwitcher.GetActive() is { } now && now.ServerId == active.ServerId &&
                DnexDialog.Ask("Сменить сервер?", text + ".\n\nCHIMERION подключится к лучшему рабочему серверу с той же страной выхода. Claude увидит другой адрес.",
                    "Сменить", windowTitle: "Защищенный сеанс"))
                _ = SwitchToNextBestAsync(now, byUser: true);
        });
    }
}
