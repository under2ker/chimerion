using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Dnex;

/// <summary>
/// Выбор режима прямо на Главной: те же четыре режима, что в разделе «Режим» («Только ИИ», «Все сразу», «Игры»,
/// «Все выключить»), но один щелчок вместо двух. Само включение делает прежний ApplyModeAsync, здесь только плитки,
/// их состояние и подсказка «Сейчас». ОТКАТ: весь блок снимается откатом коммита с пометкой «ОТКАТ: режимы на Главной»
/// (git revert), файл и три строки в MainWindow.Dashboard.cs уходят вместе.
/// </summary>
public partial class MainWindow
{
    private UniformGrid _dashboardModeGrid = null!;
    private TextBlock _dashboardModeState = null!;
    private readonly List<(Controls.ClickTile Tile, Controls.Icon Icon, TextBlock Title, TextBlock Sub)> _dashboardModeTiles = new();
    private int? _dashboardModeRunning;

    /// <summary>Короткая подпись под названием режима, пока он не включен.</summary>
    private static readonly string[] ModeShort = { "нейросети", "ИИ, Telegram, обход", "обход для игр", "DNS сети, обходы выкл." };

    private static List<ModeWindow.ModeInfo>? _modeInfos;
    private static List<ModeWindow.ModeInfo> ModeInfos => _modeInfos ??=
        Modes.Select((m, i) => new ModeWindow.ModeInfo(i, m.Title, m.Hint, m.Icon, m.Mode == Mode.AiOnly, m.Dns, m.Telegram, m.Zapret)).ToList();

    /// <summary>Карточка «Режим» для Главной: заголовок с пояснением и четыре плитки.</summary>
    private Border BuildDashboardModes()
    {
        var box = new StackPanel();
        _dashboardModeState = Ui.Hint("", 0);
        box.Children.Add(DashboardPanelHeader("Режим", _dashboardModeState,
            DashboardHeaderButton("Что меняет каждый режим", OpenModeWindow)));
        DashboardHelp((DockPanel)box.Children[0], "Режим", "Включает нужный набор одним щелчком.\n«Только ИИ»: нейросети через Smart DNS, остальное как было.\n«Все сразу»: еще быстрый Telegram и обход замедления YouTube и Discord.\n«Игры»: обход и для игр, DNS и Telegram не меняются.\n«Все выключить»: DNS вашей сети, ускорение Telegram и обход выключены. Верхний переключатель выключает только Smart DNS.");
        _dashboardModeGrid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -8, 0) }; // край плиток вровень с краем карточки
        for (int i = 0; i < Modes.Length; i++)
        {
            int id = i;
            var m = Modes[i];
            var icon = new Controls.Icon { Kind = m.Icon, Size = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var title = new TextBlock { Text = m.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var sub = Ui.Hint(ModeShort[i], 1).With(t => { t.FontSize = 12; t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(title); texts.Children.Add(sub);
            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(texts);
            var tile = new Controls.ClickTile
            {
                Child = row, Padding = DashboardTilePad, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0), ToolTip = m.Hint,
            };
            AutomationProperties.SetName(tile, "Режим: " + m.Title);
            tile.Click += (_, _) => _ = DashboardModeAsync(id);
            _dashboardModeGrid.Children.Add(tile);
            _dashboardModeTiles.Add((tile, icon, title, sub));
        }
        box.Children.Add(_dashboardModeGrid);
        var card = Ui.Card(box, bottom: 0);
        card.Padding = new Thickness(14, 10, 14, 6);
        card.Margin = new Thickness(0, 8, 0, 0);
        return card;
    }

    /// <summary>Подсветка текущего режима, подписи и доступность плиток. Вызывается из RefreshDashboard.</summary>
    private void RefreshDashboardModes()
    {
        if (_dashboardModeTiles.Count == 0) return;
        int? current = ModeWindow.Current(ModeInfos, ModeState());
        bool busy = _dashboardModeRunning != null || _modeBusy || _busyConn;
        for (int i = 0; i < _dashboardModeTiles.Count; i++)
        {
            var (tile, icon, title, sub) = _dashboardModeTiles[i];
            bool cur = current == i, running = _dashboardModeRunning == i;
            tile.SetResourceReference(Border.BackgroundProperty, cur || running ? "AccentSoft" : "Surface2");
            if (cur || running) tile.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else { tile.ClearValue(Border.BorderBrushProperty); tile.BorderBrush = Brushes.Transparent; } // рамка только у включенного режима
            icon.SetResourceReference(Controls.Icon.ForegroundProperty, cur || running ? "Accent" : "Muted");
            Ui.SwapText(sub, running ? "Включаю…" : cur ? "сейчас включен" : ModeShort[i]);
            sub.SetResourceReference(TextBlock.ForegroundProperty, cur ? "Accent" : "Muted");
            tile.IsHitTestVisible = !busy && !cur; // занято или уже включен: нажатия не принимаются, вид не тускнеет
        }
        if (_dashboardModeRunning is { } r) return; // ход работы пишет сам DashboardModeAsync
        _dashboardModeState.Text = current is { } c ? $"Сейчас: {Modes[c].Title}" : "Сейчас: свой набор, часть включена отдельно";
    }

    private async Task DashboardModeAsync(int id)
    {
        if (_dashboardModeRunning != null || _modeBusy || _busyConn) { ShowToast("Подождите: предыдущее действие еще идет", "Warn"); return; }
        if (ModeWindow.Current(ModeInfos, ModeState()) == id) return;
        _dashboardModeRunning = id;
        _dashboardModeState.Text = $"Включаю «{Modes[id].Title}»…";
        RefreshDashboard();
        try { await ApplyModeAsync(Modes[id].Mode, step => Dispatcher.Invoke(() => _dashboardModeState.Text = $"«{Modes[id].Title}»: {step}…")); }
        finally { _dashboardModeRunning = null; RefreshDashboard(); }
    }
}
