namespace Dnex.Services;

/// <summary>Порядок сетевых изменений вокруг локального DoH. Исполнители можно подменить в сценарных проверках.</summary>
internal sealed class DohRelayActivation(
    Func<CancellationToken, Task> startRelay,
    Func<CancellationToken, Task> writeRecovery,
    Func<CancellationToken, Task> applyNetwork,
    Func<CancellationToken, Task<bool>> verifyNetwork,
    Func<Task> restoreNetwork,
    Func<Task> stopRelay,
    Func<Task> clearRecovery)
{
    private readonly SemaphoreSlim _operation = new(1, 1);

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await startRelay(cancellationToken).ConfigureAwait(false);
            bool mayHaveChangedNetwork = false;
            bool recoveryPrepared = false;
            try
            {
                await writeRecovery(cancellationToken).ConfigureAwait(false);
                recoveryPrepared = true;
                cancellationToken.ThrowIfCancellationRequested();
                mayHaveChangedNetwork = true;
                await applyNetwork(cancellationToken).ConfigureAwait(false);
                if (!await verifyNetwork(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Не удалось подтвердить работу DNS через посредник");
            }
            catch (Exception startError)
            {
                if (mayHaveChangedNetwork)
                {
                    try { await restoreNetwork().ConfigureAwait(false); }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException("Не удалось подключиться и восстановить DNS; посредник оставлен работать", startError, restoreError);
                    }
                }
                await stopRelay().ConfigureAwait(false);
                if (recoveryPrepared) await clearRecovery().ConfigureAwait(false);
                throw;
            }
        }
        finally { _operation.Release(); }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Пока DNS не возвращен, посредник нужен даже после отказа Windows.
            await restoreNetwork().ConfigureAwait(false);
            await stopRelay().ConfigureAwait(false);
            await clearRecovery().ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }
}
