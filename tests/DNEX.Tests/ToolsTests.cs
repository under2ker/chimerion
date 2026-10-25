using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

public class ToolsTests
{
    // Начало стратегии из zapret-discord-youtube 1.10.4 (general (FAKE TLS AUTO).bat), сокращено
    private const string Bat = "@echo off\r\nchcp 65001 > nul\r\ncd /d \"%~dp0\"\r\ncall service.bat status_zapret\r\n" +
        "set \"BIN=%~dp0bin\\\"\r\nset \"LISTS=%~dp0lists\\\"\r\ncd /d %BIN%\r\n\r\n" +
        "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^\r\n" +
        "--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --dpi-desync-fake-tls=^! --dpi-desync-fake-quic=\"%BIN%quic_initial_www_google_com.bin\"\r\n";

    [Fact]
    public void Strategy_args_are_taken_from_bat_with_paths_and_escapes()
    {
        var a = ZapretTool.ParseArgs(Bat, @"C:\Z\bin\", @"C:\Z\lists\", games: false)!;
        Assert.Equal("--wf-tcp=80,443,12 --wf-udp=443,12  --filter-tcp=443 --hostlist=\"C:\\Z\\lists\\list-google.txt\" " +
                     "--dpi-desync-fake-tls=! --dpi-desync-fake-quic=\"C:\\Z\\bin\\quic_initial_www_google_com.bin\"", a);
    }

    [Fact]
    public void Game_filter_opens_high_ports()
    {
        var a = ZapretTool.ParseArgs(Bat, @"C:\Z\bin\", @"C:\Z\lists\", games: true)!;
        Assert.StartsWith("--wf-tcp=80,443,1024-65535 --wf-udp=443,1024-65535", a);
    }

    [Fact]
    public void Bat_without_winws_gives_null() =>
        Assert.Null(ZapretTool.ParseArgs("@echo off\r\necho hi\r\n", @"C:\Z\bin\", @"C:\Z\lists\", false));

    [Theory]
    [InlineData("general.bat", "Основная")]
    [InlineData("general (ALT2).bat", "ALT2")]
    [InlineData("general (FAKE TLS AUTO ALT3).bat", "FAKE TLS AUTO ALT3")]
    public void Strategy_titles(string file, string title) => Assert.Equal(title, ZapretTool.StrategyTitle(file));

    [Theory]
    [InlineData("v1.11.2", "1.11.2")]
    [InlineData("1.10.4", "1.10.4")]
    [InlineData("v2.0-beta", "2.0")]
    public void Release_tags(string tag, string version) => Assert.Equal(Version.Parse(version), GitHubRelease.ParseTag(tag));

    [Fact]
    public void Telegram_link_is_built_like_the_proxy_does()
    {
        Assert.Equal("tg://proxy?server=127.0.0.1&port=1443&secret=dd0123abcd",
            TgProxyTool.ProxyLinkFrom("{\"host\":\"127.0.0.1\",\"port\":1443,\"secret\":\"0123abcd\"}"));
        Assert.Equal("tg://proxy?server=127.0.0.1&port=2000&secret=ddff",
            TgProxyTool.ProxyLinkFrom("{\"host\":\"0.0.0.0\",\"port\":2000,\"secret\":\"ff\"}"));
        Assert.Null(TgProxyTool.ProxyLinkFrom("{\"host\":\"127.0.0.1\"}"));
        Assert.Null(TgProxyTool.ProxyLinkFrom("не json"));
    }
}
