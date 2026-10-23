namespace Dnex.Models;

/// <summary>Что проверять: сервис ИИ и признаки гео-блокировки в ответе.</summary>
public class CheckTarget
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "/";
    public bool Enabled { get; set; } = true;
    /// <summary>Строки в теле ответа, означающие «регион не поддерживается».</summary>
    public List<string> BlockedMarkers { get; set; } = new();
    /// <summary>HTTP-коды, которые считаются блокировкой (без ключа API нормой является 401).</summary>
    public List<int> BlockedStatusCodes { get; set; } = new();
    /// <summary>Коды, по которым видно, что запрос дошел до настоящего API. Остальные ответы доступом не считаются.
    /// Пусто — 200 и 401.</summary>
    public List<int> OkStatusCodes { get; set; } = new();
    /// <summary>Дополнительные хосты сервиса (вход, сайт, CDN). Они не влияют на вердикт «доступен»,
    /// но проверяются при подключении и попадают в журнал.</summary>
    public List<string> ExtraHosts { get; set; } = new();
    /// <summary>Подпись настоящего ответа API: строки, одна из которых должна быть в теле при коде из OkStatusCodes.
    /// Без нее ответ мог прийти от чужого прокси или заглушки, и доступ не засчитывается.</summary>
    public List<string> OkMarkers { get; set; } = new();
    /// <summary>Сайт сервиса для второй проверки («chatgpt.com/»): если сайт закрыт, сервис не считается доступным,
    /// даже когда API ответил. Пусто — проверяется только API.</summary>
    public string? WebCheck { get; set; }
    /// <summary>API без ключа отвечает одинаково и из закрытого региона, и через прокси (так у Gemini).
    /// Тогда доступ подтверждается только подменой адреса: без нее сервис считается недоступным.</summary>
    public bool RequiresProxy { get; set; }
    /// <summary>Владельцы сетей самого сервиса (поле org в ipinfo). Адрес, который не совпал с эталоном,
    /// но принадлежит сервису, подменой не считается: у Google и Cloudflare много адресов в разных регионах.</summary>
    public List<string> OwnerOrgs { get; set; } = new();
}

/// <summary>Откуда парсить списки серверов.</summary>
public class SourceConfig
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>"text" — вытащить IP и DoH-адреса из любой страницы/файла;
    /// "json" — массив объектов {name, primary, secondary, doh}.</summary>
    public string Type { get; set; } = "text";
    public bool Enabled { get; set; } = true;
}

public class AppSettings
{
    /// <summary>Эталонный резолвер (DoH, чтобы провайдер не подменял ответ).
    /// Сравнение с ним показывает, подменяет ли проверяемый сервер адреса ИИ-сервисов.</summary>
    public string ReferenceDoh { get; set; } = "https://1.1.1.1/dns-query";
    public string ReferenceDohFallback { get; set; } = "https://dns.google/dns-query";

    public int TimeoutSeconds { get; set; } = 6;

    /// <summary>Как часто трей сам проверяет ИИ-сервисы (минуты, 1–60).</summary>
    public int TrayCheckMinutes { get; set; } = 5;
    /// <summary>Проверять наличие новой версии на GitHub через несколько секунд после запуска. Файлы не загружаются.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Версия, о которой пользователь выбрал «Пропустить версию».</summary>
    public string? SkippedUpdate { get; set; }
    /// <summary>Как часто обновляется пинг всех серверов в таблице, в шапке и в трее (минуты, 1–60).</summary>
    public int PingRefreshMinutes { get; set; } = 10;
    /// <summary>Как часто сторож проверяет DNS, пока подключение активно (секунды, 15–600).</summary>
    public int WatchdogSeconds { get; set; } = 60;

    /// <summary>Крестик окна прячет программу в трей (подключение продолжает работать). Выход — из меню трея.</summary>
    public bool CloseToTray { get; set; } = true;
    public int Parallelism { get; set; } = 24;

    /// <summary>Не добавлять эти адреса при парсинге (обычные публичные резолверы).</summary>
    public List<string> ExcludeIps { get; set; } = new()
    {
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112",
        "77.88.8.8", "77.88.8.1", "208.67.222.222", "208.67.220.220",
    };

    public List<CheckTarget> Targets { get; set; } = new()
    {
        // Признаки сняты с живых ответов. ChatGPT и Claude из закрытого региона отвечают 403 с текстом блокировки,
        // через прокси 401 с ошибкой ключа. Gemini без ключа в обоих случаях отвечает 403 PERMISSION_DENIED,
        // поэтому для него доступ подтверждают подмена адреса и сайт (через помеченный прокси Google дает капчу).
        new() { Name = "ChatGPT", Host = "api.openai.com", Path = "/v1/models",
                BlockedMarkers = { "unsupported_country" }, OkStatusCodes = { 200, 401 },
                OkMarkers = { "invalid_request_error", "invalid_api_key" }, WebCheck = "chatgpt.com/", OwnerOrgs = { "OpenAI", "Cloudflare" },
                ExtraHosts = { "chatgpt.com", "auth.openai.com", "cdn.oaistatic.com" } },
        new() { Name = "Claude", Host = "api.anthropic.com", Path = "/v1/models",
                BlockedMarkers = { "Request not allowed" }, BlockedStatusCodes = { 403 }, OkStatusCodes = { 200, 401 },
                OkMarkers = { "authentication_error" }, WebCheck = "claude.ai/new", OwnerOrgs = { "Anthropic" },
                ExtraHosts = { "claude.ai" } },
        new() { Name = "Gemini", Host = "generativelanguage.googleapis.com", Path = "/v1beta/models",
                BlockedMarkers = { "User location is not supported" }, OkStatusCodes = { 200, 401, 403 },
                OkMarkers = { "PERMISSION_DENIED", "API_KEY_INVALID" }, WebCheck = "gemini.google.com/app", RequiresProxy = true,
                OwnerOrgs = { "Google" },
                ExtraHosts = { "gemini.google.com", "accounts.google.com" } },
    };

    /// <summary>Стартовые страницы глубокого парсера. Добавляйте свои: сайты, raw-файлы GitHub, t.me/s/канал.</summary>
    public List<SourceConfig> Sources { get; set; } = new()
    {
        new() { Name = "Comss.ru", Url = "https://www.comss.ru/page.php?id=16095" },
        new() { Name = "Код Дурова", Url = "https://kod.ru/kak-polzovatsya-chatgpt-gemini-spotify-v-rossii" },
        new() { Name = "Хабр", Url = "https://habr.com/ru/articles/982070/" },
        new() { Name = "Хабр", Url = "https://habr.com/ru/articles/870494/" },
        new() { Name = "4PDA: DNS Club", Url = "https://4pda.to/forum/index.php?showtopic=1117490" },
        new() { Name = "4PDA: DNS Club, DNS для ИИ", Url = "https://4pda.to/forum/index.php?showtopic=1117490&view=findpost&p=129800546" },
        new() { Name = "4PDA: DNS Club, DNS и гайды для ИИ", Url = "https://4pda.to/forum/index.php?showtopic=1117490&view=findpost&p=137714684" },
        new() { Name = "4PDA: Клубы и общение (ИИ)", Url = "https://4pda.to/forum/index.php?showforum=1243" },
        new() { Name = "4PDA: Искусственный интеллект", Url = "https://4pda.to/forum/index.php?showforum=1240" },
        new() { Name = "Пример: Telegram-канал (замените имя)", Url = "https://t.me/s/CHANNEL_NAME", Enabled = false },
        new() { Name = "Пример: JSON-список", Url = "https://example.com/servers.json", Type = "json", Enabled = false },
    };

    // ---------- Глубокий парсер ----------

    /// <summary>Сколько переходов по ссылкам делать от стартовой страницы.</summary>
    public int CrawlDepth { get; set; } = 2;
    /// <summary>Предел загруженных страниц за один запуск.</summary>
    public int CrawlMaxPages { get; set; } = 500;
    public int CrawlParallelism { get; set; } = 6;
    /// <summary>Сколько страниц Telegram-канала листать назад (?before=…).</summary>
    public int TelegramPages { get; set; } = 80; // хватит на всю тему DNS Club (~78 стр.)
    /// <summary>Переходить только по ссылкам, где в адресе или тексте есть одно из слов.</summary>
    public List<string> LinkKeywords { get; set; } = new()
    {
        "dns", "днс", "doh", "chatgpt", "openai", "gpt", "gemini", "claude", "grok", "нейросет", "ии-", "-ai", "ai-",
        "без-vpn", "bez-vpn", "smart", "proxy", "геоблок", "unblock", "club", "клуб",
    };
    /// <summary>Сайты, с которых можно уходить по ссылкам на другие домены (агрегаторы, статьи).</summary>
    public bool FollowExternalLinks { get; set; } = true;

    /// <summary>Автопоиск новых источников через поисковик (DuckDuckGo HTML, без API-ключа).</summary>
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryResultsPerQuery { get; set; } = 8;
    public List<string> DiscoveryQueries { get; set; } = new()
    {
        "DNS для ChatGPT в России",
        "DNS для доступа к нейросетям без VPN",
        "smart DNS Gemini Claude Россия",
        "DoH сервер для ChatGPT",
        "github dns list chatgpt russia",
    };
    /// <summary>Cookie для сайтов, где без входа видно не все. Ключ — домен (например "4pda.to"),
    /// значение — строка Cookie из браузера, где вы залогинены (DevTools → Network → заголовок Cookie).</summary>
    public Dictionary<string, string> HostCookies { get; set; } = new();

    /// <summary>Домены, которые парсер никогда не открывает.</summary>
    public List<string> BlockedHosts { get; set; } = new()
    {
        "youtube.com", "www.youtube.com", "vk.com", "facebook.com", "twitter.com", "x.com", "instagram.com",
        "apps.apple.com", "play.google.com", "duckduckgo.com",
    };
}
