using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>Подробности для файла и отчета. В список событий окна эти записи не поступают.</summary>
internal static class SupportTrace
{
    private const int MaxPending = 4000;
    private static readonly ConcurrentQueue<(DateTime Time, string Text)> Pending = new();
    private static readonly object Gate = new();
    private static readonly AsyncLocal<long?> CurrentOperation = new();
    private static string _session = "";
    private static long _sequence, _operations;
    private static int _scheduled, _dropped;
    internal static bool Enabled { get; private set; }

    public static void Start()
    {
        if (Enabled) return;
        _session = Guid.NewGuid().ToString("N")[..8];
        Enabled = true;
        Write($"Начало сеанса; версия={UpdateChecker.CurrentVersion}; Windows={Environment.OSVersion.Version}; разрядность={IntPtr.Size * 8}");
    }

    internal static void Stop()
    {
        Enabled = false;
        Flush();
    }

    /// <summary>Только служебные параметры: значения полей, URL, заголовки и тела ответов сюда не передаются.</summary>
    public static void Write(string text)
    {
        if (!Enabled) return;
        if (Pending.Count >= MaxPending) { Interlocked.Increment(ref _dropped); return; }
        var operation = CurrentOperation.Value is { } id ? $"; операция={id}" : "";
        Pending.Enqueue((DateTime.Now, PrivacyGuard.Scrub($"[Подробно; сеанс={_session}; запись={Interlocked.Increment(ref _sequence)}{operation}] {text}")));
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
            _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        try { Flush(); }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (!Pending.IsEmpty && Interlocked.Exchange(ref _scheduled, 1) == 0) _ = Task.Run(Drain);
        }
    }

    /// <summary>Перед экспортом и выходом очередь должна оказаться на диске.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            var lines = new List<(DateTime, string)>();
            while (Pending.TryDequeue(out var line)) lines.Add(line);
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) lines.Add((DateTime.Now, $"[Подробно] Очередь переполнена, пропущено записей: {dropped}"));
            LogStore.Append(lines);
        }
    }

    public static Operation Begin(string name, CancellationToken cancellation = default) => new(name, cancellation);

    public static string Service(string name) => name.Split(':')[0] switch
    {
        "ChatGPT" => "ChatGPT", "Claude" => "Claude", "Gemini" => "Gemini",
        "YouTube" => "YouTube", "Discord" => "Discord", "Telegram" => "Telegram",
        _ => "свой сервис",
    };

    public static async Task<T> RunAsync<T>(string name, Func<Task<T>> action, CancellationToken cancellation = default)
    {
        using var operation = Begin(name, cancellation);
        try { return await action().ConfigureAwait(false); }
        catch (Exception e) { operation.Fail(e); throw; }
    }

    /// <summary>Те же запросы и таймауты. В журнал идет только назначение запроса и числовой код, без адреса и ответа.</summary>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, HttpCompletionOption completion,
        CancellationToken ct, string purpose)
    {
        using var operation = Begin(purpose, ct);
        operation.Detail($"HTTP GET; таймаут={http.Timeout.TotalMilliseconds:F0} мс; режим={completion}");
        try
        {
            var response = await http.GetAsync(url, completion, ct).ConfigureAwait(false);
            operation.Detail($"HTTP {(int)response.StatusCode}; получены " +
                (completion == HttpCompletionOption.ResponseHeadersRead ? "заголовки" : "заголовки и тело"));
            return response;
        }
        catch (Exception e) { operation.Fail(e); throw; }
    }

    public static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct, string purpose)
    {
        using var response = await GetAsync(http, url, HttpCompletionOption.ResponseContentRead, ct, purpose).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Сообщение исключения может содержать ответ сервера или секрет. Сохраняем тип, коды и стек методов.</summary>
    public static string Error(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? e = exception; e != null && parts.Count < 8; e = e.InnerException)
        {
            var codes = e is SocketException socket ? $"; socket={socket.SocketErrorCode}" :
                e is HttpRequestException http ? $"; HTTP={(int?)http.StatusCode}; сеть={http.HttpRequestError}" : "";
            var frames = new StackTrace(e, false).GetFrames()?.Take(8)
                .Select(f => f.GetMethod()).Where(m => m != null)
                .Select(m => $"{m!.DeclaringType?.Name}.{m.Name}");
            parts.Add($"{e.GetType().Name}; HResult=0x{e.HResult:X8}{codes}; стек={string.Join(" > ", frames ?? [])}");
        }
        return string.Join("; причина: ", parts);
    }

    internal sealed class Operation : IDisposable
    {
        private readonly string _name;
        private readonly long _id;
        private readonly long? _parent;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly CancellationToken _cancellation;
        private string _outcome = "завершено";
        private bool _disposed;

        internal Operation(string name, CancellationToken cancellation)
        {
            _name = name;
            _cancellation = cancellation;
            _parent = CurrentOperation.Value;
            _id = Interlocked.Increment(ref _operations);
            CurrentOperation.Value = _id;
            Write($"Начало: {_name}; родитель={_parent?.ToString() ?? "нет"}");
        }

        public void Detail(string text) => Write($"{_name}: {text}");
        public void Fail(Exception exception)
        {
            _outcome = exception is OperationCanceledException ? "отменено или истекло время ожидания" : "ошибка";
            Detail(Error(exception));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_cancellation.IsCancellationRequested) _outcome = "отменено";
            Write($"Итог: {_name}; {_outcome}; длительность={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F0} мс");
            CurrentOperation.Value = _parent;
        }
    }
}
