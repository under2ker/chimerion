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
    }

    /// <summary>Неудачи подряд на текущий момент.</summary>
    public int Fails { get; private set; }

    /// <summary>Об отсутствии сети уже написали в журнал (пишем один раз, пока сеть не вернется).</summary>
    public bool NoNetworkReported { get; private set; }

    /// <summary>Сколько раз писать в журнал «нет сети»: true только на первом такте без сети.</summary>
    public bool FirstNoNetwork { get; private set; }

    public void Reset() { Fails = 0; NoNetworkReported = false; FirstNoNetwork = false; }

    /// <summary>
    /// Адреса подключения пропали с адаптеров или пропали правила NRPT режима «Два DNS». Подключение,
    /// которое адаптеры не меняло (в «Двух DNS» общий DNS не задан), проверяется только по правилам.
    /// </summary>
    public static bool NeedsRestore(DnsSwitcher.ActiveConnection active, IEnumerable<IPAddress> current, bool rulesPresent, out bool rulesLost)
    {
        var mine = Mine(active);
        rulesLost = active.Split && !rulesPresent;
        return rulesLost || (mine.Count > 0 && !mine.Any(current.Contains));
    }

    /// <summary>Адреса, которые подключение ставило на адаптеры.</summary>
    public static List<IPAddress> Mine(DnsSwitcher.ActiveConnection active) =>
        active.OnAdapters.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();

    /// <summary>
    /// Один такт сторожа. dnsWorks вызывается, только если сеть есть и DNS на месте: это сетевой запрос.
    /// </summary>
    public async Task<Verdict> TickAsync(DnsSwitcher.ActiveConnection active, bool networkUp, IReadOnlyCollection<IPAddress> current,
                                         bool rulesPresent, Func<Task<bool>> dnsWorks)
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
        if (NeedsRestore(active, current, rulesPresent, out _)) return Verdict.Restore;
        if (await dnsWorks()) { Fails = 0; return Verdict.Ok; }
        if (++Fails < FailsBeforeSwitch) return Verdict.Failing;
        Fails = 0;
        return Verdict.SwitchServer;
    }

    /// <summary>
    /// Замена умершему серверу: до take рабочих серверов, кроме текущего и мусора. Порядок: стабильность «все сервисы
    /// сразу» (у старых данных общая), потом избранные, потом пинг. Не проверявшиеся давно идут после свежих
    /// при равной стабильности: их результат мог устареть.
    /// </summary>
    public static List<DnsServer> Replacements(IEnumerable<DnsServer> servers, DnsSwitcher.ActiveConnection active, int take = 3, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        return servers
            .Where(s => s.Id != active.ServerId && !s.IsJunk && s.Status == ServerStatus.Ok)
            .OrderByDescending(s => s.AllStability ?? (s.ChecksTotal == 0 ? 0 : s.ChecksOk * 100 / s.ChecksTotal))
            .ThenByDescending(s => s.IsFavorite)
            .ThenByDescending(s => ServerMetrics.Of(s, t).Fresh)
            .ThenBy(s => s.LatencyMs ?? long.MaxValue)
            .Take(take)
            .ToList();
    }
}
