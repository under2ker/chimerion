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

    /// <summary>IPv6-адреса этого же сервера (из источника или DoH). При подключении DNS ставится и для IPv6,
    /// и запросы по IPv6 не уходят мимо сервера к DNS роутера.</summary>
    public List<string> Ipv6 { get; set; } = new();

    public string Source { get; set; } = "вручную";
    /// <summary>Добавлен пользователем вручную: автоочистка его не убирает, даже если ИИ через него не открываются.</summary>
    public bool Manual { get; set; }

    /// <summary>Страница, на которой сервер найден (кнопка 🔗 в списке).</summary>
    public string? SourceUrl { get; set; }

    [JsonIgnore]
    public bool HasSourceUrl => Uri.TryCreate(SourceUrl, UriKind.Absolute, out var u) &&
                                (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    [JsonIgnore]
    public string SourceHost => HasSourceUrl ? new Uri(SourceUrl!).Host : Source;

    /// <summary>Откуда взят сервер, коротко: «github.com › main». Полный путь — над таблицей и в подсказке.</summary>
    [JsonIgnore]
    public string SourcePrettyShort => SourceLabel.Compact(HasSourceUrl ? SourceUrl : null, Source);

    /// <summary>Подсказка кнопки источника в таблице: откуда сервер и что сделает щелчок.</summary>
    [JsonIgnore]
    public string SourceHint => $"Открыть источник: {SourcePrettyShort}";
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

    private bool _isFavorite;
    /// <summary>Сервер в избранном: вкладка «Избранные», а сторож при замене сервера выбирает его при равной стабильности.</summary>
    public bool IsFavorite { get => _isFavorite; set { if (_isFavorite == value) return; _isFavorite = value; On(); } }

    private int _focusRank;
    /// <summary>Место в порядке «сначала лучшие для одного сервиса» (меньше — выше). Считается в окне, не сохраняется.</summary>
    [JsonIgnore]
    public int FocusRank { get => _focusRank; set { if (_focusRank == value) return; _focusRank = value; On(); } }

    /// <summary>Плашки сервисов перерисовать: сменился сервис, по которому выстроен список.</summary>
    public void RefreshChips() { _chips = null; On(nameof(Chips)); On(nameof(StabilityShown)); }

    /// <summary>Стабильность для колонки таблицы: когда список выстроен по одному сервису — по нему, иначе общая.</summary>
    [JsonIgnore]
    public string StabilityShown => ServiceChip.Focus is { } f && ServiceChecks > 0
        ? $"{ServiceStability(f)}% ({ServiceChecks})" : Stability;

    private string? _bestFor;
    /// <summary>Бейдж в таблице: «лучший для всех» или «лучший: Claude». Считается из стабильности, не сохраняется.</summary>
    [JsonIgnore]
    public string? BestFor { get => _bestFor; set { if (_bestFor == value) return; _bestFor = value; On(); On(nameof(IsBest)); } }

    [JsonIgnore]
    public bool IsBest => _bestFor != null;

    private bool _isConnected;
    /// <summary>Сервер, через который сейчас подключено. В таблице он первый. Не сохраняется.</summary>
    [JsonIgnore]
    public bool IsConnected { get => _isConnected; set { _isConnected = value; On(); } }

    private ServerStatus _status;
    public ServerStatus Status { get => _status; set { _status = value; On(); On(nameof(StatusText)); On(nameof(Rank)); } }

    private long? _latencyMs;
    public long? LatencyMs { get => _latencyMs; set { _latencyMs = value; On(); On(nameof(PingText)); On(nameof(Score)); } }

    private string _summary = "";
    /// <summary>Итог по сервисам: «ChatGPT ✓ · Claude ✗ …».</summary>
    public string Summary { get => _summary; set { _summary = value; _chips = null; On(); On(nameof(Chips)); } }

    private List<ServiceChip>? _chips;
    /// <summary>Сервисы для таблицы: ChatGPT / Claude / Gemini с состоянием (ok / region / fail).
    /// Разбирается один раз на каждое изменение Summary: список нужен таблице и выбору лучших каждые полсекунды.</summary>
    [JsonIgnore]
    public List<ServiceChip> Chips => _chips ??= ServiceChip.Parse(_summary);

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
                return (string.IsNullOrEmpty(Secondary) ? Primary : $"{Primary}, {Secondary}") + (Ipv6.Count > 0 ? " · IPv6" : "");
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

    /// <summary>Итог сверки IP-баз: что ответила каждая и как выбрана страна. Пусто у данных старых версий.</summary>
    public string? GeoVotes { get; set; }

    /// <summary>Базы определили страну по-разному, и даже третья не дала большинства.</summary>
    public bool GeoDisputed { get; set; }

    /// <summary>Сравнение баз: совпали, страна выбрана большинством или вторая не ответила.</summary>
    [JsonIgnore]
    public string LocationNote => string.IsNullOrEmpty(Country) ? ""
        : !string.IsNullOrEmpty(GeoVotes) ? GeoVotes
        : string.IsNullOrEmpty(Country2) ? "вторая база не ответила"
        : Country2 == Country ? "обе базы совпадают"
        : $"базы расходятся: ipinfo: {CountryName}, ipwho.is: {CountryName2}";

    [JsonIgnore]
    public bool HasLocationWarning => !string.IsNullOrEmpty(GeoVotes) ? GeoDisputed
        : !string.IsNullOrEmpty(Country) && !string.IsNullOrEmpty(Country2) && Country2 != Country;

    /// <summary>Подсказка к месту: сверка баз и оговорка, что это данные IP-баз, а не физическое место.</summary>
    [JsonIgnore]
    public string LocationTooltip => (string.IsNullOrEmpty(Country) ? LocationText
        : $"{LocationText}\n{LocationNote}\nПо данным IP-баз. Точное физическое место может отличаться")
        + string.Concat(EgressBy.OrderBy(kv => kv.Key).Select(kv =>
            $"\nИИ видят, {kv.Key}: {GeoLocation.CountryName(kv.Value.Country)}" +
            (EgressCityByIp.TryGetValue(kv.Value.Ip, out var c) && c.Length > 0 ? ", " + GeoLocation.CityName(c) : "") + $" ({kv.Value.Ip})"));

    /// <summary>Когда геолокацию в последний раз определяли. Через 30 дней — снова.</summary>
    public DateTime? GeoCheckedAt { get; set; }

    [JsonIgnore]
    public string CountryName => string.IsNullOrEmpty(Country) ? "" : GeoLocation.CountryName(Country);

    /// <summary>Место одной строкой: «Нидерланды, Амстердам» или «—», пока не определено.</summary>
    [JsonIgnore]
    public string LocationText => string.IsNullOrEmpty(Country) ? "—"
        : string.IsNullOrEmpty(City) ? CountryName : $"{CountryName}, {City}";

    /// <summary>Адрес прокси Smart DNS: сервер подменяет им адрес ИИ-сервиса. Берется из проверки.</summary>
    public string? ProxyIp { get; set; }

    /// <summary>Страна прокси Smart DNS по первой и второй базе.</summary>
    public string? ProxyCountry { get; set; }
    public string? ProxyCountry2 { get; set; }

    /// <summary>
    /// Настоящий выход в интернет по каждому ИИ (имя сервиса → адрес и страна, которые видит сервис). Может отличаться
    /// от страны прокси: прокси пересылает трафик дальше (keyoneday в России выходит через Cloudflare во Франции).
    /// </summary>
    public Dictionary<string, Egress> EgressBy { get; set; } = new();
    /// <summary>Город выхода по адресу (страну дает сам trace Cloudflare, город — IP-база).</summary>
    public Dictionary<string, string> EgressCityByIp { get; set; } = new();

    public void NotifyEgress() { On(nameof(LocationCell)); On(nameof(LocationTooltip)); }

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
            // Выход не там, где прокси: ИИ видят именно выход (так ChatGPT называет Францию у российского сервера)
            var seen = EgressBy.Values.Select(e => e.Country).Distinct().ToList();
            var egress = seen.Count == 0 ? ""
                       : seen.Count > 1 ? " → ИИ видят разные страны"
                       : seen[0] != ProxyCountry ? $" → ИИ видят: {GeoLocation.CountryName(seen[0])}" : "";
            return $"{server} › SmartDNS: {proxy}{egress}";
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

    /// <summary>Записывает место по итогу сверки IP-баз и отмечает время проверки.</summary>
    public void SetLocation(GeoLocation.GeoResult r)
    {
        GeoVotes = r.Votes;
        GeoDisputed = r.Disputed;
        SetLocation(r.Country, r.City, r.Second);
    }

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

    /// <summary>История полных проверок за последние 14 дней (не больше 200 точек): работал ли сервер и пинг.</summary>
    public List<CheckPoint> History { get; set; } = new();

    public void AddHistory(bool ok, long? ms, DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        History.Add(new CheckPoint(now, ok, ms));
        History.RemoveAll(p => now - p.T > TimeSpan.FromDays(14));
        if (History.Count > 200) History.RemoveRange(0, History.Count - 200);
    }

    /// <summary>Итог истории для подсказки: доступность и пинг за 7 дней, сравнение с предыдущей неделей.</summary>
    [JsonIgnore]
    public string HistoryText
    {
        get
        {
            var now = DateTime.Now;
            var week = History.Where(p => now - p.T <= TimeSpan.FromDays(7)).ToList();
            if (week.Count == 0) return "Истории проверок пока нет: она копится с каждой полной проверкой";
            var ok = week.Count(p => p.Ok);
            var pings = week.Where(p => p.Ok && p.Ms != null).Select(p => p.Ms!.Value).OrderBy(v => v).ToList();
            var text = $"За 7 дней: работал в {ok} из {week.Count} проверок ({ok * 100 / week.Count}%)";
            if (pings.Count > 0) text += $", пинг {pings[0]}–{pings[^1]} мс, обычно {pings[pings.Count / 2]} мс";
            var prev = History.Where(p => now - p.T > TimeSpan.FromDays(7)).ToList();
            if (prev.Count >= 5)
            {
                int was = prev.Count(p => p.Ok) * 100 / prev.Count, isNow = ok * 100 / week.Count;
                if (isNow <= was - 15) text += $". Стал хуже: неделей раньше {was}%";
                else if (isNow >= was + 15) text += $". Стал лучше: неделей раньше {was}%";
            }
            return text;
        }
    }
    /// <summary>Сколько последних проверок подряд провалено.</summary>
    public int FailStreak { get; set; }

    /// <summary>Успешные проверки по каждому ИИ-сервису отдельно (имя → число). Делится на ServiceChecks.</summary>
    public Dictionary<string, int> ServiceOk { get; set; } = new();
    /// <summary>Проверки, в которых считались сервисы по отдельности (счет начат с версии 0.1.19).</summary>
    public int ServiceChecks { get; set; }
    /// <summary>Проверки, в которых работали сразу все сервисы.</summary>
    public int AllOk { get; set; }

    /// <summary>Стабильность по сервису в процентах. Null, если по нему еще не было проверок.</summary>
    public int? ServiceStability(string service) =>
        ServiceChecks == 0 ? null : ServiceOk.GetValueOrDefault(service) * 100 / ServiceChecks;

    /// <summary>Стабильность «все сервисы сразу» в процентах. Null, если проверок еще не было.</summary>
    [JsonIgnore]
    public int? AllStability => ServiceChecks == 0 ? null : AllOk * 100 / ServiceChecks;
    /// <summary>Сервер ни разу не подменил адрес ИИ-сервиса — это обычный резолвер, а не Smart DNS.</summary>
    public bool NoProxy { get; set; }

    private long? _jitterMs;
    /// <summary>Разброс пинга между замерами (меньше — стабильнее).</summary>
    public long? JitterMs { get => _jitterMs; set { _jitterMs = value; On(); On(nameof(PingText)); } }

    [JsonIgnore]
    public string PingText => LatencyMs is null ? "—" : $"{LatencyMs}";

    private long? _aiMs;
    /// <summary>
    /// Скорость нейросетей через сервер: сколько отвечает API сервиса через его прокси (мс), сглажено по проверкам.
    /// Пинг меряет только DNS, а скорость ответа ChatGPT зависит от прокси: через один ответы идут бегом, через
    /// другой с задержкой в секунды.
    /// </summary>
    public long? AiMs { get => _aiMs; set { _aiMs = value; On(); On(nameof(AiText)); } }

    /// <summary>Новый замер: сглаживание (половина нового, половина прежнего), чтобы один медленный ответ не ронял сервер.</summary>
    public void AddAiSample(long? ms)
    {
        if (ms is not { } v) return;
        AiMs = AiMs is { } old ? (old + v) / 2 : v;
    }

    [JsonIgnore]
    public string AiText => AiMs is null ? "—" : $"{AiMs}";

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
    public bool IsJunk => NoProxy || FailStreak >= 2 || (ChecksTotal >= 3 && ChecksOk == 0)
        // Ни разу не заработал и с последней проверки прошло больше трех дней: такие копились в списке тысячами.
        // Если поиск найдет его снова через сутки, он вернется на проверку
        || (ChecksTotal >= 1 && ChecksOk == 0 && Status == ServerStatus.Fail && LastChecked is { } lc && DateTime.Now - lc > TimeSpan.FromDays(3));

    [JsonIgnore]
    public string Stability => ChecksTotal == 0 ? "—" : $"{ChecksOk * 100 / ChecksTotal}% ({ChecksTotal})";

    private static readonly HashSet<string> CatalogNames = Catalog.Servers().Select(c => c.Name).ToHashSet();

    /// <summary>Короткая подпись в таблице и трее. Адрес-ссылку не показываем:
    /// встроенные сервисы — по названию, остальные — тип, страна и конец IP. Полное имя — в подсказке.</summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            var n = DisplayName;
            if (CatalogNames.Contains(n)) return Clip(n);
            // Страна и город говорят больше, чем конец IP; адрес и так показан строкой ниже
            if (!string.IsNullOrEmpty(Country))
                return Clip(CountryName + (string.IsNullOrEmpty(City) ? "" : ", " + GeoLocation.CityName(City)));
            return Clip(string.IsNullOrEmpty(DohUrl) ? n : "DoH · " + n);
        }
    }

    /// <summary>Обрезает подпись, чтобы не растягивать таблицу и меню.</summary>
    public static string Clip(string s, int max = 28) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Подсказка к оценке: показатели по отдельности (одно число прячет разные отказы) и из чего сложена оценка.</summary>
    [JsonIgnore]
    public string ScoreHint =>
        ServerMetrics.Of(this).Describe() + "\n\n" +
        "Оценка 0–100 только для порядка в таблице: стабильность 60, пинг 25, разброс 15. Лучший сервер выбирается по стабильности";

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

    public void NotifyStability()
    {
        On(nameof(Stability)); On(nameof(StabilityShown)); On(nameof(Score)); On(nameof(Rank));
        On(nameof(HistoryText)); On(nameof(HistoryPoints)); On(nameof(ScoreHint));
    }

    /// <summary>Копия истории для графика: новый объект, чтобы подсказка перерисовалась.</summary>
    [JsonIgnore]
    public IReadOnlyList<CheckPoint> HistoryPoints => History.ToList();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Точка истории сервера: когда проверяли, работал ли и с каким пингом.</summary>
public sealed record CheckPoint(DateTime T, bool Ok, long? Ms);

public sealed record ServiceChip(string Name, string State)
{
    /// <summary>Сервис, по которому выстроен список (после проверки только его). Null — порядок для всех сервисов.</summary>
    public static string? Focus { get; set; }

    /// <summary>Плашка другого сервиса, когда список выстроен по одному: приглушается, чтобы выделялся нужный.</summary>
    public bool Dim => Focus != null && Name != Focus;

    /// <summary>Логотип сервиса для чипа в таблице. Null, если логотипа нет.</summary>
    public System.Windows.Media.ImageSource? Logo => Name switch
    {
        "ChatGPT" => System.Windows.Application.Current?.TryFindResource("LogoOpenAI") as System.Windows.Media.ImageSource,
        "Claude" => System.Windows.Application.Current?.TryFindResource("LogoClaude") as System.Windows.Media.ImageSource,
        "Gemini" => System.Windows.Application.Current?.TryFindResource("LogoGemini") as System.Windows.Media.ImageSource,
        _ => null,
    };

    public static List<ServiceChip> Parse(string summary) =>
        string.IsNullOrWhiteSpace(summary) ? new()
        : summary.Split(" · ", StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var name = p.Split(' ', ':')[0];
            // «капча» — желтым, как «регион»: доступ есть, но мешает проверка Google, а не блокировка сервиса
            var state = p.Contains('✓') ? "ok" : p.Contains("регион") || p.Contains("капча") ? "region" : "fail";
            return new ServiceChip(name, state);
        }).ToList();
}
