namespace Dnex.Services;

/// <summary>Насколько полная проверка: полная заменяет облегченную, с тестом утечки заменяет обе.</summary>
public enum ProbeLevel { Light = 0, Full = 1, Leak = 2 }

/// <summary>
/// Общий ограничитель проверок подключения. Трей, диагностика, главная и окно скорости раньше проверяли сервисы
/// каждый сам, и с одного адреса уходило по несколько одинаковых пачек запросов к ChatGPT, Claude и Gemini.
/// Теперь:
/// - если такая же или более полная проверка уже идет, новый запрос ждет ее результата;
/// - если она закончилась меньше Reuse назад, отдается готовый результат;
/// - после смены подключения или сети (Invalidate) старый результат не отдается и идущая проверка не засчитывается.
/// Сколько проверок запущено и сколько раз результат отдан повторно, видно в отчете диагностики.
/// </summary>
public sealed class ProbeGate<T> where T : class
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private (Task<T> Task, ProbeLevel Level, long Epoch)? _running;
    private (T Value, ProbeLevel Level, DateTime At, long Epoch)? _last;
    private long _epoch;

    public TimeSpan Reuse { get; }
    /// <summary>Запущено проверок, присоединились к идущей, получили готовый результат.</summary>
    public int Started { get; private set; }
    public int Joined { get; private set; }
    public int Reused { get; private set; }

    public ProbeGate(TimeSpan reuse, Func<DateTime>? now = null)
    {
        Reuse = reuse;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Подключение или сеть сменились: прежние результаты больше не про текущее состояние.</summary>
    public void Invalidate()
    {
        lock (_lock) { _epoch++; _last = null; }
    }

    /// <summary>
    /// Результат проверки уровня level: готовый, общий с идущей или новый. ct отменяет только ожидание этого
    /// вызывающего, сама проверка доводится до конца для остальных.
    /// </summary>
    public Task<T> RunAsync(ProbeLevel level, Func<Task<T>> run, CancellationToken ct = default)
    {
        Task<T> task;
        TaskCompletionSource<T>? mine = null;
        long epoch;
        lock (_lock)
        {
            epoch = _epoch;
            if (_last is { } l && l.Epoch == epoch && l.Level >= level && _now() - l.At < Reuse)
            {
                Reused++;
                return Task.FromResult(l.Value);
            }
            if (_running is { } r && r.Epoch == epoch && r.Level >= level)
            {
                Joined++;
                task = r.Task;
            }
            else
            {
                Started++;
                mine = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _running = (mine.Task, level, epoch);
                task = mine.Task;
            }
        }
        if (mine != null) _ = CompleteAsync(mine, run, level, epoch);
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task CompleteAsync(TaskCompletionSource<T> tcs, Func<Task<T>> run, ProbeLevel level, long epoch)
    {
        try
        {
            var value = await Task.Run(run).ConfigureAwait(false);
            lock (_lock)
            {
                if (_running?.Task == tcs.Task) _running = null;
                // Более полный свежий результат не затираем облегченным
                if (epoch == _epoch && (_last is not { } l || l.Epoch != epoch || level >= l.Level || _now() - l.At >= Reuse))
                    _last = (value, level, _now(), epoch);
            }
            tcs.TrySetResult(value);
        }
        catch (Exception e)
        {
            lock (_lock) { if (_running?.Task == tcs.Task) _running = null; }
            tcs.TrySetException(e);
        }
    }

    /// <summary>Строка для отчета диагностики.</summary>
    public string Summary() => $"Проверки подключения: запущено {Started}, общий результат {Joined + Reused} раз (ждали идущую {Joined}, готовый {Reused})";

    /// <summary>
    /// Интервал фоновой проверки после нескольких неудач подряд: вторая подряд удваивает паузу, дальше
    /// не чаще раза в полчаса (или обычного интервала, если он больше). Одна неудача интервал не меняет.
    /// </summary>
    public static TimeSpan Backoff(TimeSpan normal, int failStreak)
    {
        if (failStreak <= 1) return normal;
        var cap = normal > TimeSpan.FromMinutes(30) ? normal : TimeSpan.FromMinutes(30);
        var next = failStreak == 2 ? normal * 2 : cap;
        return next > cap ? cap : next;
    }
}
