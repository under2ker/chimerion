using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Счетчик копий программы. При запуске и каждые 6 часов на сервер общей карты уходят ID устройства, версия, время работы и служебные отметки
/// о подмене. ID виден в заголовке окна. Его считает DeviceIdentity от UUID материнской платы, сам UUID компьютер не покидает.
/// Запретить подмену на чужом компьютере нельзя, поэтому она обнаруживается: метка device.lock зашифрована ключом этой установки
/// Windows и хранит прежний ID. ID сменился: сервер получает прежний и переносит на новый историю устройства. Метка не читается:
/// папку данных перенесли с другого компьютера. Скрытый UUID виден по источнику ID. IP сервер не хранит.
/// </summary>
public static class UsageCounter
{
    internal const string LockFile = "device.lock";
    public static readonly TimeSpan PingEvery = TimeSpan.FromHours(6);

    internal static Func<string?> Source = DeviceIdentity.ReadSource;
    internal static Func<string> Kind = () => DeviceIdentity.SourceKind;

    private sealed record Lock(string Id, string Token);

    /// <summary>Сверка с меткой: постоянный случайный токен установки, прежний ID, если он другой, и признак переноса данных.</summary>
    internal sealed record LockCheck(string Token, string? PrevId, bool Moved);

    /// <summary>ID устройства: 32 шестнадцатеричных знака. Null, если источника нет.</summary>
    public static string? IdOf(string? source) => DeviceIdentity.HashOf(source);

    /// <summary>ID этой машины. Компьютер его не отдал: случайный, он хранится в настройках.</summary>
    public static string DeviceId(AppSettings s) => IdOf(Source()) ?? (s.UsageId ??= Guid.NewGuid().ToString("N"));

    public static bool Due(AppSettings s, DateTime now, bool atStart = false) => atStart || s.UsagePingAt is not { } last || now - last >= PingEvery;

    /// <summary>Время работы между отметками. Сон компьютера не считается: один шаг не длиннее 15 минут.</summary>
    public static void AddSession(AppSettings s, TimeSpan elapsed) =>
        s.UsageSeconds += (long)Math.Clamp(elapsed.TotalSeconds, 0, 900);

    internal static LockCheck Inspect(string id)
    {
        var path = Storage.PathOf(LockFile);
        if (!File.Exists(path)) return new LockCheck(NewToken(), null, false);
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine);
            var mark = JsonSerializer.Deserialize<Lock>(plain)!;
            return new LockCheck(mark.Token, mark.Id != id ? mark.Id : null, false);
        }
        catch (Exception) { return new LockCheck(NewToken(), null, true); } // зашифровано на другой установке Windows или испорчено
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");

    private static void SaveLock(string id, string token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Lock(id, token));
        File.WriteAllBytes(Storage.PathOf(LockFile), ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine));
    }

    /// <summary>Отметка. atStart: первая после запуска, она идет всегда. True: сервер принял.</summary>
    public static async Task<bool> PingAsync(AppSettings s, DateTime now, bool atStart = false, CancellationToken ct = default)
    {
        if (!Due(s, now, atStart)) return false;
        var id = DeviceId(s);
        var check = Inspect(id);
        var body = JsonSerializer.Serialize(new
        {
            v = 1, id, ver = UpdateChecker.CurrentVersion.ToString(), src = IdOf(Source()) == null ? "random" : Kind(),
            tok = check.Token, prev = check.PrevId, moved = check.Moved, sec = s.UsageSeconds, runs = s.UsageRuns,
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await Crowd.Http.PostAsync(Crowd.BaseUrl() + "/v1/ping", content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        SaveLock(id, check.Token);
        s.UsagePingAt = now;
        s.UsageSeconds = 0;
        s.UsageRuns = 0;
        return true;
    }
}
