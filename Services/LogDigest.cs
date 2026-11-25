using System.Text.RegularExpressions;

namespace Dnex.Services;

/// <summary>
/// Сводка журнала для разбора проблем: сеансы программы, сбои по видам с причиной, смена доступа к сервисам,
/// повторяющиеся предупреждения и итог проверки списка серверов. Ставится в начало отчета и копии журнала:
/// за 3 дня в журнале десятки тысяч строк, и без сводки проблему приходилось искать вручную.
/// Работает только с уже очищенными строками журнала и ничего нового о человеке не добавляет.
/// </summary>
public static class LogDigest
{
    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly Regex Prefix = new(@"^\[Подробно(?:; сеанс=(?<s>\w+); запись=\d+(?:; операция=\d+)?)?\] ", Opt);
    private static readonly Regex ServerList = new(@"^(?:Начало: |Итог: )?(?:DNS через сервер;|HTTPS через сервер;|HTTPS через проверяемый сервер|Проверка сервера;)", Opt);
    private static readonly Regex Failed = new(@"^Итог: (?<name>.+?); (?<how>ошибка|истекло время ожидания|отменено или истекло время ожидания|отменено)(?:;|$)", Opt);
    private static readonly Regex Cause = new(@"^(?<name>.+?): (?:[^:;]{1,40}: )?(?<exc>\w+Exception)(?<rest>.*)$", Opt);
    private static readonly Regex Codes = new(@"; (?:socket=\w+|HTTP=\d+|сеть=(?!None)\w+|тайм-аут)", Opt);
    private static readonly Regex Access = new(@"^Сервис (?<svc>[^;]+); сайт=(?<web>True|False); облегченная=\w+: Доступ=(?<acc>[✓✗][^;]*)", Opt);
    private static readonly Regex Version = new(@"^Начало сеанса; версия=(?<v>[^;]+)", Opt);
    private static readonly Regex End = new(@"^Конец сеанса; код выхода=(?<c>-?\d+)", Opt);
    private static readonly Regex ServerId = new(@"номер=(?<id>[0-9a-f]{8,})", Opt);
    private static readonly Regex Numbers = new(@"\d+", Opt);
    private static readonly Regex HexId = new(@"\b[0-9a-f]{12,}\b", Opt);
    private static readonly Regex Problem = new(@"не удалось|не получилось|ошибк|не работает|не отвечает|не открывается|закрыт|сбой|упал|не найден(?!о)|недоступ|^\s*\[(?:x|!)\]|другую копию|переполнена|неудачн|не совпадает|мимо",
        Opt | RegexOptions.IgnoreCase);

    /// <summary>Строка журнала без служебной метки «[Подробно; сеанс=…]» и номер сеанса, если он есть.</summary>
    internal static (string? Session, string Text) Split(string raw)
    {
        var m = Prefix.Match(raw);
        if (!m.Success) return (null, raw);
        return (m.Groups["s"].Success ? m.Groups["s"].Value : null, raw[m.Length..]);
    }

    /// <summary>Подробности проверки списка серверов: тысячи строк о чужих серверах, в сводке они сведены в итог.</summary>
    public static bool IsServerListNoise(string raw) => ServerList.IsMatch(Split(raw).Text);

    /// <summary>Похожие строки в одну группу: числа и номера серверов заменяются, текст остается.</summary>
    internal static string Shape(string text) => Numbers.Replace(HexId.Replace(text, "#"), "N");

    /// <summary>Сводка по записям журнала (время и очищенный текст), строками для отчета.</summary>
    public static List<string> Build(IReadOnlyList<(DateTime Time, string Text)> entries, int maxSessions = 12)
    {
        var result = new List<string> { "=== Сводка журнала (сначала читать ее) ===" };
        if (entries.Count == 0) { result.Add("Записей нет"); return result; }

        var sessions = new List<SessionInfo>();
        var byId = new Dictionary<string, SessionInfo>();
        var failures = new Dictionary<string, Group>();
        var causes = new Dictionary<string, Dictionary<string, int>>();
        var events = new Dictionary<string, Group>();
        var access = new Dictionary<string, string>();
        // В каком сеансе состояние записано последним: имена проверок менялись между версиями (0.1.167 писала
        // «Claude (сайт)», 0.1.180 пишет «Claude (claude.ai) (сайт)»), и итог старого сеанса не должен выдаваться за текущий
        var accessSession = new Dictionary<string, string?>();
        string? lastAccessSession = null;
        var changes = new List<string>();
        int serverLines = 0, serverFailed = 0;
        var serverIds = new HashSet<string>();
        var serverCauses = new Dictionary<string, int>();

        foreach (var (time, raw) in entries)
        {
            var (session, text) = Split(raw);
            if (session != null)
            {
                if (!byId.TryGetValue(session, out var s))
                {
                    s = new SessionInfo(session, time);
                    byId[session] = s;
                    sessions.Add(s);
                }
                s.Last = time;
                s.Lines++;
                if (Version.Match(text) is { Success: true } v) s.Version = v.Groups["v"].Value;
                if (End.Match(text) is { Success: true } e) s.ExitCode = e.Groups["c"].Value;
            }

            if (ServerList.IsMatch(text))
            {
                serverLines++;
                if (Failed.IsMatch(text))
                {
                    serverFailed++;
                    if (ServerId.Match(text) is { Success: true } id) serverIds.Add(id.Groups["id"].Value);
                }
                else if (Cause.Match(text) is { Success: true } c)
                {
                    var kind = (text.StartsWith("DNS", StringComparison.Ordinal) ? "DNS: " : "HTTPS: ") + CauseOf(c);
                    serverCauses[kind] = serverCauses.GetValueOrDefault(kind) + 1;
                }
                continue;
            }

            // Причина пишется строкой раньше итога операции: копим ее по имени операции отдельно
            if (Failed.Match(text) is { Success: true } f)
                Add(failures, Shape(f.Groups["name"].Value), time, f.Groups["how"].Value);
            else if (Cause.Match(text) is { Success: true } c)
            {
                var key = Shape(c.Groups["name"].Value);
                if (!causes.TryGetValue(key, out var byCause)) causes[key] = byCause = new Dictionary<string, int>();
                byCause[CauseOf(c)] = byCause.GetValueOrDefault(CauseOf(c)) + 1;
            }

            if (Access.Match(text) is { Success: true } a)
            {
                var key = $"{a.Groups["svc"].Value} ({(a.Groups["web"].Value == "True" ? "сайт" : "API")})";
                var now = a.Groups["acc"].Value.Trim();
                if (access.TryGetValue(key, out var was) && was != now)
                    changes.Add($"{time:dd.MM HH:mm:ss} {key}: {was} → {now}");
                access[key] = now;
                accessSession[key] = lastAccessSession = session;
            }

            var ev = text.StartsWith("Событие: ", StringComparison.Ordinal) ? text["Событие: ".Length..]
                   : session == null && !text.StartsWith('[') ? text : null;
            if (ev != null && Problem.IsMatch(ev)) Add(events, Shape(ev.Trim()), time, ev.Trim());
            else if (text.Contains("Очередь переполнена", StringComparison.Ordinal)) Add(events, "Очередь журнала переполнена", time, text);
        }

        result.Add($"Период: {entries[0].Time:dd.MM HH:mm} - {entries[^1].Time:dd.MM HH:mm}, записей {entries.Count}, из них проверка списка серверов {serverLines}");

        result.Add("");
        result.Add($"Запуски программы ({sessions.Count}{(sessions.Count > maxSessions ? $", последние {maxSessions}" : "")}):");
        for (int k = Math.Max(0, sessions.Count - maxSessions); k < sessions.Count; k++)
        {
            var s = sessions[k];
            var end = s.ExitCode != null ? $"закрыта, код {s.ExitCode}"
                    : k == sessions.Count - 1 ? "работает"
                    : "оборвалась без записи о закрытии (сбой, выключение компьютера или снятие процесса)";
            result.Add($"  {s.Start:dd.MM HH:mm:ss} - {s.Last:dd.MM HH:mm:ss} сеанс {s.Id}, версия {s.Version ?? "?"}, строк {s.Lines}: {end}");
        }
        if (sessions.Count == 0) result.Add("  нет записей о запуске");

        result.Add("");
        result.Add("Сбои операций (без проверки списка серверов):");
        if (failures.Count == 0) result.Add("  нет");
        foreach (var (name, g) in failures.OrderByDescending(x => x.Value.Count).Take(20))
        {
            var why = causes.TryGetValue(name, out var c) && c.Count > 0
                ? "; причина: " + string.Join(", ", c.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key} ×{x.Value}")) : "";
            result.Add($"  {g.Count}× {name}: {g.Example}; {Span(g)}{why}");
        }

        result.Add("");
        result.Add("Доступ к сервисам: изменения" + (changes.Count > 30 ? $" (последние 30 из {changes.Count})" : "") + ":");
        if (changes.Count == 0) result.Add("  не менялся");
        result.AddRange(changes.Skip(Math.Max(0, changes.Count - 30)).Select(c => "  " + c));
        var current = access.Where(x => accessSession[x.Key] == lastAccessSession).ToList();
        if (current.Count > 0) result.Add("  последнее: " + string.Join("; ", current.Select(x => $"{x.Key} {x.Value}")));

        result.Add("");
        result.Add("Предупреждения и ошибки в событиях:");
        if (events.Count == 0) result.Add("  нет");
        foreach (var g in events.Values.OrderByDescending(x => x.Last).Take(25))
            result.Add($"  {g.Count}× {Clip(g.Example, 240)}; {Span(g)}");

        result.Add("");
        result.Add($"Проверка списка серверов: неудачных проверок {serverFailed}, разных серверов {serverIds.Count}. " +
                   "Это сбои чужих серверов из списка, сами по себе не проблема программы");
        foreach (var (kind, n) in serverCauses.OrderByDescending(x => x.Value).Take(5))
            result.Add($"  {kind} ×{n}");
        return result;
    }

    private static string CauseOf(Match c)
    {
        var codes = string.Concat(Codes.Matches(c.Groups["rest"].Value).Select(m => m.Value));
        return c.Groups["exc"].Value + codes;
    }

    private static void Add(Dictionary<string, Group> map, string key, DateTime time, string example)
    {
        if (!map.TryGetValue(key, out var g)) map[key] = g = new Group { First = time };
        g.Count++;
        g.Last = time;
        g.Example = example;
    }

    private static string Span(Group g) => g.Count == 1 || g.First == g.Last ? $"{g.Last:dd.MM HH:mm:ss}" : $"{g.First:dd.MM HH:mm} … {g.Last:dd.MM HH:mm}";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class Group
    {
        public int Count;
        public DateTime First, Last;
        public string Example = "";
    }

    private sealed class SessionInfo(string id, DateTime start)
    {
        public string Id { get; } = id;
        public DateTime Start { get; } = start;
        public DateTime Last { get; set; } = start;
        public int Lines { get; set; }
        public string? Version { get; set; }
        public string? ExitCode { get; set; }
    }
}
