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

    /// <summary>
    /// Сервер можно предлагать и подключать сам: выход в выбранной стране (если она выбрана) и адрес выхода не в черном
    /// списке. Вручную пользователь может подключиться к любому, программа только предупредит.
    /// </summary>
    private static bool Usable(DnsServer s) => AccountSafety.Fits(s) && !IpReputation.IsBad(s);

    /// <summary>Выбранная страна из настроек вступает в силу (при выключенной защите аккаунтов страна не закреплена).</summary>
    private void ApplyCountryPin() => AccountSafety.Pinned = _settings.AccountSafetyGuard && _settings.EgressCountry is { Length: 2 } c ? c.ToUpperInvariant() : null;

    private readonly HashSet<string> _repWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// После проверки подключения: репутация адресов выхода, через которые нейросети видят вас. Только адреса прокси
    /// Smart DNS (сервис идет через сервер), свой IP пользователя не проверяется.
    /// </summary>
    private async Task CheckEgressReputationAsync(NetSnapshot snap)
    {
        if (DnsSwitcher.GetActive() is not { } active) return;
        var ips = snap.Services.Where(x => x.Proxied && x.Egress != null).Select(x => x.Egress!.Ip).Distinct().ToList();
        foreach (var ip in ips)
        {
            var rep = await IpReputation.CheckAsync(ip, _settings.ReferenceDoh);
            if (rep == null) continue;
            var key = $"{active.ServerId}|{ip}";
            if (!_repWarned.Add(key)) continue;
            Log($"Репутация IP выхода {ip}: {IpReputation.Describe(rep)}");
            if (rep.Bad)
                Notify($"Адрес, через который нейросети видят вас, на плохом счету ({IpReputation.Describe(rep)}). С таких адресов входы блокируют чаще: лучше сменить сервер", "Warn", logLine: null, feature: FeatureSafety);
        }
        UpdateTopList();
    }

    /// <summary>После проверки серверов: репутация адресов выхода у лучших рабочих (по 10 за раз, итоги хранятся три дня).</summary>
    private async Task EnrichReputationAsync()
    {
        var ips = _servers.Where(s => s.Status is ServerStatus.Ok or ServerStatus.Partial && !s.IsJunk).OrderBy(s => s.Rank)
                          .SelectMany(IpReputation.Unchecked).Distinct().Take(10).ToList();
        if (ips.Count == 0) return;
        foreach (var ip in ips) await IpReputation.CheckAsync(ip, _settings.ReferenceDoh);
        var bad = _servers.Where(IpReputation.IsBad).Select(s => s.DisplayName).ToList();
        if (bad.Count > 0) Log($"Адрес выхода в черном списке или с высоким риском, такие серверы не предлагаются: {string.Join(", ", bad.Take(5))}");
        UpdateTopList();
    }

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
        bool frequent = _safety.Frequent(mism.Select(m => m.Service));
        var seen = mism.SelectMany(m => _safety.Recent(m.Service)).Distinct().Select(GeoLocation.CountryName).ToList();
        Log($"Защита аккаунтов: «{s.DisplayName}» сменит страну входа ({AccountSafety.Describe(mism)}), смена заблокирована" + (frequent ? ", страна уже менялась недавно" : ""));
        var text = $"{AccountSafety.Describe(mism)}.\n\n" +
                   (frequent ? $"За {AccountSafety.RecentDays} дней вы уже входили из: {string.Join(", ", seen)}. " : "") +
                   "Частая смена локации грозит баном аккаунта. Лучше сервер с той же страной (колонка «Где»).";
        bool yes = DnexDialog.AskRisk(frequent ? "Смена страны заблокирована: она уже менялась недавно" : "Смена страны заблокирована",
            text, "Понимаю, что аккаунт могут заблокировать", "Сменить на свой риск", "Не менять страну", windowTitle: "Защита аккаунтов");
        Log(yes ? "Защита аккаунтов: смену страны подтвердили вручную, на свой страх и риск" : "Защита аккаунтов: смену страны отменили, подключение не меняется");
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
            Notify($"Сервисы видят вас не из привычной страны ({AccountSafety.Describe(fresh)}). Частая смена страны повышает риск блокировки", "Warn",
                   logLine: $"Защита аккаунтов: сервисы видят вас не из привычной страны ({AccountSafety.Describe(fresh)})", feature: FeatureSafety);
        // В историю пишем только подтвержденную страну: разовый скачок не должен становиться «привычным»
        bool changed = false;
        foreach (var (service, country) in now)
            if (!mism.Any(m => m.Service == service) || confirmed.Any(m => m.Service == service)) changed |= _safety.Record(service, country);
        if (changed) _safety.Save();
    }
}
