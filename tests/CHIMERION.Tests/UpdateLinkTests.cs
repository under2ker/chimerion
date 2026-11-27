using Xunit;

namespace Dnex.Tests;

/// <summary>Окно обновления открывает только свои ссылки (аудит U-21).</summary>
public class UpdateLinkTests
{
    [Theory]
    [InlineData("https://github.com/under2ker/chimerion/releases/tag/v0.1.181", true)]
    [InlineData("https://github.com/under2ker/chimerion", true)]
    [InlineData("https://chimerion.dev/download", true)]
    [InlineData("https://ru.chimerion.dev/", true)]
    [InlineData("https://t.me/chimeriondns", true)]
    [InlineData("https://github.com/under2ker/chimerion-evil/releases", false)]
    [InlineData("https://github.com/someone/else", false)]
    [InlineData("https://chimerion.dev.evil.ru/", false)]
    [InlineData("https://evil-chimerion.dev/", false)]
    [InlineData("https://t.me/fake_channel", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows", false)]
    public void Only_own_links_open(string url, bool own) => Assert.Equal(own, UpdateWindow.IsOwnLink(url));
}
