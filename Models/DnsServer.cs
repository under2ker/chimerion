using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex.Models;

public enum ServerStatus { Unknown, Checking, Ok, Partial, Fail }

/// <summary>Один DNS-сервер из списка.</summary>
public class DnsServer : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _name = "";
    public string Name { get => _name; set { _name = value; On(); On(nameof(DisplayName)); } }

    private string _primary = "";
    /// <summary>Основной IPv4. Может быть пустым у DoH-only записей.</summary>
    public string Primary { get => _primary; set { _primary = value?.Trim() ?? ""; On(); On(nameof(Address)); On(nameof(DisplayName)); } }

    private string? _secondary;
    public string? Secondary { get => _secondary; set { _secondary = value?.Trim(); On(); On(nameof(Address)); } }

    private string? _dohUrl;
    /// <summary>DNS-over-HTTPS адрес, например https://example/dns-query.</summary>
    public string? DohUrl { get => _dohUrl; set { _dohUrl = value?.Trim(); On(); On(nameof(Address)); } }

    public string Source { get; set; } = "вручную";

    /// <summary>Страница, на которой сервер найден (кнопка 🔗 в списке).</summary>
    public string? SourceUrl { get; set; }

    [JsonIgnore]
    public bool HasSourceUrl => Uri.TryCreate(SourceUrl, UriKind.Absolute, out var u) &&
                                (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    [JsonIgnore]
    public string SourceHost => HasSourceUrl ? new Uri(SourceUrl!).Host : Source;
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>Найден за последние 7 дней — в таблице помечается «новый».</summary>
    [JsonIgnore]
    public bool IsNew => (DateTime.Now - AddedAt).TotalDays < 7;

    /// <summary>Строка плавно гаснет перед удалением из списка (автоочистка). Не сохраняется.</summary>
    private bool _fading;
    [JsonIgnore]
    public bool Fading { get => _fading; set { _fading = value; On(); } }

    /// <summary>Строка только что добавлена в список: проявляется один раз. Не сохраняется.</summary>
    private bool _justAdded;
    [JsonIgnore]
    public bool JustAdded { get => _justAdded; set { _justAdded = value; On(); } }

    private ServerStatus _status;
    public ServerStatus Status { get => _status; set { _status = value; On(); On(nameof(StatusText)); On(nameof(Rank)); } }

    private long? _latencyMs;
    public long? LatencyMs { get => _latencyMs; set { _latencyMs = value; On(); On(nameof(PingText)); On(nameof(Score)); } }

    private string _summary = "";
    /// <summary>Итог по сервисам: «ChatGPT ✓ · Claude ✗ …».</summary>
    public string Summary { get => _summary; set { _summary = value; On(); On(nameof(Chips)); } }

    /// <summary>Сервисы для таблицы: ChatGPT / Claude / Gemini с состоянием (ok / region / fail).</summary>
    [JsonIgnore]
    public List<ServiceChip> Chips => ServiceChip.Parse(_summary);

    /// <summary>Короткое имя без повтора адреса: «trofimovsky.ru: 1.2.3.4» → «trofimovsky.ru».</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var n = Name;
            foreach (var a in new[] { Primary, Secondary })
                if (!string.IsNullOrEmpty(a) && n.EndsWith(": " + a)) n = n[..^(a.Length + 2)];
            if (n.StartsWith("DoH ") && !string.IsNullOrEmpty(DohUrl)) n = n[4..];
            return n;
        }
    }

    /// <summary>Адрес одной строкой: IP (и резервный) или хост DoH.</summary>
    [JsonIgnore]
    public string Address
    {
        get
        {
            if (!string.IsNullOrEmpty(Primary))
                return string.IsNullOrEmpty(Secondary) ? Primary : $"{Primary}, {Secondary}";
            return Uri.TryCreate(DohUrl, UriKind.Absolute, out var u) ? $"DoH · {u.Host}" : "—";
        }
    }

    private string? _country;
    /// <summary>Код страны, где находится сервер (NL, RU…). Сохраняется, чтобы не запрашивать геолокацию заново.</summary>
    public string? Country { get => _country; set { _country = value; On(); On(nameof(CountryName)); On(nameof(LocationText)); On(nameof(LocationCell)); } }

    /// <summary>Город по IP (ipinfo). Может быть пустым, если сервис его не знает.</summary>
    public string? City { get; set; }

    /// <summary>Код страны по второй базе (ipwho.is). Совпадение с первой — повод доверять данным больше.</summary>
    public string? Country2 { get; set; }

    [JsonIgnore]
    public string CountryName2 => string.IsNullOrEmpty(Country2) ? "" : GeoLocation.CountryName(Country2);

    /// <summary>Сравнение двух баз: совпали, расходятся или вторая не ответила.</summary>
    [JsonIgnore]
    public string LocationNote => string.IsNullOrEmpty(Country) ? ""
        : string.IsNullOrEmpty(Country2) ? "вторая база не ответила"
        : Country2 == Country ? "обе базы совпадают"
        : $"базы расходятся: ipinfo — {CountryName}, ipwho.is — {CountryName2}";

    [JsonIgnore]
    public bool HasLocationWarning => !string.IsNullOrEmpty(Country) && !string.IsNullOrEmpty(Country2) && Country2 != Country;

    /// <summary>Подсказка к месту: сверка баз и оговорка, что это данные IP-баз, а не физическое место.</summary>
    [JsonIgnore]
    public string LocationTooltip => string.IsNullOrEmpty(Country) ? LocationText
        : $"{LocationText}\n{LocationNote}\nДанные IP-баз, точное физическое место может отличаться";

    /// <summary>Когда геолокацию в последний раз определяли. Через 30 дней — снова.</summary>
    public DateTime? GeoCheckedAt { get; set; }

    [JsonIgnore]
    public string CountryName => string.IsNullOrEmpty(Country) ? "" : GeoLocation.CountryName(Country);

    /// <summary>Место одной строкой: «Нидерланды, Амстердам» или «—», пока не определено.</summary>
    [JsonIgnore]
    public string LocationText => string.IsNullOrEmpty(Country) ? "—"
        : string.IsNullOrEmpty(City) ? CountryName : $"{CountryName}, {City}";

    /// <summary>Адрес прокси Smart DNS: сервер подменяет им адрес ИИ-сервиса. Берётся из проверки.</summary>
    public string? ProxyIp { get; set; }

    /// <summary>Страна прокси Smart DNS по первой и второй базе.</summary>
    public string? ProxyCountry { get; set; }
    public string? ProxyCountry2 { get; set; }

    [JsonIgnore]
    public string ProxyCountryName => string.IsNullOrEmpty(ProxyCountry) ? "" : GeoLocation.CountryName(ProxyCountry);

    /// <summary>Строка для таблицы: «Россия › SmartDNS: Нидерланды». Прокси без страны — «…», без подмены — «нет».</summary>
    [JsonIgnore]
    public string LocationCell
    {
        get
        {
            var server = string.IsNullOrEmpty(Country) ? "—" : CountryName;
            var proxy = !string.IsNullOrEmpty(ProxyCountry) ? ProxyCountryName
                      : string.IsNullOrEmpty(ProxyIp) ? "нет подмены" : "…";
            return $"{server} › SmartDNS: {proxy}";
        }
    }

    /// <summary>Новый адрес прокси: страну определим заново.</summary>
    public void SetProxy(string ip)
    {
        ProxyIp = ip;
        ProxyCountry = null;
        ProxyCountry2 = null;
        On(nameof(LocationCell));
    }

    public void SetProxyLocation(string? country, string? country2)
    {
        ProxyCountry = country;
        ProxyCountry2 = country2;
        On(nameof(LocationCell));
    }

    private ImageSource? _flag;
    /// <summary>Флаг страны для показа. Не сохраняется: грузится из кэша при запуске.</summary>
    [JsonIgnore]
    public ImageSource? Flag { get => _flag; set { _flag = value; On(); } }

    /// <summary>Записывает место и отмечает проверку времени.</summary>
    public void SetLocation(string? country, string? city, string? country2 = null)
    {
        City = city;
        Country2 = country2;
        GeoCheckedAt = DateTime.Now;
        Country = country;
        On(nameof(LocationNote)); On(nameof(LocationTooltip)); On(nameof(HasLocationWarning)); On(nameof(CountryName2));
    }

    private DateTime? _lastChecked;
    public DateTime? LastChecked { get => _lastChecked; set { _lastChecked = value; On(); } }

    private string? _lastError;
    public string? LastError { get => _lastError; set { _lastError = value; On(); } }

    /// <summary>Сколько проверок подряд прошли успешно / всего — для оценки стабильности.</summary>
    public int ChecksOk { get; set; }
    public int ChecksTotal { get; set; }
    /// <summary>Сколько последних проверок подряд провалено.</summary>
    public int FailStreak { get; set; }
    /// <summary>Сервер ни разу не подменил адрес ИИ-сервиса — это обычный резолвер, а не Smart DNS.</summary>
    public bool NoProxy { get; set; }

    private long? _jitterMs;
    /// <summary>Разброс пинга между замерами (меньше — стабильнее).</summary>
    public long? JitterMs { get => _jitterMs; set { _jitterMs = value; On(); On(nameof(PingText)); } }

    [JsonIgnore]
    public string PingText => LatencyMs is null ? "—" : JitterMs is null ? $"{LatencyMs}" : $"{LatencyMs} ±{JitterMs}";

    /// <summary>Итоговая оценка 0–100: доля успешных проверок, пинг, разброс, свежесть.</summary>
    [JsonIgnore]
    public int Score
    {
        get
        {
            if (ChecksTotal == 0 || Status is ServerStatus.Fail or ServerStatus.Unknown) return 0;
            double stab = (double)ChecksOk / ChecksTotal;                    // 0..1
            double conf = Math.Min(1, ChecksTotal / 3.0);                    // мало проверок — меньше доверия
            double ping = LatencyMs is null ? 0 : Math.Clamp(1 - (LatencyMs.Value - 20) / 480.0, 0, 1);
            double jit = JitterMs is null ? 0.5 : Math.Clamp(1 - JitterMs.Value / 150.0, 0, 1);
            double full = Status == ServerStatus.Ok ? 1 : 0.6;
            return (int)Math.Round(100 * full * (0.45 * stab * conf + 0.15 * stab + 0.25 * ping + 0.15 * jit));
        }
    }

    /// <summary>Мусор: не подменяет адреса или стабильно не работает.</summary>
    [JsonIgnore]
    public bool IsJunk => NoProxy || FailStreak >= 2 || (ChecksTotal >= 3 && ChecksOk == 0);

    [JsonIgnore]
    public string Stability => ChecksTotal == 0 ? "—" : $"{ChecksOk * 100 / ChecksTotal}% ({ChecksTotal})";

    /// <summary>Порядок сортировки: рабочие сверху, нерабочие внизу.</summary>
    [JsonIgnore]
    public int Rank => Status switch
    {
        ServerStatus.Ok or ServerStatus.Partial => 100 - Score, // лучшие сверху
        ServerStatus.Checking => 200,
        ServerStatus.Unknown => 300,
        _ => 400,
    };

    [JsonIgnore]
    public string StatusText => Status switch
    {
        ServerStatus.Ok => "Работает",
        ServerStatus.Partial => "Частично",
        ServerStatus.Fail => "Не работает",
        ServerStatus.Checking => "Проверка…",
        _ => "Не проверен",
    };

    /// <summary>Ключ для дедупликации.</summary>
    [JsonIgnore]
    public string Key => !string.IsNullOrEmpty(Primary) ? Primary : (DohUrl ?? Name).ToLowerInvariant();

    public void NotifyStability() { On(nameof(Stability)); On(nameof(Score)); On(nameof(Rank)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed record ServiceChip(string Name, string State)
{
    public static List<ServiceChip> Parse(string summary) =>
        string.IsNullOrWhiteSpace(summary) ? new()
        : summary.Split(" · ", StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var name = p.Split(' ', ':')[0];
            var state = p.Contains('✓') ? "ok" : p.Contains("регион") ? "region" : "fail";
            return new ServiceChip(name, state);
        }).ToList();
}
