using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Предел размера скачиваемого обновления (аудит U-12): с длиной в заголовке и без нее.</summary>
[Collection("scenarios")]
public class UpdateSizeLimitTests
{
    private static (HttpListener Listener, string Url) Serve(int size, bool chunked)
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch (Exception) { return; }
                try
                {
                    if (chunked) ctx.Response.SendChunked = true; else ctx.Response.ContentLength64 = size;
                    var buf = new byte[8192];
                    for (int sent = 0; sent < size; sent += buf.Length)
                        await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, Math.Min(buf.Length, size - sent)));
                    ctx.Response.Close();
                }
                catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
            }
        });
        return (listener, $"http://127.0.0.1:{port}/CHIMERION-9.9.9-win-x64.zip");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Too_big_download_is_refused(bool chunked)
    {
        var prev = Updater.MaxDownloadBytes;
        Updater.MaxDownloadBytes = 100_000;
        var (listener, url) = Serve(400_000, chunked);
        var path = Path.Combine(TestSetup.DataDir, $"big-{Guid.NewGuid():N}.zip");
        try
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                Updater.DownloadAsync(url, path, new Progress<(double? Part, string Stage)>(_ => { }), new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token));
            Assert.Contains("слишком большой", error.Message);
        }
        finally { Updater.MaxDownloadBytes = prev; listener.Stop(); try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task Normal_download_still_works()
    {
        var (listener, url) = Serve(300_000, chunked: true);
        var path = Path.Combine(TestSetup.DataDir, $"ok-{Guid.NewGuid():N}.zip");
        try
        {
            await Updater.DownloadAsync(url, path, new Progress<(double? Part, string Stage)>(_ => { }), new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            Assert.Equal(300_000, new FileInfo(path).Length);
        }
        finally { listener.Stop(); try { File.Delete(path); } catch (IOException) { } }
    }
}
