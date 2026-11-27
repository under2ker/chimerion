using System.Text.Json;
using Dnex.Models;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Пустые значения (null) вместо списков в settings.json (аудит P-03): запуск не падает, остальное не меняется.</summary>
public class SettingsRepairTests
{
    private static AppSettings Parse(string json) => JsonSerializer.Deserialize<AppSettings>(json, Storage.Options)!;

    [Fact]
    public void Null_sources_and_targets_get_defaults()
    {
        var st = Parse("{\"sources\": null, \"targets\": null, \"discoveryQueries\": null, \"timeoutSeconds\": 9}");
        Assert.Null(st.Sources);
        var fixedCount = SettingsRepair.FillNullCollections(st, new AppSettings());
        Assert.True(fixedCount >= 3);
        Assert.Equal(new AppSettings().Sources.Count, st.Sources.Count);
        Assert.Equal(new AppSettings().Targets.Select(t => t.Name), st.Targets.Select(t => t.Name));
        Assert.NotEmpty(st.DiscoveryQueries);
        Assert.Equal(9, st.TimeoutSeconds); // обычные значения не тронуты
        st.Sources.RemoveAll(_ => false);  // то, на чем падала загрузка
    }

    [Fact]
    public void Defaults_are_copies_not_the_shared_instance()
    {
        var defs = new AppSettings();
        var st = Parse("{\"sources\": null}");
        SettingsRepair.FillNullCollections(st, defs);
        st.Sources.Clear();
        Assert.NotEmpty(defs.Sources);
    }

    [Fact]
    public void Null_lists_inside_services_and_null_items_are_repaired()
    {
        var st = Parse("{\"targets\": [ {\"name\": \"ChatGPT\", \"host\": \"chatgpt.com\", \"path\": \"/\", \"okStatusCodes\": null, \"extraHosts\": null}, null ]}");
        SettingsRepair.FillNullCollections(st, new AppSettings());
        Assert.Single(st.Targets);
        Assert.NotNull(st.Targets[0].OkStatusCodes);
        Assert.NotNull(st.Targets[0].ExtraHosts);
        Assert.Equal("chatgpt.com", st.Targets[0].Host);
    }

    [Fact]
    public void Normal_settings_are_not_changed_at_all()
    {
        var original = new AppSettings { TimeoutSeconds = 11 };
        original.Sources.RemoveAt(0);
        var json = JsonSerializer.Serialize(original, Storage.Options);
        var st = Parse(json);
        Assert.Equal(0, SettingsRepair.FillNullCollections(st, new AppSettings()));
        Assert.Equal(json, JsonSerializer.Serialize(st, Storage.Options));
    }

    [Fact]
    public void Empty_list_stays_empty()
    {
        var st = Parse("{\"sources\": []}");
        SettingsRepair.FillNullCollections(st, new AppSettings());
        Assert.Empty(st.Sources); // пустой список — выбор пользователя, а не поломка
    }
}
