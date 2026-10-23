using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>
/// Правила доступа на ответах, снятых с живых сервисов: напрямую из закрытого региона и через прокси Smart DNS.
/// Настройки сервисов берутся из значений по умолчанию, как у нового пользователя.
/// </summary>
public class ServiceAccessLiveTests
{
    private static CheckTarget Target(string name) => new AppSettings().Targets.Single(t => t.Name == name);
    private static CheckTarget Site(string host) => new() { Name = host, Host = host, Path = "/" };

    // ---------- API ----------

    [Fact]
    public void ChatGpt_Direct_IsClosed_Proxy_IsOpen()
    {
        var t = Target("ChatGPT");
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.Classify(t, 403,
            "{\"error\":{\"code\":\"unsupported_country_region_territory\",\"message\":\"Country, region, or territory not supported\",\"type\":\"request_forbidden\"}}"));
        Assert.Equal(ServiceAccess.Result.Open, ServiceAccess.Classify(t, 401,
            "{\"error\":{\"message\":\"Missing bearer authentication in header\",\"type\":\"invalid_request_error\"}}"));
    }

    [Fact]
    public void Claude_Direct_IsClosed_Proxy_IsOpen()
    {
        var t = Target("Claude");
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.Classify(t, 403,
            "{\"error\":{\"type\":\"forbidden\",\"message\":\"Request not allowed\"}}"));
        Assert.Equal(ServiceAccess.Result.Open, ServiceAccess.Classify(t, 401,
            "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"x-api-key header is required\"}}"));
    }

    [Fact]
    public void Gemini_RealGoogleAnswer_IsOpen_AndNeedsProxy()
    {
        var t = Target("Gemini");
        Assert.True(t.RequiresProxy); // без ключа ответ одинаковый из закрытого региона и через прокси
        Assert.Equal(ServiceAccess.Result.Open, ServiceAccess.Classify(t, 403,
            "{\"error\":{\"code\":403,\"message\":\"Method doesn't allow unregistered callers\",\"status\":\"PERMISSION_DENIED\"}}"));
    }

    [Theory]
    [InlineData("ChatGPT", 401, "<html>401 Authorization Required</html>")] // чужой прокси со своей страницей
    [InlineData("Claude", 401, "")]
    [InlineData("Gemini", 403, "<html>403 Forbidden nginx</html>")]
    public void OkCode_WithoutServiceSignature_IsNotOpen(string name, int code, string body)
    {
        Assert.Equal(ServiceAccess.Result.Error, ServiceAccess.Classify(Target(name), code, body));
    }

    // ---------- Сайты ----------

    [Fact]
    public void ChatGptSite_Challenge_IsOpen_Plain403_IsClosed()
    {
        Assert.Equal(ServiceAccess.Result.Open, ServiceAccess.ClassifyWeb(Site("chatgpt.com"), 403,
            "<script src=\"/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1\"></script>"));
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.ClassifyWeb(Site("chatgpt.com"), 403, "<html>Access denied</html>"));
    }

    [Fact]
    public void ClaudeSite_RegionRedirect_IsClosed()
    {
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.ClassifyWeb(Site("claude.ai"), 302, "",
            "https://claude.com/app-unavailable-in-region"));
    }

    [Fact]
    public void GeminiSite_GoogleCaptcha_IsClosed()
    {
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.ClassifyWeb(Site("gemini.google.com"), 302, "",
            "https://www.google.com/sorry/index?continue=https://gemini.google.com/app"));
    }

    [Theory]
    [InlineData("accounts.google.com", "https://accounts.google.com/ServiceLogin?passive=1209600", ServiceAccess.Result.Open)]
    [InlineData("claude.ai", "/login", ServiceAccess.Result.Open)]                        // относительный адрес: тот же сайт
    [InlineData("console.anthropic.com", "https://platform.claude.com/", ServiceAccess.Result.Error)] // ушел на чужой сайт
    public void SiteRedirect_DependsOnTarget(string host, string location, ServiceAccess.Result expected)
    {
        Assert.Equal(expected, ServiceAccess.ClassifyWeb(Site(host), 302, "", location));
    }

    [Fact]
    public void SiteRedirect_WithoutLocation_IsNotOpen()
    {
        Assert.Equal(ServiceAccess.Result.Error, ServiceAccess.ClassifyWeb(Site("chatgpt.com"), 302, ""));
    }

    [Fact]
    public void Site5xx_IsError()
    {
        Assert.Equal(ServiceAccess.Result.Error, ServiceAccess.ClassifyWeb(Site("chatgpt.com"), 502, ""));
    }
}
