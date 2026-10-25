using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace CHIMERION.Tests;

/// <summary>Доступ к ИИ-сервису засчитывается только по ожидаемому ответу API.</summary>
public class ServiceAccessTests
{
    private static CheckTarget ChatGpt() => new()
    {
        Name = "ChatGPT", Host = "api.openai.com", Path = "/v1/models",
        BlockedMarkers = { "unsupported_country" }, OkStatusCodes = { 200, 401 },
    };

    [Theory]
    [InlineData(401, "{\"error\":\"Missing bearer authentication\"}")] // API ответил, ключа нет: сервис доступен
    [InlineData(200, "{\"data\":[]}")]
    public void ExpectedResponse_IsOpen(int code, string body)
    {
        Assert.Equal(ServiceAccess.Result.Open, ServiceAccess.Classify(ChatGpt(), code, body));
    }

    [Fact]
    public void RegionMarker_IsClosed_EvenOnOkCode()
    {
        Assert.Equal(ServiceAccess.Result.Closed,
            ServiceAccess.Classify(ChatGpt(), 403, "{\"code\":\"unsupported_country_region_territory\"}"));
    }

    [Theory]
    [InlineData(403)] // страница защиты: не сказано, что доступ закрыт, но и открыт не доказано
    [InlineData(404)]
    public void OtherClientErrors_AreNotOpen(int code)
    {
        Assert.NotEqual(ServiceAccess.Result.Open, ServiceAccess.Classify(ChatGpt(), code, "<html>Access denied</html>"));
    }

    [Theory]
    [InlineData(302)] // редирект на страницу блокировки не считается доступом
    [InlineData(500)]
    [InlineData(503)]
    public void RedirectAndServerErrors_AreErrorNotOpen(int code)
    {
        Assert.Equal(ServiceAccess.Result.Error, ServiceAccess.Classify(ChatGpt(), code, string.Empty));
    }

    [Fact]
    public void ExplicitBlockedCode_IsClosed()
    {
        var claude = new CheckTarget { Name = "Claude", Host = "api.anthropic.com", Path = "/v1/models", BlockedStatusCodes = { 403 } };
        Assert.Equal(ServiceAccess.Result.Closed, ServiceAccess.Classify(claude, 403, string.Empty));
    }
}
