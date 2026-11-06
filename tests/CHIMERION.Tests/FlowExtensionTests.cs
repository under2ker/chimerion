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
