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

        if (DnsSwitcher.LanguageModeWarning(await DnsSwitcher.LanguageModeAsync()) is { } psWarn)
            items.Add(new DiagItem(DiagLevel.Bad, "PowerShell ограничен политикой", psWarn));

        if (st.SplitDns.Enabled && SplitDns.PolicyWarning(SplitDns.PolicyRuleCount()) is { } gpoWarn)
            items.Add(new DiagItem(DiagLevel.Bad, "«Два DNS» не действуют из-за групповой политики", gpoWarn));

        // Подключение и то, что реально стоит на адаптерах
        var active = DnsSwitcher.GetActive();
        var current = await DnsSwitcher.GetCurrentAddressesAsync();
        if (active == null)
            items.Add(new DiagItem(DiagLevel.Info, "Не подключено", "DNS системы: " + (current.Count == 0 ? "автоматически" : string.Join(", ", current)),
                "connect", "Подключить лучший"));
        else
        {
            var adapters = await DnsSwitcher.GetCurrentAdapterAddressesAsync();
            var onAdapters = !ConnectionMonitor.NeedsRestore(active, current, !active.Split || SplitDns.RulesPresent(), out _, adapters);
            items.Add(new DiagItem(onAdapters ? DiagLevel.Ok : DiagLevel.Bad, $"Подключено: {active.Name} с {active.Since:dd.MM HH:mm}",
                !onAdapters ? $"DNS системы не совпадает с подключением ({string.Join(", ", current)}) или пропали правила для ИИ: их сбросила другая программа или Windows"
                : active.Split ? $"Два DNS: домены ИИ через {active.Name} ({string.Join(", ", active.Addresses)}), остальное через «{active.GeneralName}» ({(current.Count == 0 ? "автоматически" : string.Join(", ", current))})"
                : $"DNS системы: {string.Join(", ", current)}" + (active.DohTemplate != null ? $", DoH {active.DohTemplate}" : ""),
                onAdapters ? null : "reconnect", "Переподключить"));
            if (active.Split && SplitDns.GeneralIsSmart(st.SplitDns, active.Addresses))
                items.Add(new DiagItem(DiagLevel.Warn, "Общий DNS совпадает со Smart DNS", SplitDns.GeneralIsSmartWarning, "split", "Открыть «Два DNS»"));
            items.Add(active.BackupName != null
                ? new DiagItem(DiagLevel.Ok, $"Запасной сервер: {active.BackupName}", $"{string.Join(", ", active.BackupAddresses)}. Если основной перестанет отвечать, Windows перейдет на него сама")
                : new DiagItem(DiagLevel.Info, "Запасного сервера нет", st.UseBackupServer
                    ? "При подключении не нашлось второго рабочего сервера с обычным IPv4-адресом. Проверьте серверы и переподключитесь"
                    : "Выключено в настройках: «Сторож и трей», «Запасной сервер»"));
        }

        // Сервисы: доступ, через что идут и откуда их видит сервис
        var snap = await ConnectionProbe.TakeAsync(st, ct, leakTest: active != null);
        bool noDns = snap.Services.Count > 0 && snap.Services.All(x => x.ResolvedIp is "не резолвится" or "—" or "");
        // Без подключения в Windows может стоять Smart DNS, выставленный вручную (например, Xbox DNS). Если он не дает
        // адреса ИИ, программа об этом должна сказать прямо, а не писать «напрямую не резолвится»
        if (active == null && current.Count > 0 && ManualServer(current) is { } manual)
            items.Add(noDns
                ? new DiagItem(DiagLevel.Bad, $"В Windows вручную стоит {manual}, и он не дает адреса ИИ",
                    "Сервер перестал работать или просит привязать IP на своем сайте. Подключите рабочий сервер кнопкой ниже", "connect", "Подключить лучший")
                : new DiagItem(DiagLevel.Info, $"В Windows вручную стоит {manual}",
                    "CHIMERION им не управляет: сторож и запасной сервер не работают. Лучше подключиться через программу", "connect", "Подключить лучший"));
        foreach (var s in snap.Services)
        {
            var ok = s.Access.StartsWith('✓');
            var where = s.Egress is { } e ? $", видит вас из страны {GeoLocation.CountryName(e.Country)}" : "";
            var closedEgress = s.Egress is { } e2 && EgressWatch.Closed.Contains(e2.Country);
            var route = s.ResolvedIp is "не резолвится" or "—" or "" ? "DNS не дал адрес"
                      : s.Proxied ? $"через Smart DNS {s.ResolvedIp}" : $"напрямую {s.ResolvedIp}";
            var bad = !(ok && !closedEgress);
            items.Add(new DiagItem(bad ? DiagLevel.Bad : DiagLevel.Ok, $"{s.Name}: {s.Access.TrimStart('✓', '✗', ' ')}",
                route + where + (s.Note != null ? ". " + s.Note : ""),
                bad && active != null ? "switch" : null, "Сменить сервер")); // без подключения кнопка одна, в строке «Не подключено»
        }
        // Репутация адресов выхода: только адреса прокси сервера, свой IP пользователя не проверяется
        if (active != null)
            foreach (var exitIp in snap.Services.Where(x => x.Proxied && x.Egress != null).Select(x => x.Egress!.Ip).Distinct())
                if (await IpReputation.CheckAsync(exitIp, st.ReferenceDoh, ct) is { } rep)
                    items.Add(new DiagItem(rep.Bad ? DiagLevel.Warn : DiagLevel.Ok,
                        rep.Bad ? $"Адрес выхода {exitIp} на плохом счету" : $"Адрес выхода {exitIp}: в черных списках нет",
                        IpReputation.Describe(rep) + (rep.Bad ? ". С таких адресов входы блокируют чаще: лучше сменить сервер" : ""),
                        rep.Bad ? "switch" : null, "Сменить сервер"));
        if (AccountSafety.Pinned is { } pin)
            items.Add(new DiagItem(DiagLevel.Info, $"Выбрана страна выхода: {GeoLocation.CountryName(pin)}",
                "Программа подключает и подбирает замену только с выходом в этой стране. Меняется в настройках, во вкладке «Сторож и трей»"));
        // Защита аккаунтов: из каких стран сервисы видели вас за последние 30 дней
        if (st.AccountSafetyGuard)
        {
            var safety = AccountSafety.Load();
            var mism = safety.Compare(snap.Services.Where(x => x.Egress != null).Select(x => new KeyValuePair<string, string?>(x.Name, x.Egress!.Country)));
            var many = snap.Services.Select(x => x.Name).Where(n => safety.CountryCount(n) > AccountSafety.ManyCountries).ToList();
            var homes = snap.Services.Select(x => (x.Name, Home: safety.Home(x.Name))).Where(x => x.Home != null)
                                     .Select(x => $"{x.Name}: {GeoLocation.CountryName(x.Home!)} (стран за 30 дней: {safety.CountryCount(x.Name)})").ToList();
            if (homes.Count == 0)
                items.Add(new DiagItem(DiagLevel.Info, "Защита аккаунтов: истории пока нет", "Программа запоминает, из какой страны сервисы видят вас, и предупредит, если страна начнет меняться"));
            else if (mism.Count > 0 || many.Count > 0)
                items.Add(new DiagItem(DiagLevel.Warn, "Защита аккаунтов: страна входа меняется",
                    (mism.Count > 0 ? $"Сейчас не из привычной страны: {AccountSafety.Describe(mism)}. " : "") +
                    (many.Count > 0 ? $"Много разных стран за 30 дней: {string.Join(", ", many)}. " : "") +
                    "Частая смена страны повышает риск блокировки аккаунта: выбирайте серверы с той же страной выхода. Обычно: " + string.Join("; ", homes),
                    active != null ? "switch" : null, "Сменить сервер"));
            else
                items.Add(new DiagItem(DiagLevel.Ok, "Защита аккаунтов: страна входа постоянная", string.Join("; ", homes)));
        }

        // Общая карта серверов: что работает у пользователей той же сети провайдера
        if (Crowd.LoadStats() is { } crowd && crowd.Provider.Count > 0 && System.IO.File.Exists(Storage.PathOf("servers.json")))
        {
            var servers = Storage.LoadOrCreate("servers.json", () => new List<Models.DnsServer>());
            items.Add(Crowd.BestForProvider(crowd, servers) is { } best
                ? new DiagItem(DiagLevel.Info, $"У вашего провайдера лучше всего работает {best.Server.DisplayName}",
                    $"{best.Rate}% удачных проверок из {best.Total} за сутки по данным общей карты серверов")
                : new DiagItem(DiagLevel.Info, "Общая карта: по вашему провайдеру пока мало данных"));
        }

        // WSL для разработчиков: без DNS-туннеля запросы из Linux идут мимо правил «Двух DNS»
        var wsl = DevTools.Check();
        if (wsl.State != DevTools.WslState.None)
        {
            bool bad = wsl.State != DevTools.WslState.Ok;
            items.Add(new DiagItem(!bad ? DiagLevel.Ok : st.SplitDns.Enabled ? DiagLevel.Warn : DiagLevel.Info,
                bad ? "WSL: запросы идут мимо «Двух DNS»" : "WSL: DNS-туннель включен",
                DevTools.Describe(wsl) + (bad && !st.SplitDns.Enabled ? ". Сейчас «Два DNS» выключены, и это не мешает" : ""),
                bad ? "wsl-tunnel" : null, "Включить DNS-туннель"));
        }

        if (snap.Resolvers.Count > 0)
            items.Add(new DiagItem(DiagLevel.Info, "Резолверы, дошедшие до тестового сервера",
                string.Join("; ", snap.Resolvers.Select(r => $"{r.Org} ({r.Place.Split(',')[^1].Trim()})").Distinct())));

        // Помехи в системе: VPN, безопасный DNS в браузере, прокси, hosts, IPv6
        var issues = SystemCheck.Run(active?.OnAdapters ?? new List<string>());
        if (tools.OfType<ZapretTool>().Any(z => z.IsInstalled) && ZapretTool.Conflict() is { } zc)
            issues.Add(new SystemCheck.Issue("две копии zapret", zc, "tools", "Открыть обход"));
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
            sb.AppendLine("Журнал за 3 дня:");
            foreach (var l in tail) sb.AppendLine(l);
        }
        return Sanitize(sb.ToString(), r.PublicIp);
    }

    /// <summary>Название сервера из списка, если адреса в Windows — его адреса: Smart DNS, выставленный вручную.</summary>
    private static string? ManualServer(IReadOnlyCollection<IPAddress> current)
    {
        try
        {
            if (!System.IO.File.Exists(Storage.PathOf("servers.json"))) return null;
            var servers = Storage.LoadOrCreate("servers.json", () => new List<Models.DnsServer>());
            var cur = current.Select(a => a.ToString()).ToHashSet();
            return servers.FirstOrDefault(s => s.Primary.Length > 0 && cur.Contains(s.Primary))?.DisplayName is { } n ? $"«{n}»" : null;
        }
        catch (Exception) { return null; }
    }

    private static readonly Regex Ipv4 = new(@"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"(?<![\w:])[0-9a-fA-F:.%]+(?::[0-9a-fA-F:.%]*)+(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);
    private static readonly Regex SecretHeader = new(@"(?im)\b(?:authorization|proxy-authorization|cookie|set-cookie)\s*:\s*[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex SecretValue = new(@"(?i)([""']?(?:password|passwd|pwd|пароль|token|access_token|refresh_token|api[_-]?key|secret|cookie|authorization)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s&,;}]*)", RegexOptions.Compiled);
    private static readonly Regex Location = new(@"(?im)(?:город|city|region|регион|область|провайдер|provider)\s*[:=]\s*[^,;\r\n]+", RegexOptions.Compiled);

    /// <summary>Убирает адреса, учетные данные и известные личные данные из журнала и отчета.</summary>
    public static string Sanitize(string text, string? publicIp, string? userName = null)
    {
        if (!string.IsNullOrEmpty(publicIp))
        {
            text = text.Replace(publicIp, "[ваш IP]", StringComparison.OrdinalIgnoreCase);
            text = Regex.Replace(text, @"\[ваш IP\] \([^()\n]*\)", "[ваш IP] ([место скрыто])");
            text = Regex.Replace(text, @"(?<=: )[^:()\n]+ \(\[ваш IP\][^()\n]*\)", "[место скрыто] ([ваш IP])");
        }
        text = SecretHeader.Replace(text, "[учетные данные скрыты]");
        text = SecretValue.Replace(text, "$1[скрыто]");
        text = Location.Replace(text, "[место скрыто]");
        // Сначала IPv6: адрес может содержать IPv4 в конце.
        text = Ipv6.Replace(text, m => IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[IP]" : m.Value);
        text = Ipv4.Replace(text, m =>
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            bool local = a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168) || (a == 169 && b == 254)
                         || (a == 100 && b is >= 64 and <= 127);
            return local ? "[локальный IP]" : "[IP]";
        });
        text = Regex.Replace(text, @"(?<=: )[^:()\n]+ \((?:\[IP\]|\[локальный IP\])[^()\n]*\)", "[место скрыто] ([IP])");
        text = Regex.Replace(text, @"(?:\[IP\]|\[локальный IP\]) \([^()\n]*\)", "[IP] ([место скрыто])");
        var user = userName ?? Environment.UserName;
        if (user.Length >= 1) text = Regex.Replace(text, $@"(?<=\\Users\\){Regex.Escape(user)}(?=\\|$)", "[пользователь]", RegexOptions.IgnoreCase);
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
