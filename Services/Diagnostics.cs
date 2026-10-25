using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dnex.Models;
using Dnex.Services.Tools;

namespace Dnex.Services;

public enum DiagLevel { Ok, Info, Warn, Bad }

/// <summary>Строка отчета: итог одной проверки и пояснение.</summary>
/// <summary>
/// Пункт диагностики. Action — что CHIMERION может исправить сама по кнопке (ActionText): «connect», «reconnect», «switch»,
/// «update», «tools», «tool:id» (запустить инструмент), «hosts», «open:адрес» (открыть настройки Windows).
/// </summary>
public sealed record DiagItem(DiagLevel Level, string Title, string Detail = "", string? Action = null, string? ActionText = null);

public sealed record DiagReport(IReadOnlyList<DiagItem> Items, string? PublicIp, DateTime Taken);

/// <summary>
/// Диагностика одним отчетом: версия и права, подключение, доступ к сервисам и страна выхода, помехи в системе,
/// Telegram-прокси и zapret, обновления. Отчет можно скопировать или отправить на GitHub: перед этим из него
/// убираются внешний IP пользователя, локальные адреса, имя пользователя Windows и почта.
/// </summary>
public static class Diagnostics
{
    public const string IssuesUrl = "https://github.com/under2ker/chimerion/issues/new";

    public static async Task<DiagReport> RunAsync(AppSettings st, IReadOnlyList<Tool> tools, CancellationToken ct = default)
    {
        var items = new List<DiagItem>();
        var admin = DnsSwitcher.IsAdmin();
        items.Add(new DiagItem(DiagLevel.Info, $"CHIMERION {UpdateChecker.CurrentVersion}",
            $"Windows {Environment.OSVersion.Version}, права администратора: {(admin ? "да" : "нет")}, автозапуск: {(AutoStart.IsEnabled() ? "включен" : "выключен")}"));

        // Подключение и то, что реально стоит на адаптерах
        var active = DnsSwitcher.GetActive();
        var current = await DnsSwitcher.GetCurrentAddressesAsync();
        if (active == null)
            items.Add(new DiagItem(DiagLevel.Info, "Не подключено", "DNS системы: " + (current.Count == 0 ? "автоматически" : string.Join(", ", current)),
                "connect", "Подключить лучший"));
        else
        {
            var mine = active.OnAdapters.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).OfType<IPAddress>().ToList();
            var onAdapters = (mine.Count == 0 || mine.Any(current.Contains)) && (!active.Split || SplitDns.RulesPresent());
            items.Add(new DiagItem(onAdapters ? DiagLevel.Ok : DiagLevel.Bad, $"Подключено: {active.Name} с {active.Since:dd.MM HH:mm}",
                !onAdapters ? $"DNS системы не совпадает с подключением ({string.Join(", ", current)}) или пропали правила для ИИ: их сбросила другая программа или Windows"
                : active.Split ? $"Два DNS: домены ИИ через {active.Name} ({string.Join(", ", active.Addresses)}), остальное через «{active.GeneralName}» ({(current.Count == 0 ? "автоматически" : string.Join(", ", current))})"
                : $"DNS системы: {string.Join(", ", current)}" + (active.DohTemplate != null ? $", DoH {active.DohTemplate}" : ""),
                onAdapters ? null : "reconnect", "Переподключить"));
        }

        // Сервисы: доступ, через что идут и откуда их видит сервис
        var snap = await ConnectionProbe.TakeAsync(st, ct, leakTest: active != null);
        foreach (var s in snap.Services)
        {
            var ok = s.Access.StartsWith('✓');
            var where = s.Egress is { } e ? $", видит вас из страны {GeoLocation.CountryName(e.Country)}" : "";
            var closedEgress = s.Egress is { } e2 && EgressWatch.Closed.Contains(e2.Country);
            var route = s.Proxied ? $"через Smart DNS {s.ResolvedIp}" : $"напрямую {s.ResolvedIp}";
            var bad = !(ok && !closedEgress);
            items.Add(new DiagItem(bad ? DiagLevel.Bad : DiagLevel.Ok, $"{s.Name}: {s.Access.TrimStart('✓', '✗', ' ')}",
                route + where + (s.Note != null ? ". " + s.Note : ""),
                bad && active != null ? "switch" : null, "Сменить сервер")); // без подключения кнопка одна, в строке «Не подключено»
        }
        if (snap.Resolvers.Count > 0)
            items.Add(new DiagItem(DiagLevel.Info, "Резолверы, дошедшие до тестового сервера",
                string.Join("; ", snap.Resolvers.Select(r => $"{r.Org} ({r.Place})").Distinct())));

        // Помехи в системе: VPN, безопасный DNS в браузере, прокси, hosts, IPv6
        var issues = SystemCheck.Run(active?.OnAdapters ?? new List<string>());
        if (issues.Count == 0) items.Add(new DiagItem(DiagLevel.Ok, "Помех в системе не найдено"));
        foreach (var i in issues) items.Add(new DiagItem(DiagLevel.Warn, "Помеха: " + i.Title, i.Fix, i.Action, i.ActionText));

        // Сторонние программы
        foreach (var t in tools)
        {
            if (!t.IsInstalled) { items.Add(new DiagItem(DiagLevel.Info, $"{t.Title}: не установлено")); continue; }
            string upd;
            try { await t.CheckAsync(ct); upd = t.UpdateAvailable ? $", доступна {t.Latest!.Version}" : ", последняя версия"; }
            catch (ToolException e) { upd = ", обновления не проверены: " + e.Message; }
            var running = t.IsRunning();
            var strategy = t is ZapretTool ? $", стратегия {ZapretTool.StrategyTitle(t.State.Strategy ?? ZapretTool.DefaultStrategy)}{(t.State.Games ? ", с играми" : "")}" : "";
            var level = t.State.Enabled && !running ? DiagLevel.Warn : DiagLevel.Info;
            var (act, actText) = t.State.Enabled && !running ? ($"tool:{t.Id}", "Запустить")
                               : t.UpdateAvailable ? ("tools", "Обновить") : ((string?)null, (string?)null);
            items.Add(new DiagItem(level, $"{t.Title}: {(running ? "работает" : t.State.Enabled ? "включено, но не работает" : "выключено")}",
                $"версия {t.InstalledVersion}{upd}{strategy}", act, actText));
        }

        // Обновление CHIMERION
        var u = await UpdateChecker.CheckDetailedAsync(ct);
        items.Add(u.Status switch
        {
            UpdateStatus.Available => new DiagItem(DiagLevel.Warn, $"Доступна CHIMERION {u.Latest}", "Обновление ставится в программе и заработает после перезапуска", "update", "Обновить"),
            UpdateStatus.UpToDate => new DiagItem(DiagLevel.Ok, "Установлена последняя версия CHIMERION"),
            _ => new DiagItem(DiagLevel.Info, "Новую версию проверить не удалось", u.Reason ?? ""),
        });

        return new DiagReport(items, snap.PublicIp is { Length: > 0 } ip && ip != "—" ? ip : null, DateTime.Now);
    }

    /// <summary>Отчет текстом для копирования и для GitHub: строки отчета и хвост журнала, без личных данных.</summary>
    public static string ToText(DiagReport r, IEnumerable<string> logTail)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Отчет CHIMERION от {r.Taken:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        foreach (var i in r.Items)
        {
            var mark = i.Level switch { DiagLevel.Ok => "[ок]", DiagLevel.Warn => "[!]", DiagLevel.Bad => "[x]", _ => "[i]" };
            sb.AppendLine($"{mark} {i.Title}");
            if (i.Detail.Length > 0) sb.AppendLine("    " + i.Detail);
        }
        var tail = logTail.ToList();
        if (tail.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Журнал (последние записи):");
            foreach (var l in tail) sb.AppendLine(l);
        }
        return Sanitize(sb.ToString(), r.PublicIp);
    }

    private static readonly Regex Ipv4 = new(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);

    /// <summary>
    /// Убирает личное: внешний IP пользователя, адреса локальной сети, имя пользователя Windows (в путях) и почту.
    /// Адреса DNS-серверов и прокси остаются: они публичные и нужны, чтобы понять проблему.
    /// </summary>
    public static string Sanitize(string text, string? publicIp, string? userName = null)
    {
        if (!string.IsNullOrEmpty(publicIp)) text = text.Replace(publicIp, "[ваш IP]");
        text = Ipv4.Replace(text, m =>
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            bool local = a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) || (a == 169 && b == 254)
                         || (a == 100 && b is >= 64 and <= 127);
            return local ? "[локальный IP]" : m.Value;
        });
        var user = userName ?? Environment.UserName;
        if (user.Length >= 2) text = Regex.Replace(text, $@"(?<=\\Users\\){Regex.Escape(user)}(?=\\|$)", "[пользователь]", RegexOptions.IgnoreCase);
        return Email.Replace(text, "[почта]");
    }

    /// <summary>
    /// Ссылка на новый issue с заполненным текстом. Длина ссылки ограничена (браузеры и GitHub режут длинные),
    /// поэтому тело обрезается; полный отчет пользователь вставляет из буфера обмена.
    /// </summary>
    public static string IssueUrl(string title, string body, int maxLength = 7000)
    {
        const string cut = "\n\n(отчет обрезан: полный текст скопирован в буфер обмена, вставьте его сюда)";
        string Url(string b) => $"{IssuesUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(b)}";
        var url = Url(body);
        if (url.Length <= maxLength) return url;
        var lo = 0; var hi = body.Length;
        while (lo < hi) // самая длинная обрезка тела, которая влезает в лимит
        {
            var mid = (lo + hi + 1) / 2;
            if (Url(body[..mid] + cut).Length <= maxLength) lo = mid; else hi = mid - 1;
        }
        return Url(body[..lo] + cut);
    }
}
