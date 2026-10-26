using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex.Tests;

/// <summary>Регрессионные тесты на дефекты, найденные при комплексной проверке.</summary>
[Collection("scenarios")]
public class RegressionTests
{
    // ---------- Хранилище ----------

    private sealed class Sample { public int Value { get; set; } = 7; public List<string> Items { get; set; } = new(); }

    [Fact]
    public void Locked_file_is_not_treated_as_corrupted()
    {
        var name = $"lock-{Guid.NewGuid():N}.json";
        Storage.Save(name, new Sample { Value = 42 });
        var path = Storage.PathOf(name);
        Sample got;
        // Файл держит «антивирус» без права чтения дольше, чем повторы
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            got = Storage.LoadOrCreate(name, () => new Sample());
        Assert.Equal(7, got.Value);                          // в этот раз значения по умолчанию
        Assert.False(File.Exists(path + ".broken"));         // но порчей это не считается
        Assert.Equal(42, Storage.LoadOrCreate(name, () => new Sample()).Value); // и файл на диске цел
        File.Delete(path);
    }

    [Fact]
    public void Briefly_locked_file_is_read_after_retry()
    {
        var name = $"brief-{Guid.NewGuid():N}.json";
        Storage.Save(name, new Sample { Value = 5 });
        var path = Storage.PathOf(name);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(300).ContinueWith(_ => fs.Dispose());
        Assert.Equal(5, Storage.LoadOrCreate(name, () => new Sample()).Value);
        File.Delete(path);
    }

    [Theory]
    [InlineData("{ \"value\": ")]
    [InlineData("")]
    [InlineData("\0\0\0\0")]
    [InlineData("[1,2,3]")]
    public void Corrupted_file_is_kept_aside_and_defaults_used(string content)
    {
        var name = $"bad-{Guid.NewGuid():N}.json";
        File.WriteAllText(Storage.PathOf(name), content);
        Assert.Equal(7, Storage.LoadOrCreate(name, () => new Sample()).Value);
        Assert.Equal(content, File.ReadAllText(Storage.PathOf(name) + ".broken"));
        File.Delete(Storage.PathOf(name)); File.Delete(Storage.PathOf(name) + ".broken");
    }

    [Fact]
    public async Task Parallel_saves_of_one_file_do_not_fail_or_corrupt_it()
    {
        var name = $"par-{Guid.NewGuid():N}.json";
        var errors = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            for (int k = 0; k < 20; k++)
                try { Storage.Save(name, new Sample { Value = i, Items = Enumerable.Repeat("x", 200).ToList() }); }
                catch (Exception) { Interlocked.Increment(ref errors); }
        })));
        Assert.Equal(0, errors);
        var s = System.Text.Json.JsonSerializer.Deserialize<Sample>(File.ReadAllText(Storage.PathOf(name)), Storage.Options)!;
        Assert.Equal(200, s.Items.Count);
        Assert.Empty(Directory.GetFiles(TestSetup.DataDir, name + ".*.tmp"));
        File.Delete(Storage.PathOf(name));
    }

    // ---------- DNS-ответы ----------

    [Fact]
    public void Random_dns_answers_never_crash_the_parser()
    {
        var rnd = new Random(12345);
        var query = DnsWire.BuildQuery("api.openai.com", 0x1234);
        for (int i = 0; i < 20000; i++)
        {
            byte[] d;
            if (i % 2 == 0) { d = new byte[rnd.Next(0, 300)]; rnd.NextBytes(d); }
            else
            {
                // Похожий на настоящий ответ с испорченными байтами: так доходит до разбора записей
                d = query.Concat(new byte[rnd.Next(0, 80)]).ToArray();
                d[2] = 0x81; d[3] = 0x80; d[7] = (byte)rnd.Next(0, 70);
                for (int k = 0; k < 6; k++) d[rnd.Next(12, d.Length)] = (byte)rnd.Next(256);
            }
            try { DnsWire.ParseA(d, null); }
            catch (Exception e) when (e is FormatException or InvalidDataException or DnsWire.IdMismatchException) { }
        }
    }

    [Fact]
    public async Task Oversized_doh_answer_is_rejected()
    {
        // Сервер из чужого списка отвечает на DoH мегабайтами: ответ не читается в память целиком
        using var server = new TinyHttp(_ => (200, "application/dns-message", new byte[2_000_000]));
        var e = await Assert.ThrowsAnyAsync<Exception>(() =>
            DnsWire.ResolveDohAsync(server.Url, "api.openai.com", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.IsNotType<OutOfMemoryException>(e);
    }

    [Fact]
    public async Task Doh_error_status_is_reported()
    {
        using var server = new TinyHttp(_ => (502, "text/html", Encoding.UTF8.GetBytes("<html>bad gateway</html>")));
        var e = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            DnsWire.ResolveDohAsync(server.Url, "api.openai.com", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("502", e.Message);
    }

    [Fact]
    public async Task Doh_timeout_is_a_timeout_not_cancel()
    {
        using var server = new TinyHttp(_ => { Thread.Sleep(3000); return (200, "application/dns-message", Array.Empty<byte>()); });
        await Assert.ThrowsAsync<TimeoutException>(() =>
            DnsWire.ResolveDohAsync(server.Url, "api.openai.com", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    // ---------- VPN ----------

    [Theory]
    [InlineData("Radmin VPN", "Famatech Radmin VPN Ethernet Adapter", 0, 0, false)]  // журнал пользователя: VPN для игр по сети
    [InlineData("ZeroTier One", "ZeroTier Virtual Port", 0, 0, false)]
    [InlineData("WireGuard", "WireGuard Tunnel", 1, 0, true)]                       // свой DNS
    [InlineData("OpenVPN", "TAP-Windows Adapter V9", 0, 1, true)]                   // весь трафик через шлюз VPN
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", 1, 1, false)]
    [InlineData("Ethernet", "Realtek PCIe GbE", 1, 1, false)]
    public void Vpn_warning_only_for_adapters_that_can_take_dns(string name, string desc, int dns, int gw, bool warn) =>
        Assert.Equal(warn, SystemCheck.IsInterferingVpn(name, desc, System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, 1, dns, gw));

    // ---------- Отчет для GitHub ----------

    [Fact]
    public void Report_hides_city_and_provider_next_to_own_ip()
    {
        var log = "[07:28:20] После отключения: IP 88.86.206.62 (RU, Sverdlovsk Oblast, Pervouralsk, AS28890 INSYS ISP), DNS 192.168.1.1\n" +
                  "[07:28:20]   ИИ видят выход: Россия, Pervouralsk (88.86.206.62, AS28890 INSYS ISP)\n" +
                  "[07:28:32]   ИИ видят выход: Нидерланды, Dronten (83.217.211.149, AS41745 Baykov Ilya Sergeevich)";
        var r = Diagnostics.Sanitize(log, "88.86.206.62", "Alexandr");
        Assert.DoesNotContain("Pervouralsk", r);
        Assert.DoesNotContain("INSYS", r);
        Assert.DoesNotContain("88.86.206.62", r);
        Assert.DoesNotContain("192.168.1.1", r);
        Assert.Contains("IP [ваш IP] ([место скрыто]), DNS [локальный IP]", r);
        Assert.Contains("ИИ видят выход: [место скрыто] ([ваш IP])", r);
        // Выход через Smart DNS — адрес прокси, не пользователя: он нужен для разбора и остается
        Assert.Contains("Нидерланды, Dronten (83.217.211.149, AS41745", r);
    }

    // ---------- Внешние списки и релизы инструментов ----------

    [Theory]
    [InlineData("0.0.0.0/1", false)]
    [InlineData("128.0.0.0/1", false)]
    [InlineData("10.0.0.0/8", false)]
    [InlineData("192.168.1.0/24", false)]
    [InlineData("172.20.0.0/16", false)]
    [InlineData("169.254.0.0/16", false)]
    [InlineData("224.0.0.0/4", false)]
    [InlineData("100.64.0.0/10", false)]
    [InlineData("::/1", false)]
    [InlineData("fd00::/8", false)]
    [InlineData("fe80::/10", false)]
    [InlineData("104.16.0.0/12", true)]
    [InlineData("8.0.0.0/8", true)]
    [InlineData("2606:4700::/32", true)]
    public void Ip_lists_reject_too_broad_and_local_networks(string net, bool ok) =>
        Assert.Equal(ok, RemoteLists.Clean(net, ip: true).Count == 1);

    [Fact]
    public void Ip_list_keeps_single_public_addresses_and_drops_local_ones() =>
        Assert.Equal(new[] { "1.2.3.4", "2a00::1" },
                     RemoteLists.Clean("1.2.3.4\n192.168.0.1\n127.0.0.1\n2a00::1\nfe80::1\n# коммент\n", ip: true));

    [Theory]
    [InlineData("v1.11.2", "1.11.2")]
    [InlineData("1.10.4", "1.10.4")]
    [InlineData("v72", "72.0")]
    [InlineData("release-1.2", null)]
    [InlineData("", null)]
    public void Release_tags_parse(string tag, string? expected) => Assert.Equal(expected, GitHubRelease.ParseTag(tag)?.ToString());

    [Theory]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("../../evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    public async Task Release_file_name_cannot_escape_temp_folder(string name)
    {
        var body = Encoding.UTF8.GetBytes("файл релиза");
        using var server = new TinyHttp(_ => (200, "application/octet-stream", body));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
        var path = await GitHubRelease.DownloadVerifiedAsync(new ReleaseAsset(name, server.Url, sha), new Progress<string>(), CancellationToken.None);
        try { Assert.Equal(Path.Combine(Path.GetTempPath(), "CHIMERION-tools", "evil.exe"), Path.GetFullPath(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{\"host\":\"127.0.0.1\",\"port\":1443,\"secret\":\"abcdef\"}", "tg://proxy?server=127.0.0.1&port=1443&secret=ddabcdef")]
    [InlineData("{\"host\":\"0.0.0.0\",\"port\":1443,\"secret\":\"ab\"}", "tg://proxy?server=127.0.0.1&port=1443&secret=ddab")]
    [InlineData("{\"host\":\"evil&secret=00\",\"port\":1443,\"secret\":\"ab\"}", null)]
    [InlineData("{\"host\":\"127.0.0.1\",\"port\":70000,\"secret\":\"ab\"}", null)]
    [InlineData("{\"host\":\"127.0.0.1\",\"port\":1443,\"secret\":\"zz\"}", null)]
    [InlineData("не json", null)]
    public void Telegram_link_is_built_only_from_valid_settings(string json, string? link) =>
        Assert.Equal(link, TgProxyTool.ProxyLinkFrom(json));

    // ---------- zapret ----------

    [Theory]
    [InlineData("general.bat", true)]
    [InlineData("general (ALT2).bat", true)]
    [InlineData("..\\..\\general.bat", false)]
    [InlineData("C:\\temp\\general.bat", false)]
    [InlineData("service.bat", false)]
    [InlineData("general.cmd", false)]
    [InlineData(null, false)]
    public void Only_strategy_files_of_the_build_are_accepted(string? name, bool ok) => Assert.Equal(ok, ZapretTool.IsStrategyName(name));

    private static string NewBuild(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "lists"));
        File.WriteAllText(Path.Combine(root, "bin", "winws.exe"), version);
        File.WriteAllText(Path.Combine(root, "lists", "list-general.txt"), "youtube.com\n");
        return root;
    }

    [Fact]
    public void Zapret_update_replaces_folder_and_keeps_user_lists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"zap-{Guid.NewGuid():N}");
        var target = NewBuild(Path.Combine(tmp, "zapret"), "old");
        var fresh = NewBuild(Path.Combine(tmp, "new"), "new");
        var keep = new Dictionary<string, byte[]> { ["list-general-user.txt"] = Encoding.UTF8.GetBytes("mysite.ru\n") };
        ZapretTool.SwapIn(fresh, target, keep);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "bin", "winws.exe")));
        Assert.Equal("mysite.ru\n", File.ReadAllText(Path.Combine(target, "lists", "list-general-user.txt")));
        Assert.False(Directory.Exists(target + ".old"));
        Assert.False(Directory.Exists(target + ".new"));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public void Failed_zapret_update_keeps_installed_version()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"zap-{Guid.NewGuid():N}");
        var target = NewBuild(Path.Combine(tmp, "zapret"), "old");
        File.WriteAllText(Path.Combine(target, "lists", "list-general-user.txt"), "mysite.ru\n");
        var fresh = NewBuild(Path.Combine(tmp, "new"), "new");
        // Драйвер WinDivert занят: файл в установленной папке открыт, и папку нельзя ни удалить, ни переместить
        using (new FileStream(Path.Combine(target, "bin", "winws.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ToolException>(() => ZapretTool.SwapIn(fresh, target, new Dictionary<string, byte[]>()));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "bin", "winws.exe")));
        Assert.Equal("mysite.ru\n", File.ReadAllText(Path.Combine(target, "lists", "list-general-user.txt")));
        Assert.False(Directory.Exists(target + ".new"));
        Directory.Delete(tmp, true);
    }

    // ---------- Повышенный запуск PowerShell ----------

    private static async Task<(int Code, string Out)> RunElevatedLauncher(string script, string outFile)
    {
        var file = Path.Combine(TestSetup.DataDir, $"ps-{Guid.NewGuid():N}.ps1");
        var bytes = new UTF8Encoding(false).GetBytes(script);
        File.WriteAllBytes(file, bytes);
        try
        {
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            using var p = Process.Start(new ProcessStartInfo(ps, DnsSwitcher.Arguments(DnsSwitcher.Launcher(file, bytes, outFile), hidden: false))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await p.WaitForExitAsync();
            return (p.ExitCode, File.Exists(outFile) ? File.ReadAllText(outFile) : "");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Elevated_launcher_writes_output_and_errors_to_its_own_file()
    {
        var outFile = Path.Combine(TestSetup.DataDir, $"ps-{Guid.NewGuid():N}.log");
        var (code, text) = await RunElevatedLauncher("Write-Output 'строка 1'; Write-Warning 'внимание'; Write-Output 'ADDED 1.2.3.4'", outFile);
        Assert.Equal(0, code);
        Assert.Contains("строка 1", text);
        Assert.Contains("ADDED 1.2.3.4", text);
        File.Delete(outFile);

        (code, text) = await RunElevatedLauncher("Write-Output 'до'; throw 'сломалось'", outFile);
        Assert.Equal(1, code);
        Assert.Contains("до", text);
        Assert.Contains("ОШИБКА: сломалось", text);
        File.Delete(outFile);
    }

    [Fact]
    public async Task PowerShell_errors_reach_the_log()
    {
        // Ошибки PowerShell без прав администратора приходят в CLIXML; раньше их отбрасывали вместе с прогрессом
        var (code, output) = await DnsSwitcher.RunPsAsync("Write-Output 'начало'; Write-Error 'несуществующий адаптер'; Write-Output 'конец'");
        Assert.Contains("начало", output);
        Assert.Contains("несуществующий адаптер", output);
        (code, output) = await DnsSwitcher.RunPsAsync("$ErrorActionPreference = 'Stop'; Get-Item 'Z:/нет-такого-файла'");
        Assert.NotEqual(0, code);
        Assert.Contains("ОШИБКА", output);
    }

    [Fact]
    public void Clixml_progress_is_dropped_and_errors_kept()
    {
        var err = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\"><AV>Подготовка модулей</AV></PR></MS></Obj>" +
                  "<S S=\"Error\">Отказано в доступе_x000D__x000A_</S><S S=\"Error\">строка &lt;2&gt;_x000D__x000A_</S></Objs>\r\nОШИБКА: итог";
        var r = DnsSwitcher.ClixmlErrors(err);
        Assert.DoesNotContain("Подготовка", r);
        Assert.Contains("Отказано в доступе", r);
        Assert.Contains("строка <2>", r);
        Assert.Contains("ОШИБКА: итог", r);
        Assert.Equal("", DnsSwitcher.ClixmlErrors("#< CLIXML\r\n<Objs Version=\"1.1.0.1\"><Obj S=\"progress\"></Obj></Objs>"));
        Assert.Equal("обычный текст", DnsSwitcher.ClixmlErrors("обычный текст"));
    }

    [Fact]
    public async Task Elevated_launcher_does_not_write_into_planted_file()
    {
        // На месте файла вывода уже лежит чужой файл (так выглядела бы подложенная жесткая ссылка на системный файл)
        var outFile = Path.Combine(TestSetup.DataDir, $"ps-{Guid.NewGuid():N}.log");
        File.WriteAllText(outFile, "системный файл");
        var (code, text) = await RunElevatedLauncher("Write-Output 'перезапись'", outFile);
        Assert.Equal(1, code);
        Assert.Equal("системный файл", text);
        File.Delete(outFile);
    }
}

/// <summary>Минимальный HTTP-сервер на 127.0.0.1 для проверки DoH-клиента без внешней сети.</summary>
public sealed class TinyHttp : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/dns-query";

    public TinyHttp(Func<string, (int Code, string Type, byte[] Body)> handler)
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try
                        {
                            var s = c.GetStream();
                            var buf = new byte[65536];
                            var n = await s.ReadAsync(buf);
                            var (code, type, body) = handler(Encoding.ASCII.GetString(buf, 0, n));
                            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} X\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                            await s.WriteAsync(head);
                            await s.WriteAsync(body);
                        }
                        catch (Exception) { }
                    }
                });
            }
        });
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

/// <summary>Автозапуск без прав, предупреждения о политиках и запасная проверка версии.</summary>
public class PolicyAndStartupTests
{
    [Theory]
    [InlineData(@"C:\Program Files\CHIMERION\CHIMERION.exe")]
    [InlineData(@"C:\Users\Ваня Петров\Desktop\CHIMERION.exe")]
    public void Autostart_command_keeps_path_with_spaces(string exe)
    {
        var cmd = AutoStart.CommandFor(exe);
        Assert.EndsWith(" --tray", cmd);
        Assert.Equal(exe, AutoStart.ExeFromCommand(cmd));
    }

    [Fact]
    public void Autostart_path_is_read_from_old_unquoted_command() =>
        Assert.Equal(@"C:\Tools\DNEX.exe", AutoStart.ExeFromCommand(@"C:\Tools\DNEX.exe --tray"));

    [Theory]
    [InlineData("FullLanguage", false)]
    [InlineData(null, false)]
    [InlineData("ConstrainedLanguage", true)]
    [InlineData("RestrictedLanguage", true)]
    public void Restricted_powershell_is_reported(string? mode, bool warn)
    {
        var w = DnsSwitcher.LanguageModeWarning(mode);
        Assert.Equal(warn, w != null);
        if (warn) Assert.Contains(mode!, w);
    }

    [Fact]
    public void Group_policy_nrpt_is_reported()
    {
        Assert.Null(SplitDns.PolicyWarning(0));
        Assert.Contains("групповой политикой (3)", SplitDns.PolicyWarning(3));
    }

    [Theory]
    [InlineData("{\"versions\":[{\"version\":\"0.1.9\"},{\"version\":\"0.1.26\"},{\"version\":\"0.1.10\"}]}", "0.1.26")]
    [InlineData("{\"versions\":[]}", null)]
    [InlineData("{\"versions\":[{\"version\":\"main\"}]}", null)]
    [InlineData("<html>не json</html>", null)]
    public void Jsdelivr_versions_parse(string json, string? latest) =>
        Assert.Equal(latest, UpdateChecker.CdnLatest(json)?.ToString(3));
}

/// <summary>Режим скрытия данных: что маскируется и что остается.</summary>
[Collection("scenarios")]
public class PrivacyMaskTests : IDisposable
{
    public PrivacyMaskTests()
    {
        PrivacyGuard.ForgetPersonal();
        PrivacyGuard.RememberPersonal("88.86.206.62", "RU, Sverdlovsk Oblast, Pervouralsk", "AS28890 INSYS ISP");
    }

    public void Dispose() { PrivacyGuard.Set(false); PrivacyGuard.ForgetPersonal(); }

    private const string Line = "После отключения: IP 88.86.206.62 (RU, Sverdlovsk Oblast, Pervouralsk, AS28890 INSYS ISP), DNS 83.217.211.149";

    [Fact]
    public void Personal_values_are_hidden_when_mode_is_on()
    {
        PrivacyGuard.Set(true);
        var r = PrivacyGuard.Mask(Line);
        Assert.DoesNotContain("88.86.206.62", r);
        Assert.DoesNotContain("Pervouralsk", r);
        Assert.DoesNotContain("Sverdlovsk", r);
        Assert.DoesNotContain("INSYS", r);
        Assert.DoesNotContain("AS28890", r);
        Assert.Contains("83.217.211.149", r); // адрес DNS-сервера нужен для работы и остается
        Assert.Contains("RU", r);             // страна остается
    }

    [Fact]
    public void Nothing_changes_when_mode_is_off() => Assert.Equal(Line, PrivacyGuard.Mask(Line));

    [Fact]
    public void Only_whole_values_are_masked()
    {
        PrivacyGuard.Set(true);
        Assert.Equal("188.86.206.62 и 88.86.206.621", PrivacyGuard.Mask("188.86.206.62 и 88.86.206.621"));
        Assert.Equal("IP скрыто, город скрыто", PrivacyGuard.Mask("IP 88.86.206.62, город Pervouralsk"));
    }

    [Fact]
    public void Copied_text_is_also_cleaned_of_local_addresses_and_user_name()
    {
        PrivacyGuard.Set(true);
        var r = PrivacyGuard.ForExport(Line + @" DNS 192.168.1.1, путь C:\Users\" + Environment.UserName + @"\AppData");
        Assert.DoesNotContain("88.86.206.62", r);
        Assert.DoesNotContain("192.168.1.1", r);
        if (Environment.UserName.Length >= 2) Assert.DoesNotContain(@"\" + Environment.UserName + @"\", r);
    }

    [Fact]
    public void Turning_mode_on_raises_change_for_the_window()
    {
        int n = 0;
        void H() => n++;
        PrivacyGuard.Changed += H;
        try { PrivacyGuard.Set(true); PrivacyGuard.Set(true); PrivacyGuard.Set(false); }
        finally { PrivacyGuard.Changed -= H; }
        Assert.Equal(2, n);
    }
}
