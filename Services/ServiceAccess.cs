using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Решает, открыт ли ИИ-сервис, по ответу его API или сайта. Доступ засчитывается только при ожидаемом ответе.
/// Блокировка, страница-заглушка, редирект и ошибка сервера доступом не считаются.
/// Одна и та же логика используется и при проверке серверов, и в блоке «Проверка подключения».
/// Признаки сняты с живых ответов: напрямую из заблокированного региона и через прокси Smart DNS.
/// </summary>
public static class ServiceAccess
{
    public enum Result { Open, Closed, Error }

    private static readonly int[] DefaultOk = { 200, 401 };

    /// <summary>
    /// Признаки закрытого сайта: claude.ai из заблокированного региона уводит на claude.com/app-unavailable-in-region,
    /// Google отправляет помеченный адрес прокси на капчу google.com/sorry.
    /// </summary>
    private static readonly string[] WebBlockMarkers = { "app-unavailable-in-region", "google.com/sorry" };

    /// <summary>Страница проверки Cloudflare: браузер ее проходит сам, значит, сайт доступен.</summary>
    private static readonly string[] ChallengeMarkers = { "challenge-platform", "cf-chl", "cf_chl" };

    /// <summary>Ответ API: код и тело. Без ключа нормой является 401 с текстом ошибки самого сервиса.</summary>
    public static Result Classify(CheckTarget t, int code, string body)
    {
        if (t.BlockedStatusCodes.Contains(code) ||
            t.BlockedMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return Result.Closed;

        IEnumerable<int> ok = t.OkStatusCodes.Count > 0 ? t.OkStatusCodes : DefaultOk;
        if (ok.Contains(code))
        {
            // Код подходит, но ответ должен быть от самого сервиса, а не от чужого прокси или заглушки
            if (t.OkMarkers.Count > 0 && !t.OkMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
                return Result.Error;
            return Result.Open;
        }

        // Остальные 4xx (в том числе страница защиты Cloudflare) считаем закрытым доступом.
        // Редиректы (3xx), 5xx и прочее — сервис не ответил нормально, это не доказательство доступа.
        return code >= 400 && code < 500 ? Result.Closed : Result.Error;
    }

    /// <summary>
    /// Ответ сайта сервиса (chatgpt.com, claude.ai, gemini.google.com). Правила по живым ответам:
    /// редирект на страницу «недоступно в регионе» или на капчу Google — закрыто;
    /// редирект внутри того же сайта — доступ (так работает вход в аккаунт); на чужой сайт — неясно;
    /// 403 с проверкой Cloudflare — доступ, браузер ее пройдет; 403 без нее — закрыто;
    /// остальные ответы до 500 — сайт ответил; 5xx — сервис не отвечает нормально.
    /// </summary>
    public static Result ClassifyWeb(CheckTarget t, int code, string body, string? location = null)
    {
        if (WebBlockMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)
                                  || (location?.Contains(m, StringComparison.OrdinalIgnoreCase) ?? false)))
            return Result.Closed;

        if (code >= 300 && code < 400)
        {
            if (string.IsNullOrEmpty(location)) return Result.Error;
            if (!Uri.TryCreate(location, UriKind.Absolute, out var to)) return Result.Open; // относительный адрес: тот же сайт
            return SameSite(t.Host, to.Host) ? Result.Open : Result.Error;
        }
        if (code == 403)
            return ChallengeMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)) ? Result.Open : Result.Closed;
        return code >= 200 && code < 500 ? Result.Open : Result.Error;
    }

    /// <summary>Один и тот же сайт: совпадают два последних уровня домена (accounts.google.com и www.google.com).</summary>
    internal static bool SameSite(string a, string b)
    {
        static string Root(string h)
        {
            var parts = h.TrimEnd('.').ToLowerInvariant().Split('.');
            return parts.Length <= 2 ? string.Join('.', parts) : string.Join('.', parts[^2..]);
        }
        return Root(a) == Root(b);
    }
}
