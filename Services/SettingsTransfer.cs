using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>Сервер из файла переноса: только то, что нужно, чтобы добавить его в список на другом компьютере.</summary>
public sealed class PortableServer
{
    public string Name { get; set; } = "";
    public string? Primary { get; set; }
    public string? Secondary { get; set; }
    public string? DohUrl { get; set; }
}

/// <summary>
/// Настройки, которые можно переносить между компьютерами. Список закрытый: берутся только безопасные и понятные поля.
/// Cookie, источники и цели проверок, списки исключений, согласие на общую карту, пропущенная версия и «Два DNS»
/// в файл не попадают и из файла не читаются: это либо секреты, либо то, что зависит от сети конкретного компьютера,
/// либо решение, которое пользователь должен принять заново.
/// </summary>
public sealed class PortableSettings
{
    public string? Language { get; set; }
    public string? Theme { get; set; }
    public Dictionary<string, string>? Colors { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? TrayCheckMinutes { get; set; }
    public int? PingRefreshMinutes { get; set; }
    public int? WatchdogSeconds { get; set; }
    public int? Parallelism { get; set; }
    public bool? CheckUpdates { get; set; }
    public bool? PrivacyMode { get; set; }
    public bool? PrivacyHideWindows { get; set; }
    public bool? StartCompact { get; set; }
    public bool? AutoSwitchOnServiceDown { get; set; }
    public bool? AllowServerSwitch { get; set; }
    public bool? UseBackupServer { get; set; }
    public bool? AccountSafetyGuard { get; set; }
    public string? EgressCountry { get; set; }
    public bool? SiteWatch { get; set; }
    public bool? CloseToTray { get; set; }
}

public sealed class TransferFile
{
    // Без значений по умолчанию: пустой или чужой JSON не должен сойти за файл CHIMERION. Export заполняет оба поля явно
    public int Format { get; set; }
    public string App { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime Exported { get; set; }
    public PortableSettings Settings { get; set; } = new();
    public List<PortableServer> Favorites { get; set; } = new();
}

/// <summary>Разобранный и очищенный файл переноса, готовый к применению. Skipped: что пришлось отбросить и почему.</summary>
public sealed record ImportPlan(PortableSettings Settings, List<PortableServer> Favorites, List<string> Skipped)
{
    public int SettingsCount => typeof(PortableSettings).GetProperties().Count(p => p.GetValue(Settings) != null);
}

/// <summary>Экспорт и импорт настроек и избранных серверов в один JSON-файл, чтобы перенести их на другой компьютер.</summary>
public static class SettingsTransfer
{
    public const int Format = 1;
    public const long MaxBytes = 1_000_000;
    public const int MaxFavorites = 300;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static string Export(AppSettings s, IEnumerable<DnsServer> servers)
    {
        var file = new TransferFile
        {
            Format = Format, App = "CHIMERION",
            Version = UpdateChecker.CurrentVersion.ToString(3),
            Exported = DateTime.Now,
            Settings = new PortableSettings
            {
                Language = s.Language, Theme = s.Theme, Colors = s.Colors.Count > 0 ? new Dictionary<string, string>(s.Colors) : null,
                TimeoutSeconds = s.TimeoutSeconds, TrayCheckMinutes = s.TrayCheckMinutes, PingRefreshMinutes = s.PingRefreshMinutes,
                WatchdogSeconds = s.WatchdogSeconds, Parallelism = s.Parallelism, CheckUpdates = s.CheckUpdates,
                PrivacyMode = s.PrivacyMode, PrivacyHideWindows = s.PrivacyHideWindows, StartCompact = s.StartCompact,
                AutoSwitchOnServiceDown = s.AutoSwitchOnServiceDown, AllowServerSwitch = s.AllowServerSwitch, UseBackupServer = s.UseBackupServer,
                AccountSafetyGuard = s.AccountSafetyGuard, EgressCountry = s.EgressCountry, SiteWatch = s.SiteWatch, CloseToTray = s.CloseToTray,
            },
            Favorites = servers.Where(x => x.IsFavorite).Take(MaxFavorites)
                .Select(x => new PortableServer { Name = x.Name, Primary = x.Primary, Secondary = x.Secondary, DohUrl = x.DohUrl }).ToList(),
        };
        return JsonSerializer.Serialize(file, Json);
    }

    /// <summary>
    /// Разбор файла. Чужой или испорченный файл не должен ничего ломать: размер ограничен, числа приводятся к допустимым
    /// пределам, адреса серверов проверяются, лишние поля игнорируются. Error: что не так, если файл не подошел.
    /// </summary>
    public static (ImportPlan? Plan, string? Error) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return (null, "Файл пустой или слишком большой для файла настроек CHIMERION");
        TransferFile? f;
        try { f = JsonSerializer.Deserialize<TransferFile>(json, Json); }
        catch (JsonException) { return (null, "Это не файл настроек CHIMERION: не удалось прочитать содержимое"); }
        if (f == null || f.App != "CHIMERION") return (null, "Это не файл настроек CHIMERION");
        if (f.Format > Format) return (null, $"Файл сделан более новой версией программы (формат {f.Format}). Обновите CHIMERION и повторите");
        if (f.Format < 1) return (null, "Неизвестный формат файла");

        var skipped = new List<string>();
        var p = f.Settings ?? new PortableSettings();
        var clean = new PortableSettings
        {
            Language = p.Language is "ru" or "en" ? p.Language : null,
            Theme = p.Theme is "dark" or "light" ? p.Theme : null,
            Colors = CleanColors(p.Colors, skipped),
            TimeoutSeconds = Clamp(p.TimeoutSeconds, 2, 30), TrayCheckMinutes = Clamp(p.TrayCheckMinutes, 1, 60),
            PingRefreshMinutes = Clamp(p.PingRefreshMinutes, 1, 60), WatchdogSeconds = Clamp(p.WatchdogSeconds, 15, 600),
            Parallelism = Clamp(p.Parallelism, 1, 64),
            CheckUpdates = p.CheckUpdates, PrivacyMode = p.PrivacyMode, PrivacyHideWindows = p.PrivacyHideWindows, StartCompact = p.StartCompact,
            AutoSwitchOnServiceDown = p.AutoSwitchOnServiceDown, AllowServerSwitch = p.AllowServerSwitch, UseBackupServer = p.UseBackupServer,
            AccountSafetyGuard = p.AccountSafetyGuard,
            EgressCountry = p.EgressCountry is { Length: 2 } c && c.All(char.IsAsciiLetter) ? c.ToUpperInvariant() : null,
            SiteWatch = p.SiteWatch, CloseToTray = p.CloseToTray,
        };

        var favorites = new List<PortableServer>();
        foreach (var s in (f.Favorites ?? new()).Take(MaxFavorites))
        {
            var one = CleanServer(s);
            if (one == null) { skipped.Add($"сервер «{Short(s?.Name)}»: адрес не подошел"); continue; }
            if (!favorites.Any(x => Same(x, one))) favorites.Add(one);
        }
        if ((f.Favorites?.Count ?? 0) > MaxFavorites) skipped.Add($"избранных больше {MaxFavorites}: лишние пропущены");
        return (new ImportPlan(clean, favorites, skipped), null);
    }

    private static int? Clamp(int? v, int lo, int hi) => v is { } x ? Math.Clamp(x, lo, hi) : null;
    private static string Short(string? s) => string.IsNullOrWhiteSpace(s) ? "без имени" : (s.Length > 30 ? s[..30] + "…" : s);

    private static readonly Regex ColorKey = new(@"^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex ColorValue = new(@"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    private static Dictionary<string, string>? CleanColors(Dictionary<string, string>? colors, List<string> skipped)
    {
        if (colors == null || colors.Count == 0) return null;
        var ok = new Dictionary<string, string>();
        foreach (var (k, v) in colors.Take(60))
            if (ColorKey.IsMatch(k) && v != null && ColorValue.IsMatch(v)) ok[k] = v;
        if (ok.Count < colors.Count) skipped.Add($"цвета: пропущено {colors.Count - ok.Count} с неверным значением");
        return ok.Count > 0 ? ok : null;
    }

    /// <summary>Сервер: имя без управляющих символов и адреса, которые похожи на публичные DNS (не локальная сеть) или https-адрес DoH.</summary>
    internal static PortableServer? CleanServer(PortableServer? s)
    {
        if (s == null) return null;
        string? Ip(string? v) => IPAddress.TryParse(v?.Trim(), out var ip) && GeoLocation.IsPublicIp(ip.ToString()) ? ip.ToString() : null;
        string? Doh(string? v)
        {
            if (string.IsNullOrWhiteSpace(v) || v.Length > 300) return null;
            if (!Uri.TryCreate(v.Split('{')[0], UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
            // DoH по IP: адрес обязан быть публичным. Имя вида host.tld подходит, localhost и имена без точки отбрасываются
            if (IPAddress.TryParse(u.Host, out var hostIp)) return GeoLocation.IsPublicIp(hostIp.ToString()) ? v : null;
            return u.Host.Contains('.') ? v : null;
        }
        var primary = Ip(s.Primary); var secondary = Ip(s.Secondary); var doh = Doh(s.DohUrl);
        if (primary == null && doh == null) return null;
        var name = new string((s.Name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = primary ?? new Uri(doh!.Split('{')[0]).Host;
        if (name.Length > 100) name = name[..100];
        return new PortableServer { Name = name, Primary = primary, Secondary = secondary, DohUrl = doh };
    }

    private static bool Same(PortableServer a, PortableServer b) =>
        (a.Primary ?? a.DohUrl ?? a.Name).Equals(b.Primary ?? b.DohUrl ?? b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Переносит безопасные поля в настройки. Возвращает, сколько значений применено.</summary>
    public static int Apply(PortableSettings p, AppSettings t)
    {
        int n = 0;
        void Set<T>(T? v, Action<T> set) where T : struct { if (v.HasValue) { set(v.Value); n++; } }
        if (p.Language != null) { t.Language = p.Language; n++; }
        if (p.Theme != null) { t.Theme = p.Theme; n++; }
        if (p.Colors != null) { t.Colors = new Dictionary<string, string>(p.Colors); n++; }
        Set(p.TimeoutSeconds, v => t.TimeoutSeconds = v); Set(p.TrayCheckMinutes, v => t.TrayCheckMinutes = v);
        Set(p.PingRefreshMinutes, v => t.PingRefreshMinutes = v); Set(p.WatchdogSeconds, v => t.WatchdogSeconds = v);
        Set(p.Parallelism, v => t.Parallelism = v); Set(p.CheckUpdates, v => t.CheckUpdates = v);
        Set(p.PrivacyMode, v => t.PrivacyMode = v); Set(p.PrivacyHideWindows, v => t.PrivacyHideWindows = v);
        Set(p.StartCompact, v => t.StartCompact = v); Set(p.AutoSwitchOnServiceDown, v => t.AutoSwitchOnServiceDown = v);
        Set(p.AllowServerSwitch, v => t.AllowServerSwitch = v); Set(p.UseBackupServer, v => t.UseBackupServer = v);
        Set(p.AccountSafetyGuard, v => t.AccountSafetyGuard = v); Set(p.SiteWatch, v => t.SiteWatch = v); Set(p.CloseToTray, v => t.CloseToTray = v);
        if (p.EgressCountry != null) { t.EgressCountry = p.EgressCountry; n++; }
        return n;
    }
}
