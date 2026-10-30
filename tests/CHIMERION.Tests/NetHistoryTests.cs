using System.Text.Json;
using Dnex.Models;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>История сетевых изменений: запись, причина, чистка и приватность.</summary>
[Collection("scenarios")]
public class NetHistoryTests : IDisposable
{
    public NetHistoryTests() => NetHistory.Clear();
    public void Dispose() => NetHistory.Clear();

    [Fact]
    public void Entry_is_saved_with_time_what_and_default_cause()
    {
        NetHistory.Add("Подключено: Пример", "DNS адаптеров заменен на Smart DNS");
        var e = Assert.Single(NetHistory.Load());
        Assert.Equal("Подключено: Пример", e.What);
        Assert.Equal(NetHistory.UserCause, e.Why);
        Assert.Contains("Smart DNS", e.Details);
        Assert.True((DateTime.Now - e.At).TotalSeconds < 30);
    }

    [Fact]
    public async Task Cause_scope_follows_the_operation_across_awaits_and_ends_with_it()
    {
        using (NetHistory.Because("сторож"))
        {
            await Task.Delay(20);
            NetHistory.Add("Первое");
            await Task.Run(() => NetHistory.Add("Из другого потока"));
        }
        NetHistory.Add("После");
        var all = NetHistory.Load();
        Assert.Equal(new[] { "сторож", "сторож", NetHistory.UserCause }, all.Select(x => x.Why));
    }

    [Fact]
    public void Nested_cause_returns_to_the_outer_one()
    {
        using (NetHistory.Because("режим"))
        {
            using (NetHistory.Because("сторож")) NetHistory.Add("внутри");
            NetHistory.Add("снаружи");
        }
        Assert.Equal(new[] { "сторож", "режим" }, NetHistory.Load().Select(x => x.Why));
    }

    [Fact]
    public void Personal_data_never_reaches_the_history()
    {
        var user = Environment.UserName;
        NetHistory.Add("Событие", $@"путь C:\Users\{user}\AppData, адрес 198.51.100.7");
        var text = NetHistory.AsText();
        Assert.DoesNotContain(user, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("198.51.100.7", text);
    }

    [Fact]
    public void History_is_trimmed_to_the_latest_entries()
    {
        for (int i = 0; i < 650; i++) NetHistory.Add("запись " + i);
        var all = NetHistory.Load();
        Assert.InRange(all.Count, 400, 600);
        Assert.Equal("запись 649", all[^1].What);          // новые остаются
        Assert.DoesNotContain(all, x => x.What == "запись 0"); // старые уходят
    }

    [Fact]
    public void Broken_line_does_not_hide_the_other_entries()
    {
        NetHistory.Add("до");
        File.AppendAllText(Path.Combine(TestSetup.DataDir, "net-history.jsonl"), "{это не json\n");
        NetHistory.Add("после");
        Assert.Equal(new[] { "до", "после" }, NetHistory.Load().Select(x => x.What));
    }

    [Fact]
    public void Clear_removes_everything()
    {
        NetHistory.Add("раз");
        NetHistory.Clear();
        Assert.Empty(NetHistory.Load());
    }

    private static readonly DnsServer Srv = new() { Name = "Пример", Primary = "203.0.113.11" };

    [Fact]
    public async Task Connect_and_disconnect_are_recorded_with_the_reason()
    {
        using var sim = new NetSim(new NetSim.State { adapters = { new NetSim.Adapter() } });
        using (NetHistory.Because("сторож"))
            await DnsSwitcher.ApplyAsync(Srv, new AppSettings(), _ => { });
        await DnsSwitcher.RestoreAsync(_ => { });
        var all = NetHistory.Load();
        Assert.Contains(all, c => c.What.StartsWith("Подключено") && c.Why == "сторож");
        Assert.Contains(all, c => c.What.StartsWith("Отключено") && c.Why == NetHistory.UserCause);
    }
}
