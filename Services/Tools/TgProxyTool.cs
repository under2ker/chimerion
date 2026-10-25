using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dnex.Services.Tools;

/// <summary>
/// TG WS Proxy (github.com/Flowseal/tg-ws-proxy): локальный MTProto-прокси для Telegram Desktop, который ведет
/// трафик к серверам Telegram через WebSocket. Это отдельная программа со своим значком в трее и своими
/// настройками в %APPDATA%\TgWsProxy. DNEX ее скачивает, запускает, останавливает и обновляет, а ссылку
/// для подключения Telegram собирает из ее настроек так же, как она сама.
/// </summary>
public sealed class TgProxyTool : Tool
{
    public override string Id => "tg";
    public override string Title => "Ускорение Telegram";
    public override string Repo => "Flowseal/tg-ws-proxy";
    public override string Authors => "TG WS Proxy, автор Flowseal, лицензия MIT";

    private static string Dir => Path.Combine(Storage.Dir, "tools", "tg-ws-proxy");
    private static string AssetName => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "TgWsProxy_windows_arm64.exe" : "TgWsProxy_windows.exe";
    private static string ExePath => Path.Combine(Dir, AssetName);

    public override bool IsInstalled => File.Exists(ExePath);

    /// <summary>Любая копия прокси: у программы своя защита от второго экземпляра, две копии не уживутся.</summary>
    private static Process[] Running() =>
        Process.GetProcesses().Where(p => p.ProcessName.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase)).ToArray();

    public override bool IsRunning()
    {
        var list = Running();
        foreach (var p in list) p.Dispose();
        return list.Length > 0;
    }

    /// <summary>Копия из папки DNEX. Путь чужой копии (поставленной пользователем самим) не совпадает.</summary>
    private static bool IsOurs(Process p)
    {
        try { return string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; } // процесс с правами выше наших: путь не прочитать, считаем чужим
    }

    protected override bool RunningOurs()
    {
        var list = Running();
        try { return list.Any(IsOurs); }
        finally { foreach (var p in list) p.Dispose(); }
    }

    /// <summary>Работает копия прокси, которую пользователь запустил сам, не через DNEX.</summary>
    public bool ForeignCopyRunning()
    {
        var list = Running();
        try { return list.Any(p => !IsOurs(p)); }
        finally { foreach (var p in list) p.Dispose(); }
    }

    protected override ReleaseAsset? PickAsset(Release r) => r.Assets.FirstOrDefault(a => a.Name == AssetName);

    protected override Task PlaceAsync(string file, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        File.Copy(file, ExePath, overwrite: true);
        return Task.CompletedTask;
    }

    public override async Task StartAsync()
    {
        if (IsRunning()) return; // уже работает (запущен раньше или пользователем сам)
        if (!IsInstalled) throw new ToolException("Прокси не установлен");
        try { Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Dir })?.Dispose(); }
        catch (Exception e) { throw new ToolException("Не удалось запустить прокси: " + e.Message + ". Возможно, файл удалил антивирус"); }
        // Прокси поднимается за пару секунд; если он сразу закрылся, это видно здесь
        await Task.Delay(2500);
        if (!IsRunning()) throw new ToolException("Прокси запустился и сразу закрылся. Возможно, его остановил антивирус");
    }

    /// <summary>Останавливает только копию из папки DNEX. Собственную копию пользователя не трогаем.</summary>
    public override Task StopAsync()
    {
        var list = Running();
        Kill(list.Where(IsOurs));
        foreach (var p in list) p.Dispose();
        if (ForeignCopyRunning())
            throw new ToolException("Работает ваша собственная копия TG WS Proxy, запущенная не через DNEX. Закройте ее через ее значок в трее");
        return Task.CompletedTask;
    }

    /// <summary>Папка настроек прокси: переносная рядом с exe, если она есть, иначе %APPDATA%\TgWsProxy.</summary>
    private static string ConfigPath
    {
        get
        {
            var portable = Path.Combine(Dir, "TgWsProxy_data", "config.json");
            return File.Exists(portable) ? portable
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TgWsProxy", "config.json");
        }
    }

    /// <summary>
    /// Ссылка tg://proxy для Telegram, как ее собирает сам прокси: адрес, порт и секрет из его настроек,
    /// к секрету приписывается «dd». Null, если настроек еще нет (прокси ни разу не запускался).
    /// </summary>
    public static string? ProxyLink() => ProxyLinkFrom(File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null);

    public static string? ProxyLinkFrom(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var r = doc.RootElement;
            var host = r.TryGetProperty("host", out var h) ? h.GetString() ?? "127.0.0.1" : "127.0.0.1";
            var port = r.TryGetProperty("port", out var p) && p.TryGetInt32(out var pv) ? pv : 1443;
            var secret = r.TryGetProperty("secret", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(secret) || !secret.All(Uri.IsHexDigit)) return null;
            if (host is "0.0.0.0" or "::" or "") host = "127.0.0.1"; // слушает все адреса: Telegram на этом же компьютере
            return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
        }
        catch (JsonException) { return null; }
    }
}
