using System.IO;
using System.Net.Http;

namespace Dnex.Services;

/// <summary>Точка входа отдельного процесса посредника без окна и без прав администратора.</summary>
internal static class DohRelayHelper
{
    internal static int Run()
    {
        try
        {
            var config = DohRelayConfig.Load() ?? throw new InvalidOperationException("Нет настроек посредника");
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
