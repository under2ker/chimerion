using System.Windows;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Пункт меню «Google Flow»: окно подключения расширения и обновление его файлов после обновления программы.</summary>
public partial class MainWindow
{
    private void GoogleFlow_Click(object sender, RoutedEventArgs e) => FlowWindow.Show(Log);

    /// <summary>Если расширение уже распаковано, подкладывает файлы из новой версии программы. Сеть не трогает.</summary>
    private async Task RefreshFlowExtensionAsync()
    {
        try
        {
            if (await Task.Run(FlowExtension.RefreshIfPrepared))
                Log("Расширение Google Flow обновлено: изменения подхватятся после перезапуска браузера.");
        }
        catch (Exception ex) { Log($"Не удалось обновить расширение Google Flow: {ex.Message}"); }
    }
}
