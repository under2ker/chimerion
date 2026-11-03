using System.ComponentModel;
using System.IO;
using System.Net;

namespace Dnex.Services;

/// <summary>Запущенный локальный посредник DoH: канал управления и локальный адрес, на который ставится DNS.</summary>
internal sealed record DohRelaySession(string Channel, string ListenAddress, int Port);

/// <summary>
/// Запуск и остановка локального посредника DoH. Здесь собраны решения, которые не должны повторяться
/// в DnsSwitcher: проверка настроек, ожидание готовности и уборка прежнего задания.
/// </summary>
internal static class DohRelayRuntime
{
    internal const int Port = 53;
    internal const string ListenAddress = "127.0.0.2";
    /// <summary>
    /// Свободные локальные адреса по порядку. Один посредник занимает 127.0.0.2; при переключении на другой
    /// DoH-сервер прежний еще работает, поэтому новый берет следующий свободный адрес. DNS ставится на выбранный.
    /// </summary>
    internal static readonly string[] CandidateAddresses = { ListenAddress, "127.0.0.3", "127.0.0.4", "127.0.0.5" };

    // Точки подмены для сценарных проверок: настоящие запуск и остановка требуют Планировщика и отдельного процесса.
    internal static Func<string> ExecutablePath = () => Environment.ProcessPath ?? "";
    internal static Func<string?> CurrentUserSid = () => System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
    internal static Func<DohRelayConfig, CancellationToken, Task<bool>> StartHelper = DefaultStartHelper;
    internal static Func<string, CancellationToken, Task<bool>> StopHelper = DefaultStopHelper;
    internal static Func<string, CancellationToken, Task<bool>> PingHelper = (channel, ct) => DohRelayHost.CommandAsync(channel, "ping", ct);
    // Команда остановки отличается от проверки готовности: в тестах они подменяются по отдельности.
    internal static Func<string, CancellationToken, Task<bool>> StopCommand = DefaultStopCommand;
    // Планировщик подменяется целиком: так проверяется возврат прежнего задания без настоящего schtasks.
    internal static Func<DohRelayScheduler> Scheduler = DohRelayScheduler.SystemScheduler;
    internal static TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    internal static async Task<DohRelaySession> StartAsync(Uri endpoint, IReadOnlyList<IPAddress> upstream, Action<string> log, CancellationToken cancellationToken)
    {
        // Проверяем до записи настроек: иначе неудачный запуск оставил бы в папке данных защищенный адрес DoH.
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Некорректный адрес DoH", nameof(endpoint));
        if (upstream is null || upstream.Count == 0) throw new ArgumentException("Нет адресов сервера DoH", nameof(upstream));

        Exception? lastError = null;
        // Занятый локальный адрес (прежний посредник) это не сбой: переходим к следующему.
        foreach (var candidate in CandidateAddresses)
        {
            DohRelayConfig config;
            try { config = DohRelayConfig.Create(endpoint, IPAddress.Parse(candidate), upstream); }
            catch (ArgumentException) { continue; }
            // Настройки пишутся в файл этого канала: работающий прежний посредник свои не теряет, даже если смена не удастся.
            config.Save();
            // Файл ошибки прошлой попытки не должен ускорять отказ этой: его пишет сам посредник.
            DeleteFile(DohRelayConfig.ErrorFileNameFor(config.Channel), log);
            bool started;
            try { started = await StartHelper(config, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Отмена не должна оставлять задание, которое поднимется при следующем входе.
                await StopChannelAsync(config.Channel, log).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) { lastError = ex; started = false; }
            if (started)
            {
                log("Локальный посредник DoH запущен");
                return new DohRelaySession(config.Channel, candidate, Port);
            }
            await StopChannelAsync(config.Channel, log).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Локальный посредник DoH не запустился. Проверьте, что программе разрешено создавать задания Планировщика.", lastError);
    }

    internal static async Task StopAsync(DohRelaySession session, Action<string> log)
    {
        await StopChannelAsync(session.Channel, log).ConfigureAwait(false);
        log("Локальный посредник DoH остановлен");
    }

    internal static async Task<bool> EnsureRunningAsync(DohRelaySession session, Action<string> log, CancellationToken cancellationToken)
    {
        if (await PingAsync(session.Channel, cancellationToken).ConfigureAwait(false)) return true;
        // Процесс мог закрыться, а задание осталось: возвращаем его, не создавая новое.
        log("Локальный посредник DoH не отвечает, возвращаю прежнее задание");
        bool started;
        try { started = await Scheduler().RunExistingAsync(session.Channel, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            started = false;
        }
        if (!started)
        {
            log("Не удалось вернуть прежнее задание локального посредника DoH");
            return false;
        }
        if (!await WaitReadyAsync(session.Channel, cancellationToken).ConfigureAwait(false))
        {
            log("Локальный посредник DoH не ответил за отведенное время");
            return false;
        }
        return true;
    }

    internal static async Task CleanupStaleAsync(DohRelaySession? active, Action<string> log)
    {
        List<string> channels;
        try { channels = DohRelayConfig.SavedChannels(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("Не удалось прочитать настройки прежнего локального посредника DoH");
            return;
        }
        // Файл ошибки общего формата прежних сборок больше никто не читает. Файлы ошибок каналов без настроек
        // остаются от посредников, которые уже убраны: их тоже некому читать.
        DeleteFile(DohRelayConfig.LegacyErrorFileName, log);
        foreach (var orphan in DohRelayConfig.OrphanErrorChannels(channels))
            if (active is null || !string.Equals(active.Channel, orphan, StringComparison.Ordinal))
                DeleteFile(DohRelayConfig.ErrorFileNameFor(orphan), log);
        foreach (var channel in channels)
        {
            // Текущее подключение не трогаем: его посредник нужен прямо сейчас.
            if (active is not null && string.Equals(active.Channel, channel, StringComparison.Ordinal)) continue;
            await StopChannelAsync(channel, log).ConfigureAwait(false);
            log("Убран прежний локальный посредник DoH");
        }
    }

    private static async Task<bool> DefaultStartHelper(DohRelayConfig config, CancellationToken cancellationToken)
    {
        var exe = ExecutablePath();
        var sid = CurrentUserSid();
        // Задание нельзя создать без полного пути к программе и идентификатора пользователя.
        if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(sid)) return false;
        if (!await Scheduler().CreateAndRunAsync(exe, sid, config.Channel, cancellationToken).ConfigureAwait(false)) return false;
        // Процесс запускается заданием не мгновенно: готовность подтверждает ответ по каналу управления.
        return await WaitReadyAsync(config.Channel, cancellationToken).ConfigureAwait(false);
    }

    private static Task<bool> DefaultStopHelper(string channel, CancellationToken cancellationToken) =>
        Scheduler().DeleteAsync(channel, cancellationToken);

    private static async Task<bool> DefaultStopCommand(string channel, CancellationToken cancellationToken)
    {
        // Если процесс уже завершился, подключение к именованному каналу ждало бы впустую: ограничиваем
        // ожидание, чтобы остановка и перебор адресов не подвешивали интерфейс. Задание все равно удаляется следом.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        return await DohRelayHost.CommandAsync(channel, "stop", timeout.Token).ConfigureAwait(false);
    }

    private static async Task StopChannelAsync(string channel, Action<string> log)
    {
        // Остановка best effort: уже закрытый процесс или занятое задание не должны срывать отключение.
        try { await StopCommand(channel, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { log("Не удалось отправить команду остановки локальному посреднику DoH"); }

        try
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            if (!await StopHelper(channel, timeout.Token).ConfigureAwait(false))
                log("Задание локального посредника DoH не найдено или уже удалено");
        }
        catch (Exception) { log("Не удалось удалить задание локального посредника DoH"); }

        // У каждого канала свой файл настроек: удаляем только свои, настройки работающего посредника остаются.
        try { DohRelayConfig.Delete(channel); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log("Не удалось удалить служебный файл локального посредника DoH");
        }
        if (DohRelayConfig.IsChannel(channel)) DeleteFile(DohRelayConfig.ErrorFileNameFor(channel), log);
    }

    private static async Task<bool> WaitReadyAsync(string channel, CancellationToken cancellationToken)
    {
        if (!DohRelayConfig.IsChannel(channel)) return false;
        var error = Storage.PathOf(DohRelayConfig.ErrorFileNameFor(channel));
        var until = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            if (await PingAsync(channel, cancellationToken).ConfigureAwait(false)) return true;
            // Посредник уже сообщил, что не смог занять локальный адрес: ждать весь таймаут незачем,
            // иначе перебор адресов при занятом 127.0.0.2 занимал бы десятки секунд.
            if (File.Exists(error)) return false;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> PingAsync(string channel, CancellationToken cancellationToken)
    {
        try { return await PingHelper(channel, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static void DeleteFile(string name, Action<string> log)
    {
        try { File.Delete(Storage.PathOf(name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("Не удалось удалить служебный файл локального посредника DoH");
        }
    }
}
