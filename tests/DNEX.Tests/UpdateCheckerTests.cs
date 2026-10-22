using Dnex.Services;
using Xunit;

namespace DNEX.Tests;

/// <summary>Разбор ответа GitHub API о последнем релизе: когда показывать уведомление, а когда нет.</summary>
public class UpdateCheckerTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false, string page = "https://github.com/under2ker/dnex-dns-hub/releases/tag/x") =>
        $@"{{ ""tag_name"": ""{tag}"", ""draft"": {draft.ToString().ToLowerInvariant()}, ""prerelease"": {prerelease.ToString().ToLowerInvariant()},
            ""html_url"": ""{page}"", ""body"": ""## Что нового\n- первое\n- второе"" }}";

    [Fact]
    public void NewerRelease_ReturnsVersionNotesAndPage()
    {
        var info = UpdateChecker.Parse(Release("v0.2.0"), new Version(0, 1, 15));
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 2, 0), info!.Version);
        Assert.Contains("первое", info.Notes);
        Assert.StartsWith("https://github.com/", info.PageUrl);
    }

    [Fact]
    public void SameOrOlderRelease_ReturnsNull()
    {
        Assert.Null(UpdateChecker.Parse(Release("v0.1.15"), new Version(0, 1, 15)));
        Assert.Null(UpdateChecker.Parse(Release("v0.1.11"), new Version(0, 1, 15)));
    }

    [Fact]
    public void DraftAndPrerelease_AreIgnored()
    {
        Assert.Null(UpdateChecker.Parse(Release("v0.2.0", draft: true), new Version(0, 1, 15)));
        Assert.Null(UpdateChecker.Parse(Release("v0.2.0", prerelease: true), new Version(0, 1, 15)));
    }

    [Fact]
    public void BadTag_ReturnsNull()
    {
        Assert.Null(UpdateChecker.Parse(Release("latest"), new Version(0, 1, 15)));
    }

    [Fact]
    public void NonGitHubPage_FallsBackToReleasesPage()
    {
        var info = UpdateChecker.Parse(Release("v0.2.0", page: "https://example.com/evil"), new Version(0, 1, 15));
        Assert.NotNull(info);
        Assert.Equal("https://github.com/under2ker/dnex-dns-hub/releases/latest", info!.PageUrl);
    }
}
