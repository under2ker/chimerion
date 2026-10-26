using System.Windows.Media;
using Dnex.Controls;
using Xunit;

namespace Dnex.Tests;

public class UiTests
{
    /// <summary>
    /// Каждая иконка разбирается и целиком лежит в поле 24×24. Так ловятся сломанные контуры: склеенная
    /// относительная команда «m» (крестик рисовался как «<») и слитные числа вроде «.53.53».
    /// </summary>
    [Fact]
    public void Every_icon_parses_and_fits_the_24_box()
    {
        foreach (var (name, data) in Icon.Outline.Concat(Icon.Filled))
            foreach (var part in data.Split('|'))
            {
                var g = Geometry.Parse(part.Trim());
                var b = g.Bounds;
                Assert.False(b.IsEmpty, name);
                Assert.True(b.Left >= -0.5 && b.Top >= -0.5 && b.Right <= 24.5 && b.Bottom <= 24.5, $"{name}: {b}");
            }
    }

    [Fact]
    public void Close_icon_is_two_crossing_lines()
    {
        var parts = Icon.Outline["x"].Split('|').Select(p => Geometry.Parse(p.Trim()).Bounds).ToList();
        Assert.Equal(2, parts.Count);
        // Обе линии от 6 до 18 по обеим осям: крест, а не «<»
        Assert.All(parts, b => { Assert.Equal(6, b.Left, 1); Assert.Equal(18, b.Right, 1); Assert.Equal(6, b.Top, 1); Assert.Equal(18, b.Bottom, 1); });
    }
}

public class LocTests
{
    /// <summary>Английский без окон: свойство Lang ставится напрямую (SetLanguage создает элементы интерфейса).</summary>
    private static string En(string ru)
    {
        var p = typeof(Dnex.Services.Loc).GetProperty("Lang")!;
        p.SetValue(null, "en");
        try { return Dnex.Services.Loc.T(ru); }
        finally { p.SetValue(null, "ru"); }
    }

    [Theory]
    [InlineData("Сохранить", "Save")]
    [InlineData("Подключено · Нидерланды, Амстердам", "Connected · Netherlands, Amsterdam")]
    [InlineData("Найдено 3 новых, убрано 2 нерабочих", "Found 3 new, removed 2 non-working")]
    [InlineData("0 работают   ·   1 частично   ·   показано 7 из 7", "0 working   ·   1 partial   ·   showing 7 of 7")]
    [InlineData("Программа закроется, DNS останется переключенным.", "The app will close, DNS will stay switched.")]
    [InlineData("Россия › SmartDNS: Нидерланды → ИИ видят: Франция", "Russia › SmartDNS: Netherlands → AI sees: France")]
    [InlineData("Нидерланды › SmartDNS: нет подмены", "Netherlands › SmartDNS: no spoofing")]
    [InlineData("США › SmartDNS: Германия → ИИ видят разные страны", "United States › SmartDNS: Germany → AI sees different countries")]
    public void Translates_exact_templates_and_fragments(string ru, string en) => Assert.Equal(en, En(ru));

    [Fact]
    public void Never_mixes_languages_inside_a_phrase()
    {
        // Раньше короткий шаблон «{0} и {1}» давал «можно and через»
        var r = En("Вернуть исходные DNS можно и через reset-dns.cmd.");
        Assert.DoesNotContain(" and через", r);
        Assert.Equal("You can also restore the original DNS with reset-dns.cmd.", r);
    }

    [Fact]
    public void Russian_interface_returns_text_as_is() => Assert.Equal("Сохранить", Dnex.Services.Loc.T("Сохранить"));
}

public class RenameUpdateTests
{
    [Fact]
    public void New_release_picks_chimerion_archive_by_name()
    {
        // DNEX первой строкой: старые версии берут первую сумму
        var sha = "AAAA" + new string('0', 60) + "  DNEX-0.1.25-win-x64.zip\nBBBB" + new string('0', 60) + "  CHIMERION-0.1.25-win-x64.zip\n";
        var (hash, legacy) = Dnex.Services.Updater.PickSum(sha, new Version(0, 1, 25));
        Assert.False(legacy);
        Assert.StartsWith("BBBB", hash);
        Assert.EndsWith("/v0.1.25/CHIMERION-0.1.25-win-x64.zip", Dnex.Services.Updater.ZipUrl(new Version(0, 1, 25)));
    }

    [Fact]
    public void Old_release_falls_back_to_dnex_archive()
    {
        var sha = "CCCC" + new string('0', 60) + "  DNEX-0.1.24-win-x64.zip\n";
        var (hash, legacy) = Dnex.Services.Updater.PickSum(sha, new Version(0, 1, 24));
        Assert.True(legacy);
        Assert.StartsWith("CCCC", hash);
        Assert.EndsWith("/v0.1.24/DNEX-0.1.24-win-x64.zip", Dnex.Services.Updater.ZipUrl(new Version(0, 1, 24), legacy));
    }
}

public class PowerShellLaunchTests
{
    // Скрипт «Двух DNS» со всеми наборами сайтов: тысячи доменов, раньше не влезал в командную строку
    private static string BigScript(int domains) =>
        "$n = @(" + string.Join(",", Enumerable.Range(0, domains).Select(i => $"'site{i}.example.com','.site{i}.example.com'")) + ")\n" +
        "Write-Output ('правил: ' + $n.Count)";

    [Fact]
    public void Command_line_stays_short_for_huge_script()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(BigScript(20000));
        var args = Dnex.Services.DnsSwitcher.Arguments(Dnex.Services.DnsSwitcher.Launcher(@"C:\Users\x\AppData\Roaming\DNEX\ps-1.ps1", bytes, @"C:\Users\x\AppData\Roaming\DNEX\ps-1.log"), hidden: true);
        Assert.True(bytes.Length > 500_000);
        Assert.True(args.Length < 8000, $"командная строка {args.Length} символов");
    }

    [Fact]
    public async Task Huge_script_runs()
    {
        var (code, output) = await Dnex.Services.DnsSwitcher.RunPsAsync(BigScript(5000));
        Assert.Equal(0, code);
        Assert.Contains("правил: 10000", output);
    }

    [Fact]
    public async Task Changed_script_file_is_not_executed()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chimerion-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, "Write-Output 'ПОДМЕНА'");
        try
        {
            var launcher = Dnex.Services.DnsSwitcher.Launcher(file, System.Text.Encoding.UTF8.GetBytes("Write-Output 'исходный'"), null);
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(ps, Dnex.Services.DnsSwitcher.Arguments(launcher, hidden: false))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
              StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.NotEqual(0, p.ExitCode);
            Assert.DoesNotContain("ПОДМЕНА", output);
            Assert.Contains("изменен до запуска", output);
        }
        finally { File.Delete(file); }
    }
}
