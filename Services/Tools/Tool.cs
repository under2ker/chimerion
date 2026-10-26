using System.Diagnostics;
using System.IO;

namespace Dnex.Services.Tools;

/// <summary>Что помнится об инструменте между запусками CHIMERION (файл tools.json).</summary>
public sealed class ToolState
{
    /// <summary>Установленная версия. Null — не установлен.</summary>
    public string? Version { get; set; }
    /// <summary>Включен пользователем: CHIMERION поднимает его при своем запуске.</summary>
    public bool Enabled { get; set; }
    /// <summary>zapret: файл стратегии (general (ALT2).bat).</summary>
    public string? Strategy { get; set; }
    /// <summary>zapret: обход и для игр (порты выше 1023).</summary>
    public bool Games { get; set; }
    /// <summary>zapret: обход по списку адресов сборки (ipset-all), в том числе Cloudflare.</summary>
    public bool IpSet { get; set; }
    /// <summary>zapret: свои сайты для обхода. Null — еще не переносились из файла списка (версии до наборов).</summary>
    public List<string>? OwnSites { get; set; }
    /// <summary>zapret: включенные готовые наборы (Meta, X, Discord...) из BypassCatalog.</summary>
    public List<string> Groups { get; set; } = new();
    /// <summary>zapret: большие внешние списки доменов (RemoteLists), например весь реестр от Re:filter.</summary>
    public List<string> BigLists { get; set; } = new();
    /// <summary>zapret: откуда брать адреса для IPSet. Null — список из сборки zapret.</summary>
    public string? IpSetSource { get; set; }
    /// <summary>zapret: после подбора сразу включать лучшую стратегию (иначе пользователь выбирает сам).</summary>
    public bool AutoApplyBest { get; set; }
}

/// <summary>
/// Сторонняя программа, которую CHIMERION скачивает с GitHub, включает, выключает и обновляет.
/// Код самих программ в CHIMERION не входит: ставится их официальный релиз после проверки SHA-256.
/// </summary>
public abstract class Tool
{
    private static readonly object Gate = new();
    private static Dictionary<string, ToolState>? _states;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Repo { get; }
    public abstract string Authors { get; }

    /// <summary>Последний релиз с прошлой проверки. Null — еще не проверяли или проверка не удалась.</summary>
    public Release? Latest { get; private set; }

    public ToolState State
    {
        get
        {
            lock (Gate)
            {
                _states ??= Storage.LoadOrCreate("tools.json", () => new Dictionary<string, ToolState>());
                if (!_states.TryGetValue(Id, out var s)) _states[Id] = s = new ToolState();
                return s;
            }
        }
    }

    public void SaveState()
    {
        lock (Gate) { try { if (_states != null) Storage.Save("tools.json", _states); } catch (Exception) { } }
    }

    public Version? InstalledVersion => State.Version is { } v && System.Version.TryParse(v, out var ver) ? ver : null;
    public bool UpdateAvailable => IsInstalled && Latest != null && InstalledVersion != null && Latest.Version > InstalledVersion;

    public abstract bool IsInstalled { get; }
    /// <summary>Запуск требует прав администратора (zapret: драйвер WinDivert работает с сетевыми пакетами).</summary>
    public virtual bool NeedsAdmin => false;
    public abstract bool IsRunning();
    /// <summary>Работает копия, которой управляет CHIMERION (ее и перезапускаем при обновлении).</summary>
    protected virtual bool RunningOurs() => IsRunning();
    public abstract Task StartAsync();
    public abstract Task StopAsync();
    protected abstract ReleaseAsset? PickAsset(Release r);
    /// <summary>Ставит проверенный файл релиза на место. Инструмент к этому моменту остановлен.</summary>
    protected abstract Task PlaceAsync(string file, IProgress<string> progress, CancellationToken ct);

    public async Task<Release> CheckAsync(CancellationToken ct = default)
    {
        Latest = await GitHubRelease.LatestAsync(Repo, ct);
        return Latest;
    }

    /// <summary>Установка или обновление до последнего релиза. Если инструмент работал, он перезапускается.</summary>
    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Смотрю последний релиз на GitHub…");
        var r = await CheckAsync(ct);
        var asset = PickAsset(r) ?? throw new ToolException($"В релизе {r.Tag} нет файла для Windows");
        var file = await GitHubRelease.DownloadVerifiedAsync(asset, progress, ct);
        var wasRunning = RunningOurs();
        if (wasRunning) { progress.Report("Останавливаю…"); await StopAsync(); }
        progress.Report("Устанавливаю…");
        try { await PlaceAsync(file, progress, ct); }
        finally { try { File.Delete(file); } catch (Exception) { } }
        State.Version = r.Version.ToString();
        SaveState();
        if (wasRunning) { progress.Report("Запускаю…"); await StartAsync(); }
    }

    /// <summary>Включить по кнопке: установить, если еще нет, и запустить. Запоминается для следующих запусков CHIMERION.</summary>
    public async Task EnableAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!IsInstalled) await InstallAsync(progress, ct);
        progress.Report("Запускаю…");
        await StartAsync();
        State.Enabled = true;
        SaveState();
    }

    public async Task DisableAsync()
    {
        await StopAsync();
        State.Enabled = false;
        SaveState();
    }

    protected static void Kill(IEnumerable<Process> processes)
    {
        foreach (var p in processes)
            using (p)
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch (Exception) { }
    }
}
