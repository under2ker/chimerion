using System.Net;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>
/// Адреса DoH-сервера для посредника. Они узнаются при подключении, но сервер может сменить IP: тогда посредник
/// переставал работать до переподключения. Если не ответил ни один адрес, посредник заново спрашивает их у эталонного
/// DoH по IP-адресу (системный DNS сейчас указывает на сам посредник), не чаще раза в минуту.
/// </summary>
internal sealed class DohRelayAddresses
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<IPAddress>>>? _resolve;
    private readonly Action<IPAddress[]>? _changed;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IPAddress[] _current;
    private DateTime _lastRefresh = DateTime.MinValue;

    /// <summary>Как часто можно спрашивать эталон: частые фоновые запросы вредят (правило проекта).</summary>
    internal TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(1);
    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    internal DohRelayAddresses(IEnumerable<IPAddress> initial,
        Func<CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolve = null, Action<IPAddress[]>? changed = null)
    {
        // Исходные адреса уже проверены при подключении (и в тестах бывают локальными): фильтруются только ответы эталона.
        _current = initial.Distinct().ToArray();
        if (_current.Length == 0) throw new ArgumentException("Нет адресов DoH-сервера", nameof(initial));
        _resolve = resolve;
        _changed = changed;
    }

    internal IReadOnlyList<IPAddress> Current => Volatile.Read(ref _current);

    /// <summary>Спрашивает адреса заново. True, если список изменился и стоит повторить подключение.</summary>
    internal async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_resolve is null) return false;
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = Now();
            if (now - _lastRefresh < MinInterval) return false;
            _lastRefresh = now;
            IPAddress[] fresh;
            try { fresh = Usable(await _resolve(cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return false; }
            var old = Current;
            if (fresh.Length == 0 || (fresh.Length == old.Count && fresh.All(old.Contains))) return false;
            Volatile.Write(ref _current, fresh);
            try { _changed?.Invoke(fresh); }
            catch (Exception) { /* сохранить не вышло: адреса все равно действуют до перезапуска */ }
            return true;
        }
        finally { _refresh.Release(); }
    }

    private static IPAddress[] Usable(IEnumerable<IPAddress> addresses) => addresses
        .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any))
        .Distinct().Take(4).ToArray();

    /// <summary>Эталоны с IP в адресе: имя вроде dns.google пришлось бы разрешать через сам посредник.</summary>
    internal static readonly string[] References = { new Models.AppSettings().ReferenceDoh, "https://8.8.8.8/dns-query", "https://1.1.1.1/dns-query" };

    /// <summary>Адреса имени через первый ответивший эталон, только IPv4 (как при подключении).</summary>
    internal static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host,
        Func<string, string, CancellationToken, Task<List<IPAddress>>> query, CancellationToken cancellationToken)
    {
        foreach (var reference in References.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out _)) continue;
            try
            {
                var ips = (await query(reference, host, cancellationToken).ConfigureAwait(false))
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Take(2).ToList();
                if (ips.Count > 0) return ips;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
        }
        return Array.Empty<IPAddress>();
    }
}
