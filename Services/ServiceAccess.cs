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
