using System.Windows;
using System.Windows.Threading;

namespace Dnex;

public partial class App : Application
{
    private static Mutex? _single;
    /// <summary>Сигнал от второй копии: «покажи окно». Вторая копия будит первую и закрывается.</summary>
    private const string ShowSignal = @"Local\DNEX-show-window";
    private static EventWaitHandle? _show;

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
            // Повторный запуск ярлыком: просим работающую копию показать окно, как привыкли в других программах.
            // Если сигнал передать нельзя (копия с правами администратора или старая версия), подсказываем, где значок
            bool woke = false;
            try { using var ev = EventWaitHandle.OpenExisting(ShowSignal); woke = ev.Set(); }
            catch (Exception) { }
            if (!woke)
                DnexDialog.Show("CHIMERION уже запущена", "Значок программы в трее, рядом с часами. Если его не видно, раскройте скрытые значки стрелкой.", DialogKind.Info);
            Shutdown();
            return;
        }
        Dnex.Services.SupportTrace.Start();
        SupportTraceUi.Install();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                Dnex.Services.SupportTrace.Write("Аварийное завершение: " + Dnex.Services.SupportTrace.Error(error));
            Dnex.Services.SupportTrace.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Dnex.Services.SupportTrace.Write("Ошибка фоновой задачи: " + Dnex.Services.SupportTrace.Error(args.Exception));
        base.OnStartup(e);
        // Завершение только явное (App.ExitNow после закрытия главного окна), а не «закрылось последнее окно»:
        // забытое скрытое окно больше не держит процесс
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var win = new MainWindow();
        MainWindow = win;
        try
        {
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
            new Thread(() =>
            {
                while (_show.WaitOne()) Dispatcher.BeginInvoke(new Action(win.ShowFromTray));
            }) { IsBackground = true, Name = "CHIMERION show signal" }.Start();
        }
        catch (Exception) { /* без сигнала вторая копия просто подскажет, где значок */ }
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
        Dnex.Services.SupportTrace.Write("Запрошено завершение программы");
        Dnex.Services.SupportTrace.Flush();
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
        Dnex.Services.SupportTrace.Write("Ошибка интерфейса: " + Dnex.Services.SupportTrace.Error(e.Exception));
        Dnex.Services.SupportTrace.Flush();
        try { System.IO.File.AppendAllText(Dnex.Services.Storage.PathOf("error.log"), $"{DateTime.Now:u} {PrivacyGuard.Scrub(e.Exception.ToString())}\n"); } catch (Exception) { }
        DnexDialog.Show("Что-то пошло не так", e.Exception.Message + "\n\nПодробности записаны в error.log. Если ошибка повторяется, перезапустите программу.",
            DialogKind.Error, "Ошибка");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dnex.Services.SupportTrace.Write($"Конец сеанса; код выхода={e.ApplicationExitCode}");
        Dnex.Services.SupportTrace.Flush();
        base.OnExit(e);
    }
}
