using Dnex.Services;

namespace Dnex.Models;

/// <summary>
/// Состояние сервера до пакетной проверки. Если проверку испортил сбой сети или VPN, ее результат не засчитывается:
/// счетчики, история и последний достоверный результат (статус, пинг, итог, выход сервисов) возвращаются, чтобы вкладка
/// «Рабочие» не опустела из-за сети, а не серверов. История хранится точной копией, а не числом точек: при полной
/// истории новая точка вытесняет самую старую, и «обрезать до N» оставило бы ошибочную точку.
/// </summary>
internal sealed record CheckSnapshot(
    ServerStatus Status, int Total, int Ok, int FailStreak, int ServiceChecks, int AllOk, Dictionary<string, int> ServiceOk,
    List<CheckPoint> History, long? LatencyMs, long? JitterMs, string Summary, string? LastError, DateTime? LastChecked,
    bool NoProxy, Dictionary<string, Egress> EgressBy)
{
    public static CheckSnapshot Take(DnsServer s) => new(
        s.Status, s.ChecksTotal, s.ChecksOk, s.FailStreak, s.ServiceChecks, s.AllOk, new(s.ServiceOk), s.History,
        s.LatencyMs, s.JitterMs, s.Summary, s.LastError, s.LastChecked, s.NoProxy, new(s.EgressBy));

    public void Restore(DnsServer s)
    {
        s.ChecksTotal = Total; s.ChecksOk = Ok; s.FailStreak = FailStreak;
        s.ServiceChecks = ServiceChecks; s.AllOk = AllOk; s.ServiceOk = new(ServiceOk);
        s.History = new List<CheckPoint>(History);
        s.Status = Status; s.LatencyMs = LatencyMs; s.JitterMs = JitterMs; s.Summary = Summary;
        s.LastError = LastError; s.LastChecked = LastChecked; s.NoProxy = NoProxy;
        s.EgressBy.Clear();
        foreach (var kv in EgressBy) s.EgressBy[kv.Key] = kv.Value;
        s.NotifyEgress();
        s.NotifyStability();
    }
}
