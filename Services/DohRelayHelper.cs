using System.IO;
using System.Net.Http;

namespace Dnex.Services;

/// <summary>Точка входа отдельного процесса посредника без окна и без прав администратора.</summary>
internal static class DohRelayHelper
{
    internal static int Run(string? channel)
    {
        // Задание передает канал: процесс запускается только для своих настроек. Старое задание без канала
        // или с чужим каналом не должно поднимать посредник с чужим адресом DoH.
        if (!DohRelayConfig.IsChannel(channel)) return 1;
        DohRelayConfig? config;
        // Отсутствующий файл не создаем: Load вернет null, и пустые настройки в папке данных не появятся.
        try { config = DohRelayConfig.Load(channel!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
        if (config is null) return 1;
        DohRelayConfig saved = config;
        try
        {
            var (endpoint, _, upstream) = config.Decode();
            // Сервер мог сменить IP после подключения: новые адреса узнаются у эталона и сохраняются для следующего запуска.
            var addresses = new DohRelayAddresses(upstream,
                ct => DohRelayAddresses.ResolveAsync(endpoint.Host, async (reference, host, token) =>
                    (await DnsWire.ResolveDohAsync(reference, host, TimeSpan.FromSeconds(6), token).ConfigureAwait(false)).Ips, ct),
                fresh =>
                {
                    saved.BootstrapAddresses = fresh.Select(ip => ip.ToString()).ToList();
                    saved.Save();
                });
            using HttpClient client = DohRelayHttp.Create(addresses);
            DohRelayHost.RunAsync(config, client, 53, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // Сохраняем только класс ошибки: адрес сервера и содержимое запросов в файл не попадают.
            try { Storage.SaveRequired(DohRelayConfig.ErrorFileNameFor(channel!), new { At = DateTime.UtcNow, Kind = ex.GetType().Name }); }
            catch (Exception) { }
            return 1;
        }
    }
}
