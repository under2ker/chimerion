using Dnex.Services;

namespace Dnex;

public partial class MainWindow
{
    private System.Windows.Forms.NotifyIcon? _supportTray;

    /// <summary>Трей использует WinForms, поэтому общие обработчики WPF его не видят.</summary>
    private void EnsureSupportTray()
    {
        if (_tray == null || ReferenceEquals(_supportTray, _tray)) return;
        _supportTray = _tray;
        _tray.DoubleClick += (_, _) => SupportTrace.Write("Трей: двойное нажатие значка");
        _tray.BalloonTipClicked += (_, _) => SupportTrace.Write("Трей: нажатие уведомления");
        if (_tray.ContextMenuStrip is not { } menu) return;
        menu.Opening += (_, _) => SupportTrace.Write("Трей: открытие меню");
        Watch(menu, "меню");

        static void Watch(System.Windows.Forms.ToolStrip strip, string path)
        {
            strip.ItemClicked += (_, e) =>
            {
                if (e.ClickedItem is { } clicked) SupportTrace.Write($"Трей: нажатие {path}/{strip.Items.IndexOf(clicked)}");
            };
            for (int i = 0; i < strip.Items.Count; i++)
                if (strip.Items[i] is System.Windows.Forms.ToolStripDropDownItem item && item.HasDropDownItems)
                    Watch(item.DropDown, path + "/" + i);
        }
    }
}
