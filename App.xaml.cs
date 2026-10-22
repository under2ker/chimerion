using System.Windows;
using System.Windows.Threading;

namespace Dnex;

public partial class App : Application
{
    private static Mutex? _single;

    /// <summary>Только один экземпляр: две копии DNEX, меняющие DNS одновременно, могли бы перепутать резервную копию.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\DNEX-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("DNEX уже запущена.", "DNEX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try { System.IO.File.AppendAllText(Dnex.Services.Storage.PathOf("error.log"), $"{DateTime.Now:u} {e.Exception}\n"); } catch (Exception) { }
        MessageBox.Show(e.Exception.Message + "\n\nПодробности записаны в error.log. Если ошибка повторяется, перезапустите программу.",
            "DNEX: ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
