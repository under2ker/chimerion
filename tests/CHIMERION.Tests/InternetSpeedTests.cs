using System.Net;
using System.Net.Http;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("speed")] // тесты меняют общие настройки замера
public class InternetSpeedTests
{
    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Hosts) Hosts.Add(request.RequestUri!.Host);
            if (request.Content != null) await request.Content.CopyToAsync(Stream.Null, ct);
            return reply(request);
        }
    }

    private static HttpResponseMessage Bytes(int n) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[n]) };

    private static async Task<(InternetResult Result, Fake Fake)> Run(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var fake = new Fake(reply);
        InternetSpeed.Handler = fake;
        var (connect, resolve) = (InternetSpeed.Connect, InternetSpeed.Resolve);
        InternetSpeed.Connect = (_, _) => Task.FromResult(12L);
        InternetSpeed.Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
        (InternetSpeed.PilotTime, InternetSpeed.DownTime, InternetSpeed.UpTime, InternetSpeed.WarmUp) = (TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
        try { return (await InternetSpeed.RunAsync(null, CancellationToken.None), fake); }
        finally
        {
            InternetSpeed.Handler = null;
            (InternetSpeed.Connect, InternetSpeed.Resolve) = (connect, resolve);
            (InternetSpeed.PilotTime, InternetSpeed.DownTime, InternetSpeed.UpTime, InternetSpeed.WarmUp) = (TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1.5));
        }
    }

    [Theory]
    [InlineData(12_500_000, 1, 100)]
    [InlineData(125_000_000, 2, 500)]
    [InlineData(1000, 0, 0)]
    public void Megabits_are_counted_from_bytes_and_time(long bytes, double seconds, double mbit) =>
        Assert.Equal(mbit, InternetSpeed.Mbit(bytes, seconds));

    [Fact]
    public void Ping_median_and_jitter()
    {
        var (median, jitter) = InternetSpeed.PingStats(new long[] { 20, 30, 20, 40 });
        Assert.Equal(20, median);
        Assert.Equal(13, jitter); // (10 + 10 + 20) / 3
        Assert.Equal((5L, 0L), InternetSpeed.PingStats(new long[] { 5 }));
    }

    [Fact]
    public async Task Good_primary_server_gives_full_result()
    {
        var (r, _) = await Run(_ => Bytes(2_000_000));
        Assert.Equal("Cloudflare", r.Source);
        Assert.True(r.DownMbit > 0);
        Assert.True(r.UpMbit > 0);
        Assert.Equal(12, r.PingMs);
        Assert.Equal(0, r.JitterMs);
    }

    [Fact]
    public async Task Blocked_primary_falls_back_and_upload_still_goes_to_primary()
    {
        var (r, fake) = await Run(q => q.RequestUri!.Host == "speed.cloudflare.com" && q.Method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Bytes(2_000_000));
        Assert.Equal("Selectel", r.Source);
        Assert.True(r.DownMbit > 0);
        Assert.Contains("speedtest.selectel.ru", fake.Hosts);
    }

    [Fact]
    public async Task Nothing_reachable_gives_empty_result_not_a_crash()
    {
        var (r, _) = await Run(_ => throw new HttpRequestException("нет сети"));
        Assert.Null(r.Source);
        Assert.Null(r.DownMbit);
    }

    [Fact]
    public async Task Cancel_stops_the_test()
    {
        InternetSpeed.Handler = new Fake(_ => Bytes(1000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InternetSpeed.RunAsync(null, cts.Token)); }
        finally { InternetSpeed.Handler = null; }
    }
}

public class SpeedVerdictTests
{
    [Fact]
    public void Fast_connection_fits_everything()
    {
        var uses = SpeedVerdict.For(new InternetResult("Cloudflare", 18, 3, 133.6, 47.2));
        Assert.All(uses, u => Assert.True(u.Ok, u.Title));
    }

    [Fact]
    public void Slow_upload_and_high_ping_are_called_out()
    {
        var uses = SpeedVerdict.For(new InternetResult("Selectel", 180, 40, 30, 1.5)).ToDictionary(u => u.Title, u => u.Ok);
        Assert.True(uses["Видео в 4K"]);
        Assert.False(uses["Видеозвонки"]);
        Assert.False(uses["Онлайн-игры"]);
        Assert.False(uses["Отправка больших файлов"]);
    }

    [Fact]
    public void Failed_test_fits_nothing() =>
        Assert.All(SpeedVerdict.For(new InternetResult(null, null, null, null, null)), u => Assert.False(u.Ok));

    [Theory]
    [InlineData("быстрый", "Ok")]
    [InlineData("медленный", "Warn")]
    [InlineData("нет ответов", "Bad")]
    public void Dns_grade_colors(string grade, string color) => Assert.Equal(color, SpeedVerdict.DnsColor(grade));
}
