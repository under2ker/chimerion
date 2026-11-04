using System.IO;
using System.Net;
using System.Net.Http;

namespace Dnex.Services;

/// <summary>Работающий DoH-прокси: локальный адрес, который стоит в DNS адаптеров.</summary>
internal sealed record DohProxySession(string ListenAddress, int Port);

/// <summary>
/// DoH-прокси внутри процесса CHIMERION для Windows без системного DoH (Windows 10). Принимает DNS на 127.0.0.x:53
/// (UDP и TCP) и пересылает каждый запрос на DoH-сервер по заранее известным IP: имя сервера через системный DNS
/// не узнать, системный DNS — это и есть прокси. Без Планировщика, отдельного процесса и канала связи: прежний
/// посредник ломался на каждом из этих звеньев (права, задания, таймауты). Пока программа закрыта, DNS идет через
/// прежний DNS сети, который стоит на адаптерах после прокси.
/// </summary>
internal static class DohLocalProxy
{
    /// <summary>
    /// Адреса по порядку. 127.0.0.1:53 часто занят локальными DNS-серверами; при смене сервера прежний прокси еще
    /// работает, поэтому новый берет следующий свободный адрес. Те же адреса, что у прежнего посредника.
    /// </summary>
    internal static readonly string[] CandidateAddresses = { "127.0.0.2", "127.0.0.3", "127.0.0.4", "127.0.0.5" };
    /// <summary>Порт: DNS Windows ходит только на 53. Тесты ставят 0 (любой свободный).</summary>
    internal static int Port = 53;
    /// <summary>Пробный запрос до смены DNS: если DoH не отвечает, подключаться нельзя.</summary>
    internal static TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    internal const string ProbeHost = "chatgpt.com";

    /// <summary>HTTPS-клиент к DoH-серверу. Тесты подставляют свой, без настоящего сервера.</summary>
    internal static Func<DohRelayAddresses, HttpClient> CreateHttp = addresses => DohRelayHttp.Create(addresses);

    private sealed record Running(DohLocalListener Listener, HttpClient Http, Uri Endpoint, string Address);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, Running> Active = new();

    /// <summary>Работает ли прокси на этом адресе.</summary>
    internal static bool IsRunning(string address)
    {
        lock (Active) return Active.TryGetValue(address, out var r) && !r.Listener.Completion.IsCompleted;
    }

    /// <summary>Адреса работающих прокси (для диагностики).</summary>
    internal static IReadOnlyList<string> RunningAddresses()
    {
        lock (Active) return Active.Keys.ToList();
    }

    /// <summary>
    /// Запуск прокси для DoH-адреса. preferred — адрес из записи о подключении (после перезапуска программы):
    /// DNS адаптеров уже стоит на нем. Без preferred берется первый свободный. Прокси с тем же DoH-адресом
    /// переиспользуется. Ошибки — DohRelayFailedException с понятной причиной.
    /// </summary>
    internal static async Task<DohProxySession> StartAsync(Uri endpoint, IReadOnlyList<IPAddress> upstream, string? preferred, Action<string> log)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Некорректный адрес DoH", nameof(endpoint));
        if (upstream.Count == 0) throw new DohRelayFailedException("Не известен IP DoH-сервера");
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (Active)
            {
                var same = Active.Values.FirstOrDefault(r => r.Endpoint == endpoint && !r.Listener.Completion.IsCompleted
                                                             && (preferred == null || r.Address == preferred));
                if (same != null) return new DohProxySession(same.Address, same.Listener.Port);
            }
            var host = endpoint.Host;
            var addresses = new DohRelayAddresses(upstream, ct => DohRelayAddresses.ResolveAsync(host, async (reference, name, token) =>
                (await DnsWire.ResolveDohAsync(reference, name, TimeSpan.FromSeconds(6), token).ConfigureAwait(false)).Ips, ct));
            var http = CreateHttp(addresses);
            try
            {
                var transport = new DohRelayTransport(http, endpoint);
                // Новое подключение — сначала пробный запрос. Подъем после перезапуска программы (preferred) без него:
                // сеть после загрузки появляется не сразу, а DNS адаптеров уже стоит на прокси
                if (preferred == null) await ProbeAsync(transport).ConfigureAwait(false);
                var busy = new List<string>();
                foreach (var candidate in preferred != null ? new[] { preferred } : CandidateAddresses)
                {
                    lock (Active) if (Active.ContainsKey(candidate)) { busy.Add(candidate + " (прежнее подключение)"); continue; }
                    DohLocalListener listener;
                    try { listener = new DohLocalListener(IPAddress.Parse(candidate), Port, transport); }
                    catch (System.Net.Sockets.SocketException ex) { busy.Add($"{candidate} ({ex.SocketErrorCode})"); continue; }
                    listener.Start();
                    lock (Active) Active[candidate] = new Running(listener, http, endpoint, candidate);
                    log($"DoH-прокси программы запущен на {candidate}");
                    return new DohProxySession(candidate, listener.Port);
                }
                throw new DohRelayFailedException("Локальный адрес для DoH-прокси занят другой программой: " + string.Join(", ", busy));
            }
            catch
            {
                http.Dispose();
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Запрос через DoH-сервер до смены DNS. Отказ — DohRelayFailedException с причиной без адреса и токена.</summary>
    private static async Task ProbeAsync(DohRelayTransport transport)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        var query = DnsWire.BuildQuery(ProbeHost, (ushort)Random.Shared.Next(1, 65535));
        try
        {
            var answer = await transport.ForwardAsync(query, timeout.Token).ConfigureAwait(false);
            int rcode = answer[3] & 0x0F;
            if (rcode is not (0 or 3)) throw new DohRelayFailedException($"DoH-сервер ответил ошибкой (код {rcode})");
        }
        catch (OperationCanceledException) { throw new DohRelayFailedException($"DoH-сервер не ответил за {ProbeTimeout.TotalSeconds:0} с"); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            throw new DohRelayFailedException("DoH-сервер не ответил: " + ex.Message);
        }
    }

    /// <summary>Остановка прокси на адресе. Неизвестный адрес — ничего не делает.</summary>
    internal static async Task StopAsync(string address, Action<string> log)
    {
        Running? running;
        lock (Active)
        {
            if (!Active.Remove(address, out running)) return;
        }
        try { await running.Listener.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* прием уже остановлен */ }
        running.Http.Dispose();
        log($"DoH-прокси программы на {address} остановлен");
    }

    /// <summary>Остановка всех прокси (выход из программы, тесты).</summary>
    internal static async Task StopAllAsync(Action<string> log)
    {
        foreach (var address in RunningAddresses()) await StopAsync(address, log).ConfigureAwait(false);
    }
}
