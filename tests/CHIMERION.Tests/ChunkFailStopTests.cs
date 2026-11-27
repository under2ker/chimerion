using System.Net;
using System.Net.Sockets;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Кусок обновления окончательно не скачался: остальные куски перестают качаться (аудит U-20).</summary>
[Collection("scenarios")]
public class ChunkFailStopTests
{
    [Fact]
    public async Task Final_chunk_failure_stops_other_chunks_and_reports_real_error()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int served = 0;
        const int chunks = 60;
        long total = (long)Updater.ChunkSize * chunks;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var p = ctx.Request.Headers["Range"]![6..].Split('-');
                        long s = long.Parse(p[0]), e = long.Parse(p[1]);
                        if (s == 0) { ctx.Response.StatusCode = 500; ctx.Response.Close(); return; } // первый кусок всегда ломается
                        await Task.Delay(2000);
                        ctx.Response.StatusCode = 206;
                        ctx.Response.Headers["Content-Range"] = $"bytes {s}-{e}/{total}";
                        ctx.Response.ContentLength64 = e - s + 1;
                        await ctx.Response.OutputStream.WriteAsync(new byte[e - s + 1]);
                        ctx.Response.Close();
                        Interlocked.Increment(ref served);
                    }
                    catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
                });
            }
        });
        try
        {
            using var dst = new MemoryStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var error = await Assert.ThrowsAsync<UpdateException>(() =>
                Updater.DownloadChunksAsync($"http://127.0.0.1:{port}/a.zip", dst, 0, total, _ => { }, cts.Token));
            Assert.Contains("500", error.Message);
            await Task.Delay(2500); // уже начатые куски дотекают
            Assert.True(served < 30, $"скачано кусков после ошибки: {served} из {chunks - 1}");
            Assert.Equal(0, dst.Length);
        }
        finally { listener.Stop(); }
    }
}
