using System.IO;
using Dnex.Services.Tools;
using Xunit;

namespace Dnex.Tests;

public class FlowExtensionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chimerion-flow-" + Guid.NewGuid().ToString("N"));

    public FlowExtensionTests()
    {
        Directory.CreateDirectory(_dir);
        FlowExtension.FolderOverride = Path.Combine(_dir, "google-flow");
    }

    public void Dispose()
    {
        FlowExtension.FolderOverride = null;
        FlowExtension.BrowsersOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Prefs(string json)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, json);
        return p;
    }

    private static string Entry(string path, string extra = "") =>
        "{\"extensions\":{\"settings\":{\"abc\":{\"location\":4,\"path\":" + System.Text.Json.JsonSerializer.Serialize(path) + extra + "}}}}";

    [Fact]
    public void PrepareWritesFilesAndRewritesOnlyChanged()
    {
        Assert.False(FlowExtension.Prepared);
        Assert.False(FlowExtension.RefreshIfPrepared()); // не распакованное не появляется само
        Assert.False(Directory.Exists(FlowExtension.Folder));

        Assert.True(FlowExtension.Prepare());
        Assert.True(FlowExtension.Prepared);
        foreach (var f in new[] { "manifest.json", "hook.js", "background.js", "LICENSE" })
            Assert.True(File.Exists(Path.Combine(FlowExtension.Folder, f)), f);
        Assert.Empty(Directory.GetFiles(FlowExtension.Folder, "*.tmp"));
        Assert.Contains("flow.google.com", File.ReadAllText(Path.Combine(FlowExtension.Folder, "manifest.json")));

        Assert.False(FlowExtension.Prepare());
        File.WriteAllText(Path.Combine(FlowExtension.Folder, "hook.js"), "// изменено");
        Assert.True(FlowExtension.RefreshIfPrepared());
        Assert.NotEqual("// изменено", File.ReadAllText(Path.Combine(FlowExtension.Folder, "hook.js")));

        FlowExtension.Remove();
        Assert.False(FlowExtension.Prepared);
        FlowExtension.Remove(); // повторно без ошибки
    }

    [Fact]
    public void StateInPreferencesReadsBrowserRecord()
    {
        var folder = FlowExtension.Folder;
        Assert.Equal(FlowExtension.State.Enabled, FlowExtension.StateInPreferences(Prefs(Entry(folder, ",\"disable_reasons\":[]")), folder));
        Assert.Equal(FlowExtension.State.Enabled, FlowExtension.StateInPreferences(Prefs(Entry(folder.ToUpperInvariant() + "\\")), folder));
        Assert.Equal(FlowExtension.State.Disabled, FlowExtension.StateInPreferences(Prefs(Entry(folder, ",\"disable_reasons\":[1]")), folder));
        Assert.Equal(FlowExtension.State.Disabled, FlowExtension.StateInPreferences(Prefs(Entry(folder, ",\"disable_reasons\":1")), folder));
        Assert.Equal(FlowExtension.State.Disabled, FlowExtension.StateInPreferences(Prefs(Entry(folder, ",\"state\":0")), folder));
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateInPreferences(Prefs(Entry(Path.Combine(_dir, "other"))), folder));
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateInPreferences(Prefs("{\"extensions\":"), folder));
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateInPreferences(Prefs("{}"), folder));
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateInPreferences(Path.Combine(_dir, "missing"), folder));
    }

    [Fact]
    public void StateInLooksThroughAllProfiles()
    {
        var userData = Path.Combine(_dir, "User Data");
        var b = new FlowExtension.Browser("Chrome", "chrome.exe", userData, "chrome://extensions", "справа вверху");
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateIn(b));

        Directory.CreateDirectory(Path.Combine(userData, "Default"));
        Directory.CreateDirectory(Path.Combine(userData, "Profile 2"));
        Directory.CreateDirectory(Path.Combine(userData, "System Profile"));
        File.WriteAllText(Path.Combine(userData, "System Profile", "Secure Preferences"), Entry(FlowExtension.Folder));
        Assert.Equal(FlowExtension.State.NotConnected, FlowExtension.StateIn(b)); // служебный профиль не считается

        File.WriteAllText(Path.Combine(userData, "Default", "Secure Preferences"), Entry(FlowExtension.Folder, ",\"disable_reasons\":[1]"));
        Assert.Equal(FlowExtension.State.Disabled, FlowExtension.StateIn(b));
        File.WriteAllText(Path.Combine(userData, "Profile 2", "Preferences"), Entry(FlowExtension.Folder));
        Assert.Equal(FlowExtension.State.Enabled, FlowExtension.StateIn(b));
    }

    [Fact]
    public void PreferredFallsBackToFirstFound()
    {
        Assert.Null(FlowExtension.Preferred([]));
        var only = new FlowExtension.Browser("Edge", "msedge.exe", _dir, "edge://extensions", "слева внизу");
        Assert.Same(only, FlowExtension.Preferred([only]));
    }

    [Fact]
    public void ShortcutCarriesBrowserAndAddress()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var lnk = FlowExtension.WriteShortcut(new FlowExtension.Browser("Chrome", exe, _dir, "chrome://extensions", "справа вверху"), FlowExtension.FlowUrl);
        Assert.Equal(_dir, Path.GetDirectoryName(lnk));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(lnk);
        Assert.Equal(exe, (string)link.TargetPath, ignoreCase: true);
        Assert.Equal("\"" + FlowExtension.FlowUrl + "\"", (string)link.Arguments);
    }

    [Fact]
    public void WindowBuildsWithAndWithoutBrowser()
    {
        var userData = Path.Combine(_dir, "User Data");
        Directory.CreateDirectory(Path.Combine(userData, "Default"));
        var lists = new[]
        {
            Array.Empty<FlowExtension.Browser>(),
            new FlowExtension.Browser[]
            {
                new("Chrome", "chrome.exe", userData, "chrome://extensions", "справа вверху"),
                new("Edge", "msedge.exe", userData, "edge://extensions", "слева внизу"),
            },
        };
        foreach (var list in lists)
        {
            FlowExtension.BrowsersOverride = () => list;
            Wpf.Run(() =>
            {
                var w = FlowWindow.Build(_ => { });
                Assert.Contains("Google Flow", w.Title);
                Assert.NotNull(w.Content);
                w.Close();
            });
        }
    }
}

public class BypassSitesHugeListTests
{
    [Fact]
    public void OwnSitesEndWhereBuiltPartStarts()
    {
        var own = ZapretTool.OwnFromList(new[]
        {
            "# Never leave this file empty", "# Собрано CHIMERION: свои сайты и готовые наборы",
            "t.me", "flow.google.com", "r2.dev", "github.com", "githubusercontent.com", "aboutfacebook.com", "zzztube.tv",
        });
        Assert.Equal(new[] { "t.me", "flow.google.com" }, own);
    }

    [Fact]
    public void HandWrittenFileIsKeptWhole()
    {
        var own = ZapretTool.OwnFromList(new[] { "# Never leave this file empty", "a.example", "r2.dev", "b.example", "domain.example.abc" });
        Assert.Equal(new[] { "a.example", "r2.dev", "b.example" }, own);
    }

    [Fact]
    public void BuiltListNoLongerFeedsItself()
    {
        var z = new ZapretTool();
        var lists = Path.Combine(ZapretTool.Dir, "lists");
        var file = Path.Combine(lists, "list-general-user.txt");
        Directory.CreateDirectory(lists);
        var (ownBefore, groupsBefore, bigBefore) = (z.State.OwnSites, z.State.Groups, z.State.BigLists);
        var textBefore = File.Exists(file) ? File.ReadAllText(file) : null;
        File.Delete(file + ".backup");
        try
        {
            z.State.OwnSites = null;
            z.State.Groups = new();
            z.State.BigLists = new();
            var old = string.Join("\r\n", "# Never leave this file empty", "# Собрано CHIMERION: старый список", "t.me", "r2.dev", "github.com", "githubusercontent.com", "old-big-list.example", "");
            File.WriteAllText(file, old);
            var write = typeof(ZapretTool).GetMethod("WriteUserList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            write.Invoke(z, new object?[] { null });

            Assert.Equal(new[] { "t.me" }, z.State.OwnSites);
            Assert.Equal(old, File.ReadAllText(file + ".backup")); // прежний файл не потерян
            var built = File.ReadAllText(file);
            Assert.Contains("t.me", built);
            Assert.Contains("r2.dev", built);
            Assert.DoesNotContain("old-big-list.example", built);

            write.Invoke(z, new object?[] { null }); // повторная сборка копию не трогает и список не меняет
            Assert.Equal(old, File.ReadAllText(file + ".backup"));
            Assert.Equal(built, File.ReadAllText(file));
        }
        finally
        {
            (z.State.OwnSites, z.State.Groups, z.State.BigLists) = (ownBefore, groupsBefore, bigBefore);
            File.Delete(file + ".backup");
            if (textBefore != null) File.WriteAllText(file, textBefore); else File.Delete(file);
        }
    }

    [Fact]
    public void WindowDrawsLimitedRowsForHugeList()
    {
        // 84 973 строки вешали окно намертво: на каждый домен строилась своя строка с кнопкой, значками и плашкой
        Wpf.Run(() =>
        {
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var ctor = typeof(BypassSitesWindow).GetConstructors(Any).Single();
            var w = ctor.Invoke(new object?[] { null, new ZapretTool(), null, (Func<bool>)(() => false), null });
            var own = (List<string>)typeof(BypassSitesWindow).GetField("_own", Any)!.GetValue(w)!;
            own.Clear();
            own.AddRange(Enumerable.Range(0, 85_000).Select(i => $"site{i}.example"));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            typeof(BypassSitesWindow).GetMethod("Refresh", Any)!.Invoke(w, null);
            timer.Stop();
            var list = (System.Windows.Controls.Panel)typeof(BypassSitesWindow).GetField("_ownList", Any)!.GetValue(w)!;
            Assert.Equal(201, list.Children.Count); // 200 строк и подсказка
            Assert.InRange(timer.Elapsed.TotalSeconds, 0, 10);
        });
    }
}
