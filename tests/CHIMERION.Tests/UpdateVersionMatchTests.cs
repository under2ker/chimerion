using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Сверка версии exe в архиве обновления (аудит U-09).</summary>
public class UpdateVersionMatchTests
{
    [Theory]
    [InlineData("0.1.180", "0.1.180", true)]
    [InlineData("0.1.180+3f2a9c1d", "0.1.180", true)]
    [InlineData("0.1.180.0", "0.1.180", true)]
    [InlineData("0.1.18", "0.1.180", false)]
    [InlineData("0.1.180", "0.1.18", false)]
    [InlineData("0.1.1800", "0.1.180", false)]
    [InlineData("", "0.1.180", false)]
    [InlineData(null, "0.1.180", false)]
    [InlineData("мусор", "0.1.180", false)]
    public void Version_must_match_exactly(string? file, string expected, bool same) =>
        Assert.Equal(same, Updater.SameVersion(file, expected));

    [Fact]
    public void Own_exe_matches_its_version()
    {
        // Настоящая сборка: ProductVersion программы проходит сверку со своим номером
        var exe = typeof(Updater).Assembly.Location;
        var pv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        Assert.True(Updater.SameVersion(pv, UpdateChecker.CurrentVersion.ToString(3)), pv);
    }
}
