using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>
/// Скачивание обновления при замедлении провайдером: сервер отдает первые 16 КБ соединения и замирает (так режут
/// Cloudflare, где лежат быстрые исправления). Загрузчик должен заметить это и докачать архив кусками.
/// </summary>
[Collection("scenarios")]
public class ThrottledDownloadTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Локальный сервер: обычный запрос режется после 16 КБ, запрос куска отдается целиком.</summary>
    private static (HttpListener Listener, string Url) Serve(byte[] content, bool throttle)
    {
        int port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
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
                        var range = ctx.Request.Headers["Range"];
                        ctx.Response.Headers["Accept-Ranges"] = "bytes";
                        if (range != null && range.StartsWith("bytes="))
                        {
                            var p = range[6..].Split('-');
                            long s = long.Parse(p[0]), e = Math.Min(long.Parse(p[1]), content.Length - 1);
                            ctx.Response.StatusCode = 206;
                            ctx.Response.Headers["Content-Range"] = $"bytes {s}-{e}/{content.Length}";
                            ctx.Response.ContentLength64 = e - s + 1;
                            await ctx.Response.OutputStream.WriteAsync(content.AsMemory((int)s, (int)(e - s + 1)));
                        }
                        else
                        {
                            ctx.Response.ContentLength64 = content.Length;
                            int first = throttle ? 16 * 1024 : content.Length;
                            await ctx.Response.OutputStream.WriteAsync(content.AsMemory(0, first));
                            await ctx.Response.OutputStream.FlushAsync();
                            if (throttle) await Task.Delay(TimeSpan.FromSeconds(30)); // соединение замерло
                        }
                        ctx.Response.Close();
                    }
                    catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
                });
            }
        });
        return (listener, $"http://127.0.0.1:{port}/CHIMERION-9.9.9-win-x64.zip");
    }

    private static async Task<byte[]> Download(string url, List<string> stages)
    {
        var path = Path.Combine(TestSetup.DataDir, $"dl-{Guid.NewGuid():N}.zip");
        try
        {
            var progress = new Progress<(double? Part, string Stage)>(p => { lock (stages) stages.Add(p.Stage); });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await Updater.DownloadAsync(url, path, progress, cts.Token);
            return await File.ReadAllBytesAsync(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Stalled_download_is_finished_in_chunks()
    {
        var content = new byte[300 * 1024];
        new Random(7).NextBytes(content);
        var (listener, url) = Serve(content, throttle: true);
        var (slowAfter, stallAfter) = (Updater.SlowAfter, Updater.StallAfter);
        try
        {
            Updater.SlowAfter = TimeSpan.FromSeconds(1);
            Updater.StallAfter = TimeSpan.FromSeconds(1);
            var stages = new List<string>();
            var got = await Download(url, stages);
            Assert.Equal(content, got);
            Assert.Contains(stages, s => s.Contains("кусками"));
        }
        finally { (Updater.SlowAfter, Updater.StallAfter) = (slowAfter, stallAfter); listener.Stop(); }
    }

    [Fact]
    public async Task Normal_download_does_not_use_chunks()
    {
        var content = new byte[120 * 1024];
        new Random(3).NextBytes(content);
        var (listener, url) = Serve(content, throttle: false);
        try
        {
            var stages = new List<string>();
            Assert.Equal(content, await Download(url, stages));
            Assert.DoesNotContain(stages, s => s.Contains("кусками"));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Update_hosts_are_always_bypassed_by_zapret()
    {
        Assert.Contains("r2.dev", Dnex.Services.Tools.ZapretTool.UpdateHosts);
        Assert.Contains("github.com", Dnex.Services.Tools.ZapretTool.UpdateHosts);
        Assert.Equal(Uri.UriSchemeHttps, new Uri(HotfixChannel.BaseUrl).Scheme);
        Assert.EndsWith(".r2.dev", new Uri(HotfixChannel.BaseUrl).Host); // канал обновлений под доменом из списка обхода
    }

    [Fact]
    public async Task Storage_root_domain_is_explained_not_blamed_on_provider()
    {
        var r = await SiteCheck.CheckAsync("r2.dev");
        Assert.Equal(SiteVerdict.Error, r.Verdict);
        Assert.Contains("pub-", r.Advice);
    }
}
