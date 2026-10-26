namespace Dnex.Services;

/// <summary>Страна выхода сервиса сменилась: было From, стало To. ToClosed — в новой стране сервис не работает.</summary>
public sealed record EgressChange(string Service, string From, string To, bool ToClosed);

/// <summary>
/// Следит, из какой страны ИИ-сервисы видят подключение. Прокси Smart DNS иногда меняет узел выхода
/// (например, был Нидерланды, стал Германия или Россия), и сервис может закрыться без видимых причин.
/// Сравнение идет только в пределах одного подключения: при смене сервера страны запоминаются заново.
/// Смена засчитывается, только если новая страна видна две проверки подряд: Windows иногда отправляет запрос на
/// запасной DNS, и одна проверка может показать чужую страну. Такой скачок туда и обратно людей только пугает.
/// </summary>
public sealed class EgressWatch
{
    /// <summary>Страны, где ChatGPT, Claude или Gemini недоступны: выход туда почти наверняка закроет сервис.</summary>
    public static readonly HashSet<string> Closed = new(StringComparer.OrdinalIgnoreCase)
    {
        "RU", "BY", "CN", "HK", "IR", "KP", "CU", "SY",
    };

    private readonly Dictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Новая страна, увиденная один раз: ждет подтверждения следующей проверкой.</summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private string? _serverId;

    /// <summary>
    /// Новые данные проверки. serverId — текущее подключение (null — не подключено). Возвращает смены стран
    /// по сравнению с прошлой проверкой того же подключения. Сервисы без данных о выходе пропускаются.
    /// </summary>
    public IReadOnlyList<EgressChange> Update(string? serverId, IEnumerable<(string Service, string? Country)> now)
    {
        var changes = new List<EgressChange>();
        if (serverId == null || serverId != _serverId)
        {
            _last.Clear();
            _pending.Clear();
            _serverId = serverId;
            if (serverId == null) return changes;
        }
        foreach (var (service, country) in now)
        {
            if (string.IsNullOrEmpty(country)) continue;
            if (!_last.TryGetValue(service, out var was) || string.Equals(was, country, StringComparison.OrdinalIgnoreCase))
            {
                _last[service] = country;
                _pending.Remove(service); // страна та же: прошлый скачок был разовым
                continue;
            }
            if (_pending.TryGetValue(service, out var seen) && string.Equals(seen, country, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new EgressChange(service, was, country, Closed.Contains(country)));
                _last[service] = country;
                _pending.Remove(service);
            }
            else _pending[service] = country;
        }
        return changes;
    }
}
