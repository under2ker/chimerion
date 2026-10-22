using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Решает, открыт ли ИИ-сервис, по ответу его API. Доступ засчитывается только при ожидаемом ответе.
/// Блокировка, страница-заглушка, редирект и ошибка сервера доступом не считаются.
/// Одна и та же логика используется и при проверке серверов, и в блоке «Проверка подключения».
/// </summary>
public static class ServiceAccess
{
    public enum Result { Open, Closed, Error }

    private static readonly int[] DefaultOk = { 200, 401 };

    /// <summary>
    /// Для сайтов сервиса (chatgpt.com, claude.ai и т.п.), а не для API. Сайт за Cloudflare отвечает 403 защиты,
    /// но открывается в браузере, поэтому 403 здесь доступом не считаем закрытым. Блокируют только маркеры
    /// региона в теле ответа. Ответ 5xx — сервис не отвечает нормально.
    /// </summary>
    public static Result ClassifyWeb(CheckTarget t, int code, string body)
    {
        if (t.BlockedMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase))) return Result.Closed;
        return code >= 200 && code < 500 ? Result.Open : Result.Error;
    }

    public static Result Classify(CheckTarget t, int code, string body)
    {
        if (t.BlockedStatusCodes.Contains(code) ||
            t.BlockedMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return Result.Closed;

        IEnumerable<int> ok = t.OkStatusCodes.Count > 0 ? t.OkStatusCodes : DefaultOk;
        if (ok.Contains(code)) return Result.Open;

        // Остальные 4xx (в том числе страница защиты Cloudflare) считаем закрытым доступом.
        // Редиректы (3xx), 5xx и прочее — сервис не ответил нормально, это не доказательство доступа.
        return code >= 400 && code < 500 ? Result.Closed : Result.Error;
    }
}
