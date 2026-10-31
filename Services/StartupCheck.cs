using Dnex.Services.Tools;

namespace Dnex.Services;

/// <summary>Что осталось от прошлого запуска.</summary>
public enum StartupIssue { Ipv6LeftOff, RulesLeft, StaleBackup, ForeignZapret }

/// <summary>Снимок состояния: подключение, файлы незавершенных изменений, правила NRPT, zapret.</summary>
public sealed record StartupState(bool Connected, int Ipv6Off, bool BackupExists, bool RulesPresent, bool ZapretRunning, bool ZapretEnabled);

/// <summary>
/// Проверка после запуска: остались ли следы оборванного изменения сети. Запись о подключении есть: DNS сверяет и возвращает
/// EnsureConnectionAfterStartAsync. Записи нет, а IPv6 выключен или стоят правила «Двух DNS»: нужен откат, он требует прав
/// администратора, поэтому только по нажатию. Резервная копия без подключения устарела: новая снимется при подключении.
/// </summary>
public static class StartupCheck
{
    public static List<StartupIssue> Inspect(StartupState s)
    {
        var issues = new List<StartupIssue>();
        if (!s.Connected)
        {
            if (s.Ipv6Off > 0) issues.Add(StartupIssue.Ipv6LeftOff);
            if (s.RulesPresent) issues.Add(StartupIssue.RulesLeft);
            if (s.BackupExists && issues.Count == 0) issues.Add(StartupIssue.StaleBackup);
        }
        if (s.ZapretRunning && !s.ZapretEnabled) issues.Add(StartupIssue.ForeignZapret);
        return issues;
    }

    /// <summary>Остатки, которые убирает откат настроек сети.</summary>
    public static bool NeedsRestore(StartupIssue i) => i is StartupIssue.Ipv6LeftOff or StartupIssue.RulesLeft;

    public static string Describe(StartupIssue i) => i switch
    {
        StartupIssue.Ipv6LeftOff => "IPv6 остался выключенным после оборванного подключения",
        StartupIssue.RulesLeft => "Правила «Двух DNS» остались без подключения",
        StartupIssue.StaleBackup => "Устаревшая копия настроек DNS",
        _ => "zapret запущен, хотя в программе он выключен: это другая копия или прошлый запуск",
    };

    public static StartupState Current(IEnumerable<Tool> tools)
    {
        var zapret = tools.OfType<ZapretTool>().FirstOrDefault();
        return new StartupState(DnsSwitcher.GetActive() != null, DnsSwitcher.Ipv6LeftOff, DnsSwitcher.BackupExists, SplitDns.RulesPresent(),
            zapret?.IsRunning() == true, zapret?.State.Enabled == true);
    }
}
