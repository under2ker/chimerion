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
            try { Storage.SaveRequired(DohRelayConfig.ErrorFileNameFor(channel!), new { At = DateTime.UtcNow, Kind = ex.GetType().Name }); }
            catch (Exception) { }
            return 1;
        }
    }
}
