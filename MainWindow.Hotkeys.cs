using Dnex.Services;

namespace Dnex;

/// <summary>
/// Глобальные сочетания клавиш (Services/GlobalHotkeys): Ctrl+Alt+D включает и выключает защиту, Ctrl+Alt+K открывает
/// «Поиск и команды» из любой программы, в том числе когда CHIMERION спрятан в трее. Включаются в настройках или в палитре.
/// </summary>
public partial class MainWindow
{
    private GlobalHotkeys.Host? _hotkeys;

    /// <summary>Привести сочетания в соответствие с настройкой: включить, если просили, выключить, если сняли галочку.</summary>
    private void ApplyGlobalHotkeys()
    {
        if (!_settings.GlobalHotkeys)
        {
            if (_hotkeys == null) return;
            _hotkeys.Dispose();
            _hotkeys = null;
            Log("Глобальные сочетания клавиш выключены");
            return;
        }
        if (_hotkeys != null) return;
        try
        {
            _hotkeys = new GlobalHotkeys.Host();
            _hotkeys.Pressed += id => Dispatcher.BeginInvoke(new Action(() => OnHotkey(id)));
            var failed = _hotkeys.RegisterAll();
            Log("Глобальные сочетания клавиш включены: " + string.Join(", ", GlobalHotkeys.All.Except(failed).Select(d => d.Text)));
            if (failed.Count > 0)
            {
                var text = string.Join(", ", failed.Select(d => d.Text));
                Log($"Сочетание уже занято другой программой: {text}");
                ShowToast($"Сочетание {text} занято другой программой и не заработает", "Warn");
            }
        }
        catch (Exception ex)
        {
            Log("Глобальные сочетания клавиш не включились: " + ex.Message);
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
    }

    private async void OnHotkey(int id)
    {
        try
        {
            if (id == GlobalHotkeys.PaletteId) { OpenPalette(); return; }
            if (id != GlobalHotkeys.ProtectionId) return;
            await ToggleProtectionAsync();
            // Окна на экране нет: итог показываем уведомлением Windows, иначе нажатие осталось бы без ответа
            if (!IsVisible || WindowState == System.Windows.WindowState.Minimized || !IsActive)
                Balloon(2500, "CHIMERION", DnsSwitcher.GetActive() != null ? "Защита включена" : "Защита выключена", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { Log("Сочетание клавиш: " + ex.Message); }
    }
}
