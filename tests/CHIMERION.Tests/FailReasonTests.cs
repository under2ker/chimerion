using Dnex.Models;

namespace Dnex.Tests;

/// <summary>Строка «Не работает» всегда сопровождается причиной простыми словами.</summary>
public class FailReasonTests
{
    private static string R(ServerStatus st, bool noProxy = false, string? err = null, string? sum = null) => DnsServer.FailReasonOf(st, noProxy, err, sum);

    [Fact] public void Working_server_has_no_reason() => Assert.Equal("", R(ServerStatus.Ok));
    [Fact] public void Unchecked_server_has_no_reason() => Assert.Equal("", R(ServerStatus.Unknown));

    [Fact]
    public void Plain_dns_is_explained()
    {
        var r = R(ServerStatus.Fail, noProxy: true, err: "Не подменяет адреса ИИ-сервисов, обычный DNS");
        Assert.Contains("не подменяет", r);
    }

    [Fact] public void Region_block_is_explained() => Assert.Contains("закрыты для региона", R(ServerStatus.Fail, err: "ChatGPT: регион не поддерживается"));

    [Fact]
    public void Dead_dns_is_explained()
        => Assert.Contains("не отвечает на DNS", R(ServerStatus.Fail, err: "ChatGPT: нет DNS", sum: "ChatGPT: нет DNS · Claude: нет DNS · Gemini: нет DNS"));

    [Fact] public void Timeout_is_explained() => Assert.Contains("не ответил вовремя", R(ServerStatus.Fail, err: "Операция превысила время ожидания"));
    [Fact] public void Captcha_is_explained() => Assert.Contains("капчу", R(ServerStatus.Fail, err: "Gemini: капча"));

    [Fact]
    public void Unknown_error_is_shown_as_is_and_missing_error_is_stated()
    {
        Assert.Equal("странный сбой", R(ServerStatus.Fail, err: "странный сбой"));
        Assert.Contains("Причина не установлена", R(ServerStatus.Fail));
    }

    [Fact]
    public void Partial_names_what_does_not_open()
        => Assert.Contains("Claude ✗", R(ServerStatus.Partial, sum: "ChatGPT ✓ · Claude ✗ · Gemini ✓"));

    [Fact]
    public void Model_exposes_reason_and_updates_with_status()
    {
        var s = new DnsServer { Primary = "203.0.113.5" };
        var changed = new List<string>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        s.LastError = "ChatGPT: регион не поддерживается";
        s.Status = ServerStatus.Fail;
        Assert.Contains("закрыты для региона", s.FailReason);
        Assert.Contains(nameof(DnsServer.FailReason), changed);
    }
}
