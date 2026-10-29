using System.Net;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Решения сторожа подключения без окна и без сети: на месте ли DNS, что делать на очередном такте, на какой
/// сервер переключаться, если текущий умер. Окно только собирает факты (есть ли сеть, какие DNS стоят, живы ли
/// правила NRPT, отвечает ли DNS) и выполняет решение. Так поведение при сбросе DNS другой программой, после
/// перезагрузки и при смерти сервера проверяется тестами.
/// </summary>
public sealed class ConnectionMonitor
{
    /// <summary>Сколько тактов подряд DNS может не отвечать, прежде чем сторож сменит сервер.</summary>
    public const int FailsBeforeSwitch = 3;

    public enum Verdict
    {
        /// <summary>Сети нет совсем (сон, загрузка, смена Wi-Fi): ждем, неудачей это не считается.</summary>
        WaitNetwork,
        /// <summary>Все на месте и DNS отвечает.</summary>
        Ok,
        /// <summary>DNS адаптеров сбросила другая программа или Windows, либо пропали правила NRPT: вернуть подключение.</summary>
        Restore,
        /// <summary>DNS не ответил, но лимит неудач еще не исчерпан.</summary>
        Failing,
        /// <summary>DNS не отвечает FailsBeforeSwitch раз подряд: переключиться на следующий лучший сервер.</summary>
        SwitchServer,
        /// <summary>
        /// Работает сторонний VPN: DNS адаптеров или ответы определяет он. Сторож не возвращает DNS и не меняет сервер,
        /// иначе спорил бы с VPN по кругу (с запросом прав каждый раз). Когда VPN выключат, все проверится заново.
        /// </summary>
        VpnHolds,
        /// <summary>DNS сбрасывают снова и снова: другая программа меняет его следом за сторожем. Сторож перестает спорить.</summary>
        Conflict,
    }

    /// <summary>Неудачи подряд на текущий момент.</summary>
    public int Fails { get; private set; }

    /// <summary>Об отсутствии сети уже написали в журнал (пишем один раз, пока сеть не вернется).</summary>
    public bool NoNetworkReported { get; private set; }

    /// <summary>Сколько раз писать в журнал «нет сети»: true только на первом такте без сети.</summary>
    public bool FirstNoNetwork { get; private set; }

    /// <summary>Сколько возвратов DNS подряд терпим за окно RestoreWindow, прежде чем решить, что с нами спорит другая программа.</summary>
    public const int MaxRestores = 3;
    public static readonly TimeSpan RestoreWindow = TimeSpan.FromMinutes(15);

    private readonly List<DateTime> _restores = new();
    /// <summary>Часы. Подменяются в тестах.</summary>
    internal Func<DateTime> Now = () => DateTime.Now;

    /// <summary>Первый такт, на котором VPN держит DNS: в журнал пишем один раз, пока VPN не выключат.</summary>
    public bool FirstVpnHold { get; private set; }
    private bool _vpnHolding;

    /// <summary>Новый такт после смены подключения. Счетчик возвратов DNS не обнуляется: возврат сам меняет подключение.</summary>
    public void Reset() { Fails = 0; NoNetworkReported = false; FirstNoNetwork = false; _vpnHolding = false; FirstVpnHold = false; }

    /// <summary>Пользователь подключился сам: прежние возвраты DNS больше не считаются спором.</summary>
    public void ForgetRestores() => _restores.Clear();

    /// <summary>
    /// Адреса подключения пропали с адаптеров или пропали правила NRPT режима «Два DNS». Подключение,
    /// которое адаптеры не меняло (в «Двух DNS» общий DNS не задан), проверяется только по правилам.
    /// </summary>
    public static bool NeedsRestore(DnsSwitcher.ActiveConnection active, IEnumerable<IPAddress> current, bool rulesPresent, out bool rulesLost, IReadOnlyCollection<IReadOnlyCollection<IPAddress>>? adapters = null)
    {
        var mine = Mine(active);
        rulesLost = active.Split && !rulesPresent;
        return rulesLost || (mine.Count > 0 && (adapters is { Count: > 0 }
            ? adapters.Any(adapter => !mine.Any(adapter.Contains)) : !mine.Any(current.Contains)));
    }

    /// <summary>Адреса, которые подключение ставило на адаптеры.</summary>
    public static List<IPAddress> Mine(DnsSwitcher.ActiveConnection active) =>
        active.OnAdapters.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();

    /// <summary>
    /// Один такт сторожа. dnsWorks вызывается, только если сеть есть и DNS на месте: это сетевой запрос.
    /// </summary>
    public async Task<Verdict> TickAsync(DnsSwitcher.ActiveConnection active, bool networkUp, IReadOnlyCollection<IPAddress> current,
                                         bool rulesPresent, Func<Task<bool>> dnsWorks, bool vpnActive = false, IReadOnlyCollection<IReadOnlyCollection<IPAddress>>? adapters = null)
    {
        if (!networkUp)
        {
            FirstNoNetwork = !NoNetworkReported;
            NoNetworkReported = true;
            Fails = 0;
            return Verdict.WaitNetwork;
        }
        NoNetworkReported = false;
        FirstNoNetwork = false;
        bool restore = NeedsRestore(active, current, rulesPresent, out _, adapters);
        bool works = restore || await dnsWorks();
        // VPN включен и DNS «сброшен» или не отвечает: это делает VPN, а не сбой сервера. Ждем, пока его выключат
        if (vpnActive && (restore || !works))
        {
            FirstVpnHold = !_vpnHolding;
            _vpnHolding = true;
            Fails = 0;
            return Verdict.VpnHolds;
        }
        _vpnHolding = false;
        FirstVpnHold = false;
        if (restore)
        {
            var now = Now();
            _restores.RemoveAll(t => now - t > RestoreWindow);
            if (_restores.Count >= MaxRestores) return Verdict.Conflict;
            _restores.Add(now);
            return Verdict.Restore;
        }
        if (works) { Fails = 0; return Verdict.Ok; }
        if (++Fails < FailsBeforeSwitch) return Verdict.Failing;
        Fails = 0;
        return Verdict.SwitchServer;
    }

    /// <summary>
    /// Замена умершему серверу: до take рабочих серверов, кроме текущего и мусора. Порядок: стабильность «все сервисы
    /// сразу» (у старых данных общая), потом избранные, потом пинг. Не проверявшиеся давно идут после свежих
    /// при равной стабильности: их результат мог устареть.
    /// </summary>
    /// <param name="fit">Насколько сервер подходит по стране выхода (AccountSafety.Fit): при близкой стабильности
    /// (в пределах 10%) выше тот, что не меняет привычную страну сервисов.</param>
    public static List<DnsServer> Replacements(IEnumerable<DnsServer> servers, DnsSwitcher.ActiveConnection active, int take = 3, DateTime? now = null,
                                               Func<DnsServer, int>? fit = null)
    {
        var t = now ?? DateTime.Now;
        return servers
            .Where(s => s.Id != active.ServerId && !s.IsJunk && s.Status == ServerStatus.Ok)
            .OrderByDescending(s => Stability(s) / 10)
            .ThenByDescending(s => fit?.Invoke(s) ?? 0)
            .ThenByDescending(s => Stability(s))
            .ThenByDescending(s => s.IsFavorite)
            .ThenByDescending(s => ServerMetrics.Of(s, t).Fresh)
            .ThenBy(s => s.LatencyMs ?? long.MaxValue)
            .Take(take)
            .ToList();
    }

    /// <summary>По каждому сервису, у которого известна страна выхода основного, запасной выводит туда же.</summary>
    public static bool SameCountry(DnsServer primary, DnsServer backup) =>
        primary.EgressBy.All(kv => backup.EgressBy.TryGetValue(kv.Key, out var e) && string.Equals(e.Country, kv.Value.Country, StringComparison.OrdinalIgnoreCase));

    private static int Stability(DnsServer s) => s.AllStability ?? (s.ChecksTotal == 0 ? 0 : s.ChecksOk * 100 / s.ChecksTotal);

    /// <summary>
    /// Запасной сервер для подключения к primary: рабочий, с обычным IPv4-адресом, не совпадающим с адресами primary
    /// (другой сервер, а не второй адрес того же). Windows переходит на него сама, если основной перестал отвечать,
    /// а иногда и разово, если основной ответил медленно.
    /// Поэтому запасной обязан выводить сервисы в ту же страну, что и основной: иначе ChatGPT и Claude видели бы
    /// вход то из Нидерландов, то из Казахстана, а такая смена страны грозит блокировкой аккаунта. Если страна выхода
    /// основного или кандидата неизвестна, запасного нет: лучше без него, чем с чужой страной.
    /// Среди подходящих выше тот, что лучше по истории стран (fit), затем стабильность и пинг.
    /// </summary>
    public static DnsServer? Backup(IEnumerable<DnsServer> servers, DnsServer primary, Func<DnsServer, int>? fit = null, DateTime? now = null)
    {
        var own = new HashSet<string>(new[] { primary.Primary, primary.Secondary }.Where(a => !string.IsNullOrEmpty(a))!, StringComparer.OrdinalIgnoreCase);
        var t = now ?? DateTime.Now;
        if (primary.EgressBy.Count == 0) return null;
        return servers
            .Where(s => s.Id != primary.Id && !s.IsJunk && s.Status == ServerStatus.Ok
                        && IPAddress.TryParse(s.Primary, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !own.Contains(s.Primary!) && SameCountry(primary, s))
            .OrderByDescending(s => fit?.Invoke(s) ?? 0)
            .ThenByDescending(s => Stability(s) / 10)
            .ThenByDescending(s => ServerMetrics.Of(s, t).Fresh)
            .ThenBy(s => s.LatencyMs ?? long.MaxValue)
            .FirstOrDefault();
    }
}
