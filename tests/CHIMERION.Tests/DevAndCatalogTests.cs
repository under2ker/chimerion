using System.Net;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Проверка наборов каталога на сервере (CatalogCheck) без сети.</summary>
public class CatalogCheckTests
{
    private static BypassCatalog.Group G(string id, params string[] domains) => new() { Id = id, Title = id, Domains = domains.ToList() };

    [Fact]
    public async Task Proxy_address_marks_group_as_opened_and_real_one_as_direct()
    {
        var other = IPAddress.Parse("192.0.2.77");
        var fake = new FakeChecker
        {
            Resolve = (host, _) => Task.FromResult(host switch
            {
                "figma.com" => new List<IPAddress> { FakeChecker.Proxy },
                "adobe.com" => new List<IPAddress> { FakeChecker.Real },
                "cdn.example" => new List<IPAddress> { other },
                "silent.example" => new List<IPAddress>(),
                _ => new List<IPAddress> { FakeChecker.Proxy }, // ИИ-сервисы сервер подменяет
            }),
        };
        var groups = new[]
        {
            G("design", "*.figma.com", "figma.com"),
            G("adobe", "adobe.com"),
            G("cdn", "cdn.example"),
            G("silent", "silent.example"),
            G("empty", "*.only.mask"),
        };
        var res = await CatalogCheck.RunAsync(fake, new DnsServer { Primary = "203.0.113.50" }, new AppSettings().Targets, groups, CancellationToken.None);
        Assert.Equal(CatalogCheck.Support.Proxied, res["design"]);
        Assert.Equal(CatalogCheck.Support.Direct, res["adobe"]);
        Assert.Equal(CatalogCheck.Support.Different, res["cdn"]);
        Assert.Equal(CatalogCheck.Support.NoAnswer, res["silent"]);
        Assert.Equal(CatalogCheck.Support.NoAnswer, res["empty"]);
    }

    [Fact]
    public void Sample_skips_masks_and_shared_google_hosts()
    {
        var g = G("x", "*.a.com", "accounts.google.com", "b.com", "c.com", "d.com");
        Assert.Equal(new[] { "b.com", "c.com" }, CatalogCheck.Sample(g));
    }

    [Fact]
    public void Real_catalog_groups_all_have_something_to_check()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "bypass.json");
        var data = System.Text.Json.JsonSerializer.Deserialize<BypassCatalog.Data>(File.ReadAllText(path), Storage.Options)!;
        Assert.All(data.Groups, g => Assert.NotEmpty(CatalogCheck.Sample(g)));
        Assert.All(DevTools.Groups, id => Assert.Contains(data.Groups, g => g.Id == id));
    }
}

/// <summary>WSL: чтение и правка .wslconfig, версия, итоговое состояние.</summary>
public class WslTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("[wsl2]\nmemory=8GB\n", null)]
    [InlineData("[wsl2]\ndnsTunneling=false\n", false)]
    [InlineData("[wsl2]\r\nDnsTunneling = TRUE # комментарий\r\n", true)]
    [InlineData("[experimental]\ndnsTunneling=false\n[wsl2]\nmemory=4GB", null)]
    [InlineData("[wsl2]\n# dnsTunneling=false\n", null)]
    public void Tunnel_setting_is_read_only_from_wsl2_section(string? config, bool? expected) =>
        Assert.Equal(expected, DevTools.TunnelSetting(config));

    [Fact]
    public void Enabling_tunnel_keeps_other_lines()
    {
        Assert.Equal("[wsl2]\ndnsTunneling=true\n", DevTools.WithTunnel(""));
        Assert.Equal("[boot]\nsystemd=true\n\n[wsl2]\ndnsTunneling=true\n", DevTools.WithTunnel("[boot]\nsystemd=true"));
        Assert.Equal("[wsl2]\r\ndnsTunneling=true\r\nmemory=8GB\r\n", DevTools.WithTunnel("[wsl2]\r\nmemory=8GB\r\n"));
        Assert.Equal("[wsl2]\nmemory=8GB\ndnsTunneling=true\n[boot]\nsystemd=true", DevTools.WithTunnel("[wsl2]\nmemory=8GB\ndnsTunneling=false\n[boot]\nsystemd=true"));
        Assert.True(DevTools.TunnelSetting(DevTools.WithTunnel("[wsl2]\n# dnsTunneling=false\n")));
    }

    [Theory]
    [InlineData("WSL version: 2.3.26.0\nKernel version: 5.15.167.4-1\n", "2.3.26")]
    [InlineData("Версия WSL: 2.1.5.0\nВерсия ядра: 5.15.146.1-2\n", "2.1.5")]
    [InlineData("Неверная команда", null)]
    public void Version_is_parsed_from_wsl_output(string output, string? expected) =>
        Assert.Equal(expected == null ? null : Version.Parse(expected), DevTools.ParseVersion(output));

    [Fact]
    public void State_depends_on_config_and_version_and_fix_writes_backup()
    {
        var dir = Path.Combine(TestSetup.DataDir, "wsl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, ".wslconfig");
        var (oldPath, oldCount, oldVer) = (DevTools.ConfigPath, DevTools.DistroCount, DevTools.WslVersion);
        try
        {
            DevTools.ConfigPath = () => cfg;
            DevTools.DistroCount = () => 0;
            Assert.Equal(DevTools.WslState.None, DevTools.Check().State);

            DevTools.DistroCount = () => 1;
            DevTools.WslVersion = () => new Version(2, 3, 26);
            Assert.Equal(DevTools.WslState.Ok, DevTools.Check().State);
            DevTools.WslVersion = () => new Version(2, 0, 9);
            Assert.Equal(DevTools.WslState.Old, DevTools.Check().State);

            File.WriteAllText(cfg, "[wsl2]\nmemory=8GB\ndnsTunneling=false\n");
            DevTools.WslVersion = () => new Version(2, 3, 26);
            Assert.Equal(DevTools.WslState.TunnelOff, DevTools.Check().State);

            DevTools.EnableTunnel();
            Assert.Equal(DevTools.WslState.Ok, DevTools.Check().State);
            Assert.Contains("memory=8GB", File.ReadAllText(cfg));
            Assert.Contains("dnsTunneling=false", File.ReadAllText(cfg + ".chimerion.bak"));
        }
        finally
        {
            (DevTools.ConfigPath, DevTools.DistroCount, DevTools.WslVersion) = (oldPath, oldCount, oldVer);
            Directory.Delete(dir, true);
        }
    }
}
