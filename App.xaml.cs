using System.Windows;
using System.Windows.Threading;

namespace Dnex;

public partial class App : Application
{
    private static Mutex? _single;

    /// <summary>Только один экземпляр: две копии DNEX, меняющие DNS одновременно, могли бы перепутать резервную копию.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(false, @"Local\DNEX-single-instance");
        bool first;
        // После обновления новая копия стартует, пока старая еще закрывается: ждем ее до 20 секунд
        try { first = _single.WaitOne(Dnex.Services.Updater.StartedAfterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { first = true; } // прежняя копия завершилась аварийно: мьютекс наш
        if (first) Dnex.Services.Updater.Cleanup();
        if (!first)
        {
            DnexDialog.Show("DNEX уже запущена", "Значок программы в трее, рядом с часами. Если его не видно, раскройте скрытые значки стрелкой.", DialogKind.Info);
            Shutdown();
            return;
        }
        base.OnStartup(e);

        var win = new MainWindow();
        MainWindow = win;
        // Запуск вместе с Windows: окно не создается вовсе, работает трей. Окно, показанное в первые секунды
        // после входа в систему (пока не готовы видеодрайвер и DWM), оставалось черным до закрытия.
        // Теперь оно создается при первом открытии из трея, когда система уже загружена.
        if (Dnex.Services.AutoStart.StartedHidden) _ = win.StartHiddenAsync();
        else win.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(Dnex.Services.Storage.PathOf("error.log"), $"{DateTime.Now:u} {e.Exception}\n"); } catch (Exception) { }
        DnexDialog.Show("Что-то пошло не так", e.Exception.Message + "\n\nПодробности записаны в error.log. Если ошибка повторяется, перезапустите программу.",
            DialogKind.Error, "Ошибка");
        e.Handled = true;
    }
}
