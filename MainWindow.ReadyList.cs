using System.Windows;
using Dnex.Models;
using Dnex.Services;

namespace Dnex;

public partial class MainWindow
{
    /// <summary>
    /// «Загрузить готовый список»: Smart DNS, которые нашел и проверил сервер CHIMERION (поиск на VPS раз в сутки).
    /// Берутся все сразу, без обхода сайтов, и проверяются здесь, как найденные поиском: не ответившие не остаются.
    /// </summary>
    private async void LoadReady_Click(object sender, RoutedEventArgs e)
    {
        var ct = BeginWork();
        int purgeStart = _purgeTotal;
        try
        {
            ShowBusyBar();
            ProgressText.Text = "Загружаю готовый список с сервера CHIMERION…";
            List<Crowd.Found> found;
            try { found = await Crowd.DiscoverAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Готовый список: сервер не ответил ({ex.GetBaseException().Message})");
                ShowToast("Сервер CHIMERION не ответил. Попробуйте позже или нажмите «Найти новые серверы»", "Warn");
                return;
            }
            var keys = _servers.Select(Crowd.KeyOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ready = Crowd.ReadyList(found, keys);
            int total = found.Count(f => f.Verified);
            var added = new List<DnsServer>();
            int skippedJunk = 0;
            foreach (var f in ready)
            {
                if (Crowd.ToServer(f) is not { } s) continue;
                // Не ответил здесь меньше суток назад: снова не берем, как и поиск
                if (_junk.TryGetValue(s.Key, out var junkAt) && DateTime.Now - junkAt < TimeSpan.FromHours(24)) { skippedJunk++; continue; }
                if (!_known.Add(s.Key)) continue;
                _junk.Remove(s.Key);
                s.Source = "Сервер CHIMERION";
                s.JustAdded = true;
                _servers.Add(s);
                _ = ClearJustAddedLaterAsync(s);
                added.Add(s);
            }
            if (added.Count == 0)
            {
                string why = total == 0 ? "на сервере пока нет готового списка" :
                    skippedJunk > 0 ? $"все {total} уже в списке или не отвечали у вас за последние сутки" : $"все {total} уже в вашем списке";
                Log($"Готовый список: новых нет, {why}");
                ShowToast($"Новых серверов нет: {why}");
                return;
            }
            Log($"Готовый список: с сервера CHIMERION {total}, новых {added.Count}. Проверяю");
            SaveServers();

            var checker = new ServerChecker(_settings);
            using var gate = new SemaphoreSlim(Math.Clamp(_settings.Parallelism, 4, 24));
            int done = 0;
            WorkBar.IsIndeterminate = false;
            WorkBar.Maximum = added.Count;
            async Task CheckAsync(DnsServer s)
            {
                try
                {
                    await gate.WaitAsync(ct);
                    try { await checker.CheckAsync(s, ct); }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log($"Проверка {s.DisplayName}: {ex.GetBaseException().Message}"); }
                finally
                {
                    int n = Interlocked.Increment(ref done);
                    _ = Dispatcher.InvokeAsync(() => { Ui.SetProgress(WorkBar, n); ProgressText.Text = $"Проверка готового списка: {n} из {added.Count}"; });
                }
            }
            await Task.WhenAll(added.Select(CheckAsync).ToArray());
            ct.ThrowIfCancellationRequested();
            await FinishChecksAsync(added, before: null);
            DropDeadNewcomers(added);
            _ = ResolveLocationsAsync();
            int working = added.Count(s => s.Status is ServerStatus.Ok or ServerStatus.Partial);
            int purged = _purgeTotal - purgeStart;
            Log($"Готовый список: работают у вас {working} из {added.Count}" + (purged > 0 ? $", не ответили и убраны {purged}" : ""));
            ShowToast($"Готовый список: добавлено {added.Count}, работают {working}");
        }
        catch (OperationCanceledException) { Log("Загрузка готового списка остановлена"); }
        finally { EndWork(); }
    }
}
