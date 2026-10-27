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
    /// <summary>Сервис и чужая страна, увиденные в прошлой проверке: предупреждаем, только если повторилось.</summary>
    private readonly Dictionary<string, string> _safetyPending = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Сервер, к которому человек подключился, сам согласившись на другую страну: о ней не напоминаем.</summary>
    private Guid? _countryAccepted;

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
        bool yes = DnexDialog.Ask("Сменится страна для сервисов",
            $"{AccountSafety.Describe(mism)}.\n\nЧастая смена страны входа повышает риск блокировки аккаунта. Лучше сервер с той же страной (колонка «Где»).",
            "Подключиться все равно", windowTitle: "Защита аккаунтов");
        if (yes) _countryAccepted = s.Id; // решение принято: через пару минут не переспрашиваем уведомлением
        return yes;
    }

    /// <summary>
    /// После каждой проверки подключения: запомнить, из какой страны сервисы видят вас сейчас, и предупредить, если
    /// страна не та, что обычно. При стороннем VPN не записываем: страну тогда определяет он.
    /// </summary>
    private void TrackEgress(NetSnapshot snap)
    {
        if (!_settings.AccountSafetyGuard || DnsSwitcher.GetActive() is not { } active || SystemCheck.ActiveVpns().Count > 0) return;
        var key = $"{active.ServerId}|{active.Since:O}";
        if (_safetyFor != key) { _safetyWarned.Clear(); _safetyPending.Clear(); _safetyFor = key; }
        var now = snap.Services.Where(x => x.Egress != null && x.Access.StartsWith('✓'))
                               .Select(x => new KeyValuePair<string, string?>(x.Name, x.Egress!.Country)).ToList();
        // Чужая страна засчитывается со второй проверки подряд: разовый уход Windows на запасной DNS не повод пугать
        var mism = _safety.Compare(now);
        var confirmed = mism.Where(m => _safetyPending.TryGetValue(m.Service, out var c) && c == m.Now).ToList();
        _safetyPending.Clear();
        foreach (var m in mism) _safetyPending[m.Service] = m.Now;
        var fresh = active.ServerId == _countryAccepted ? new List<AccountSafety.Mismatch>() : confirmed.Where(m => _safetyWarned.Add(m.Service)).ToList();
        if (fresh.Count > 0)
            Notify($"Защита аккаунтов: сервисы видят вас не из привычной страны ({AccountSafety.Describe(fresh)}). Частая смена страны повышает риск блокировки", "Warn",
                   logLine: $"Защита аккаунтов: сервисы видят вас не из привычной страны ({AccountSafety.Describe(fresh)})");
        // В историю пишем только подтвержденную страну: разовый скачок не должен становиться «привычным»
        bool changed = false;
        foreach (var (service, country) in now)
            if (!mism.Any(m => m.Service == service) || confirmed.Any(m => m.Service == service)) changed |= _safety.Record(service, country);
        if (changed) _safety.Save();
    }
}
