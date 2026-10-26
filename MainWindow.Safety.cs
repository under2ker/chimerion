using Dnex.Models;
using Dnex.Services;

namespace Dnex;

/// <summary>Защита аккаунтов: из какой страны сервисы видят подключение и как ее не дергать без нужды (AccountSafety)</summary>
public partial class MainWindow
{
    private readonly AccountSafety _safety = AccountSafety.Load();
    /// <summary>О каких сервисах уже предупредили в этом подключении: повторять каждые 5 минут незачем.</summary>
    private readonly HashSet<string> _safetyWarned = new(StringComparer.OrdinalIgnoreCase);
    private string? _safetyFor;

    /// <summary>Подбор замены и запасного сервера с учетом страны выхода, если защита включена.</summary>
    private Func<DnsServer, int>? SafetyFit => _settings.AccountSafetyGuard ? _safety.Fit : null;

    /// <summary>
    /// Перед подключением вручную: если сервер выведет сервисы не в привычную страну, спросить. true — подключаться.
    /// </summary>
    private bool ConfirmCountryChange(DnsServer s)
    {
        if (!_settings.AccountSafetyGuard) return true;
        var mism = _safety.Compare(AccountSafety.EgressOf(s));
        if (mism.Count == 0) return true;
        Log($"Защита аккаунтов: «{s.DisplayName}» сменит страну для сервисов ({AccountSafety.Describe(mism)})");
        return DnexDialog.Ask("Сменится страна для сервисов",
            $"{AccountSafety.Describe(mism)}.\n\nСервисы привыкли видеть вас из другой страны. Частая смена страны входа повышает риск блокировки аккаунта. " +
            "Если есть выбор, лучше сервер с той же страной: она видна в колонке «Где» и в подсказке к ней.",
            "Подключиться все равно", windowTitle: "Защита аккаунтов");
    }

    /// <summary>
    /// После каждой проверки подключения: запомнить, из какой страны сервисы видят вас сейчас, и предупредить, если
    /// страна не та, что обычно. При стороннем VPN не записываем: страну тогда определяет он.
    /// </summary>
    private void TrackEgress(NetSnapshot snap)
    {
        if (!_settings.AccountSafetyGuard || DnsSwitcher.GetActive() is not { } active || SystemCheck.ActiveVpns().Count > 0) return;
        var key = $"{active.ServerId}|{active.Since:O}";
        if (_safetyFor != key) { _safetyWarned.Clear(); _safetyFor = key; }
        var now = snap.Services.Where(x => x.Egress != null && x.Access.StartsWith('✓'))
                               .Select(x => new KeyValuePair<string, string?>(x.Name, x.Egress!.Country)).ToList();
        var fresh = _safety.Compare(now).Where(m => _safetyWarned.Add(m.Service)).ToList();
        if (fresh.Count > 0)
            Notify($"Защита аккаунтов: сервисы видят вас не из привычной страны ({AccountSafety.Describe(fresh)}). Частая смена страны повышает риск блокировки", "Warn");
        bool changed = false;
        foreach (var (service, country) in now) changed |= _safety.Record(service, country);
        if (changed) _safety.Save();
    }
}
