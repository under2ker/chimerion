using System.Windows;
using System.Windows.Threading;

namespace Dnex;

public partial class App : Application
{
    private static Mutex? _single;

    /// <summary>Только один экземпляр: две копии CHIMERION, меняющие DNS одновременно, могли бы перепутать резервную копию.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        bool first;
        try
        {
            _single = new Mutex(false, @"Local\DNEX-single-instance");
            // После обновления новая копия стартует, пока старая еще закрывается: ждем ее до 20 секунд
            try { first = _single.WaitOne(Dnex.Services.Updater.StartedByRestart ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
            catch (AbandonedMutexException) { first = true; } // прежняя копия завершилась аварийно: мьютекс наш
        }
        // Мьютекс создала копия с правами администратора (автозапуск), а эта запущена без них: открыть его
        // не дают права. Это тоже значит «уже запущена», а не ошибка запуска
        catch (UnauthorizedAccessException) { first = false; }
        // Новая версия трижды не запустилась: OnStartup вернул прежнюю и запустил ее, эта копия закрывается
        if (first && Dnex.Services.Updater.OnStartup()) { Shutdown(); return; }
        if (!first)
        {
            DnexDialog.Show("CHIMERION уже запущена", "Значок программы в трее, рядом с часами. Если его не видно, раскройте скрытые значки стрелкой.", DialogKind.Info);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        // Завершение только явное (App.ExitNow после закрытия главного окна), а не «закрылось последнее окно»:
        // забытое скрытое окно больше не держит процесс
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var win = new MainWindow();
        MainWindow = win;
        // Запуск вместе с Windows: окно не создается вовсе, работает трей. Окно, показанное в первые секунды
        // после входа в систему (пока не готовы видеодрайвер и DWM), оставалось черным до закрытия.
        // Теперь оно создается при первом открытии из трея, когда система уже загружена.
        if (Dnex.Services.AutoStart.StartedHidden) _ = win.StartHiddenAsync();
        else win.Show();
    }

    /// <summary>
    /// Завершить программу. Страховка: если процесс не завершился за 8 секунд (зависшая операция в другом потоке),
    /// он закрывается принудительно, а в error.log остается запись для разбора.
    /// </summary>
    public static void ExitNow()
    {
        var guard = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(8));
            try { System.IO.File.AppendAllText(Dnex.Services.Storage.PathOf("error.log"), $"{DateTime.Now:u} Выход не завершился за 8 секунд, процесс закрыт принудительно\n"); } catch (Exception) { }
            Environment.Exit(0);
        }) { IsBackground = true, Name = "CHIMERION exit guard" };
        guard.Start();
        Current?.Dispatcher.BeginInvoke(new Action(() => Current?.Shutdown()));
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(Dnex.Services.Storage.PathOf("error.log"), $"{DateTime.Now:u} {e.Exception}\n"); } catch (Exception) { }
        DnexDialog.Show("Что-то пошло не так", e.Exception.Message + "\n\nПодробности записаны в error.log. Если ошибка повторяется, перезапустите программу.",
            DialogKind.Error, "Ошибка");
        e.Handled = true;
    }
}
