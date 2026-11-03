using System.IO;
using System.Net.Http;

namespace Dnex.Services;

/// <summary>Точка входа отдельного процесса посредника без окна и без прав администратора.</summary>
internal static class DohRelayHelper
{
    internal static int Run(string? channel)
    {
        DohRelayConfig? config = null;
        // Отсутствующий файл не создаем: LoadOrCreate записал бы null, и следующий запуск
        // не отличил бы пустые настройки от настоящих.
        if (channel is not null && File.Exists(Storage.PathOf(DohRelayConfig.FileName)))
        {
            try { config = DohRelayConfig.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 1; }
        }
        // Задание передает канал: процесс запускается только для своих настроек. Старое задание без канала
        // не должно поднимать посредник с чужим адресом DoH.
        if (config is null || !string.Equals(channel, config.Channel, StringComparison.Ordinal)) return 1;
        try
        {
            var (_, _, upstream) = config.Decode();
            using HttpClient client = DohRelayHttp.Create(upstream);
            DohRelayHost.RunAsync(config, client, 53, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            // Сохраняем только класс ошибки: адрес сервера и содержимое запросов в файл не попадают.
            try { Storage.SaveRequired("doh-relay-error.json", new { At = DateTime.UtcNow, Kind = ex.GetType().Name }); }
            catch (Exception) { }
            return 1;
        }
    }
}
