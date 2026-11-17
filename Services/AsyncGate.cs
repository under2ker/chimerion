namespace Dnex.Services;

/// <summary>
/// Пропускает асинхронные операции по одной. Повторный вход из той же цепочки вызовов проходит сразу, без ожидания:
/// операция может вызывать другую такую же (перезапуск = остановка + запуск) и не зависнуть на самой себе.
/// Нужна, чтобы два быстрых нажатия не запустили две копии winws.exe: вторая упирается в занятый драйвер WinDivert
/// и сразу завершается с кодом -1.
/// </summary>
public sealed class AsyncGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _holding = new();

    public async Task RunAsync(Func<Task> body)
    {
        if (_holding.Value) { await body(); return; }
        await _gate.WaitAsync();
        _holding.Value = true;
        try { await body(); }
        finally { _holding.Value = false; _gate.Release(); }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> body)
    {
        T result = default!;
        Func<Task> inner = async () => { result = await body(); }; // явный тип: иначе выбирается эта же перегрузка и выходит бесконечная рекурсия
        await RunAsync(inner);
        return result;
    }
}
