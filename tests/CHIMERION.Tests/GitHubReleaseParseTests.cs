using System.Net;
using System.Net.Http;
using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

/// <summary>Вместо ответа GitHub пришла страница-заглушка: понятная ошибка, а не сбой (аудит T-11).</summary>
[Collection("scenarios")]
public class GitHubReleaseParseTests
{
    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData("<html><body>Доступ ограничен</body></html>")]
    [InlineData("{\"message\":\"ok\"}")]
    public async Task Stub_page_gives_clear_tool_error(string body)
    {
        var old = GitHubRelease.Api;
        GitHubRelease.Api = new HttpClient(new Reply(body));
        try
        {
            var e = await Assert.ThrowsAsync<ToolException>(() => GitHubRelease.LatestAsync("owner/repo", CancellationToken.None));
            Assert.Contains("GitHub ответил не тем", e.Message);
        }
        finally { GitHubRelease.Api = old; }
    }

    [Fact]
    public async Task Normal_release_still_parses()
    {
        var old = GitHubRelease.Api;
        GitHubRelease.Api = new HttpClient(new Reply("{\"tag_name\":\"v1.2.3\",\"assets\":[{\"name\":\"a.zip\",\"browser_download_url\":\"https://example.test/a.zip\"}]}"));
        try
        {
            var r = await GitHubRelease.LatestAsync("owner/repo", CancellationToken.None);
            Assert.Single(r.Assets);
        }
        finally { GitHubRelease.Api = old; }
    }
}
