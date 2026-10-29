using Dnex;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class PrivacySanitizationTests
{
    [Theory]
    [InlineData("Authorization: Bearer secretvalue", "secretvalue")]
    [InlineData("Cookie: session=secretvalue; theme=dark", "session=secretvalue")]
    [InlineData("password=secretvalue", "secretvalue")]
    [InlineData("{\"access_token\":\"secretvalue\"}", "secretvalue")]
    [InlineData("https://example.test/?api_key=secretvalue&x=1", "secretvalue")]
    [InlineData("server 203.0.113.27", "203.0.113.27")]
    [InlineData("peer 2001:db8::1", "2001:db8::1")]
    [InlineData("city: Samplecity", "Samplecity")]
    public void Sensitive_values_are_removed_from_report_and_log(string input, string secret)
    {
        Assert.DoesNotContain(secret, Diagnostics.Sanitize(input, null));
        Assert.DoesNotContain(secret, PrivacyGuard.Scrub(input));
    }
}
