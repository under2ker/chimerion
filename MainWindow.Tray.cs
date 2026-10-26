using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Значок в трее и его меню</summary>
public partial class MainWindow
{
    private System.Windows.Forms.ToolStripMenuItem? _trayTg, _trayZapret;


    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripLabel? _trayStatus;
    private bool _exitRequested;
    private bool _sessionEnding;
    private TrayPingStrip? _trayPing;
    private System.Windows.Forms.ToolStripLabel? _trayStab;
    private TrayServiceStrip? _traySvc;
    private NetSnapshot? _traySnap;
    private bool _trayBusy;

    /// <summary>Фоновая проверка сервисов для трея: раз в 5 минут. Таймер перезапускается после каждой проверки.</summary>
    private readonly DispatcherTimer _trayTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    private void SetTraySnapshot(NetSnapshot? snap)
    {
        if (snap != null)
        {
            _traySnap = snap;
            SyncActiveSummary(snap);
            CheckEgressChange(snap);
        }
        UpdateTrayServices();
        UpdateHeaderStats();
        if (snap != null)
        {
            bool connected = DnsSwitcher.GetActive() != null;
            // Пока подключено, проверка каждые 5 минут обновляет и блок «Проверка подключения» в полном виде
            if (connected && !_compact && !_busyConn) _after = snap;
            if (_compact || connected) ShowCompare();
        }
        _trayTimer.Stop();
        _trayTimer.Start();
    }

    /// <summary>
    /// Результат проверки подключения пишем в строку активного сервера: таблица показывает то, что видит пользователь
    /// при подключении, а не старую проверку сервера. Остальные серверы не меняются.
    /// </summary>
    private void SyncActiveSummary(NetSnapshot snap)
    {
        if (snap.Services.Count == 0) return;
        var active = DnsSwitcher.GetActive();
        var srv = active == null ? null : _servers.FirstOrDefault(x => x.Id == active.ServerId);
        if (srv == null) return;
        string Mark(ServiceState x) => x.Access.StartsWith("✓") ? "✓" : x.Access.Contains("закрыт") ? "✗ регион" : "✗";
        srv.Summary = string.Join(" · ", snap.Services.Select(x => $"{x.Name} {Mark(x)}"));
        srv.Status = snap.Services.All(x => x.Access.StartsWith("✓")) ? ServerStatus.Ok
                   : snap.Services.Any(x => x.Access.StartsWith("✓")) ? ServerStatus.Partial
                   : ServerStatus.Fail;
    }

    /// <summary>Проверяет сервисы в фоне для трея. Повторный вызов во время проверки пропускается.</summary>
    private async Task RefreshTrayServicesAsync()
    {
        if (_trayBusy) return;
        _trayBusy = true;
        try
        {
            SetTraySnapshot(await ConnectionProbe.TakeAsync(_settings));
        }
        catch (Exception ex)
        {
            Log("Трей: проверка сервисов не удалась: " + ex.Message);
        }
        finally
        {
            _trayBusy = false;
            if (!_trayTimer.IsEnabled) _trayTimer.Start(); // после ошибки отсчет все равно идет
        }
    }

    private void SetupTray()
    {
        // Меню узкое и высокое: ИИ-сервисы — ряд значков в одну строку, пинг и стабильность — отдельными строками.
        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
            BackColor = Palette.Get("MenuBg"),
            ForeColor = Palette.Get("Text"),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            // Без боковых отступов: пункты меню WinForms ставит от левого края, надписи — с отступом меню.
            // Одинаковая ширина и ноль по бокам выравнивают все строки по одной линии
            Padding = new System.Windows.Forms.Padding(0, 4, 0, 4),
        };
        menu.Opened += (_, _) => TrayMenu.Round(menu);
        var pad = new System.Windows.Forms.Padding(12, 8, 12, 8);
        // Строки статуса не выделяются при наведении: это надписи, а не пункты.
        _trayStatus = new System.Windows.Forms.ToolStripLabel("Не подключено") { Padding = pad, Image = TrayMenu.Dot(Palette.Get("Muted")),
            DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.ImageAndText, ImageAlign = System.Drawing.ContentAlignment.MiddleLeft,
            TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText };
        menu.Items.Add(_trayStatus);
        _trayPing = new TrayPingStrip();
        menu.Items.Add(new System.Windows.Forms.ToolStripControlHost(_trayPing)
        {
            AutoSize = false,
            Size = _trayPing.Size,
            Margin = System.Windows.Forms.Padding.Empty,
            Padding = System.Windows.Forms.Padding.Empty,
            BackColor = Palette.Get("MenuBg"),
        });
        _trayStab = new System.Windows.Forms.ToolStripLabel("Стабильность: —") { Padding = pad };
        menu.Items.Add(_trayStab);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _traySvc = new TrayServiceStrip(_settings.Targets.Where(x => x.Enabled).Select(x => x.Name));
        menu.Items.Add(new System.Windows.Forms.ToolStripControlHost(_traySvc)
        {
            AutoSize = false,
            Size = _traySvc.Size,
            Margin = System.Windows.Forms.Padding.Empty,
            Padding = System.Windows.Forms.Padding.Empty,
            BackColor = Palette.Get("MenuBg"),
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem("Открыть", () => ShowFromTray()));
        menu.Items.Add(TrayItem("Переключить на лучший", () => _ = SwitchToBestAsync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayTg = TrayItem("Telegram: включить", () => _ = ToggleToolFromTrayAsync(_tools[0]));
        _trayZapret = TrayItem("YouTube и Discord: включить", () => _ = ToggleToolFromTrayAsync(_tools[1]));
        menu.Items.Add(_trayTg);
        menu.Items.Add(_trayZapret);
        // Режимы в один клик: подменю «Режим»
        var modes = new System.Windows.Forms.ToolStripMenuItem("Режим") { Padding = new System.Windows.Forms.Padding(12, 8, 12, 8) };
        modes.DropDown.BackColor = menu.BackColor;
        modes.DropDown.ForeColor = menu.ForeColor;
        modes.DropDown.Renderer = menu.Renderer;
        modes.DropDown.Padding = new System.Windows.Forms.Padding(0, 4, 0, 4);
        // Подменю без пустого поля под картинки (из-за него текст стоял далеко от края) и в том же виде, что меню
        if (modes.DropDown is System.Windows.Forms.ToolStripDropDownMenu ddm) { ddm.ShowImageMargin = false; ddm.ShowCheckMargin = false; }
        modes.DropDown.Opened += (_, _) => TrayMenu.Round(modes.DropDown);
        foreach (var (mode, title, hint) in Modes)
        {
            var mi = TrayItem(title, () => _ = ApplyModeAsync(mode));
            mi.ToolTipText = hint;
            modes.DropDownItems.Add(mi);
        }
        menu.Items.Add(modes);
        menu.Opening += (_, _) => { UpdateToolsTray(); TranslateTray(); }; // состояние могло поменяться само (программу закрыли вручную)
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(TrayItem("Выход", ExitFromTray));
        // Ширина пунктов задана явно: раньше пункты выходили шире меню (291 против 264) и стрелка подменю обрезалась
        FixWidths(menu.Items, TrayWidth.Menu);
        FixWidths(modes.DropDownItems, 190);
        menu.Opening += (_, _) => FitMenu(menu, TrayWidth.Menu);
        modes.DropDown.Opening += (_, _) => FitMenu(modes.DropDown, 190);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = Controls.Brand.TrayIcon(), // знак в текущих цветах, как в окне
            Text = "CHIMERION: не подключено",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        // Щелчок левой кнопкой открывает окно (как у других программ в трее), правая — меню
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
        _trayTimer.Tick += async (_, _) =>
        {
            // Проверка по таймеру (по умолчанию каждые 5 минут) нужна, пока CHIMERION подключена. Без подключения
            // таймер обновляет только компактный блок, если он открыт. После выхода из программы проверок нет.
            if (DnsSwitcher.GetActive() == null && !(_compact && IsVisible)) return;
            await RefreshTrayServicesAsync();
        };
        UpdateTrayServices();
    }

    /// <summary>
    /// Ширина строк меню трея. Пункты меню WinForms ставит от левого края, надписи на 8 правее: надписи на 8 уже,
    /// а их текст на 8 левее, чтобы все начиналось с одной линии.
    /// </summary>
    private static void FixWidths(System.Windows.Forms.ToolStripItemCollection items, int width)
    {
        foreach (System.Windows.Forms.ToolStripItem i in items)
        {
            if (i is System.Windows.Forms.ToolStripMenuItem)
            {
                i.AutoSize = false;
                i.Size = new System.Drawing.Size(width, 34);
            }
            else if (i is System.Windows.Forms.ToolStripLabel)
            {
                i.AutoSize = false;
                i.Size = new System.Drawing.Size(width - 8, 32);
                i.Padding = new System.Windows.Forms.Padding(6, 8, 12, 8); // 8 (сдвиг WinForms) + 6 = линия текста пунктов (14)
                i.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            }
        }
    }

    /// <summary>Окно меню ровно по ширине пунктов: без пустой полосы справа, подменю открывается вплотную.</summary>
    private static void FitMenu(System.Windows.Forms.ToolStripDropDown dd, int width)
    {
        dd.AutoSize = true;
        dd.PerformLayout();
        var h = dd.Height;
        dd.AutoSize = false;
        dd.Size = new System.Drawing.Size(width + 2, h);
    }

    private static System.Windows.Forms.ToolStripMenuItem TrayItem(string text, Action onClick)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text) { Padding = new System.Windows.Forms.Padding(12, 8, 12, 8) };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Статус, пинг и стабильность в меню трея и подсказка при наведении.</summary>
    private void UpdateTray(DnsServer? srv, string? name)
    {
        if (_trayStatus == null || _tray == null) return;
        _trayStatus.Text = name == null ? "Не подключено" : DnsServer.Clip(name, 26);
        _trayStatus.ForeColor = Palette.Get(name == null ? "Muted" : "Ok");
        _trayStatus.Image = TrayMenu.Dot(Palette.Get(name == null ? "Muted" : "Ok"));
        if (_trayPing != null)
            _trayPing.SetPing(srv?.LatencyMs, srv?.LatencyMs == null ? "Пинг: —" : $"Пинг: {srv.PingText} мс");
        if (_trayStab != null)
            _trayStab.Text = srv == null ? "Стабильность: —" : $"Стабильность: {srv.Stability}";
        var tip = name == null ? "CHIMERION: не подключено" : $"CHIMERION: подключено, {name}";
        _tray.Text = tip.Length <= 63 ? tip : tip[..63]; // подсказка трея не длиннее 63 символов
    }

    /// <summary>Строки ИИ-сервисов: доступен или нет по последней проверке подключения.</summary>
    private void UpdateTrayServices()
    {
        if (_traySvc == null) return;
        var snap = _traySnap;
        foreach (var name in _traySvc.Names)
        {
            var svc = snap?.Services.FirstOrDefault(x => x.Name == name);
            // 0 — не проверено, 1 — доступен, 2 — недоступен
            var state = svc == null ? 0 : svc.Access.StartsWith("✓") ? 1 : 2;
            _traySvc.SetState(name, state);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_compact) _ = RefreshTrayServicesAsync(); // открыли окно — сервисы проверяем заново
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Исходный русский текст пунктов меню трея (WinForms: Loc туда не достает).</summary>
    private readonly Dictionary<System.Windows.Forms.ToolStripItem, string> _trayRu = new();

    /// <summary>Пункты меню трея на текущем языке. Вызывается при открытии меню и при смене языка.</summary>
    private void TranslateTray()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        void Walk(System.Windows.Forms.ToolStripItemCollection items)
        {
            foreach (System.Windows.Forms.ToolStripItem i in items)
            {
                if (string.IsNullOrEmpty(i.Text)) { }
                else if (!_trayRu.TryGetValue(i, out var ru) || (i.Text != ru && i.Text != Loc.T(ru))) _trayRu[i] = ru = i.Text;
                if (_trayRu.TryGetValue(i, out var orig)) i.Text = Loc.T(orig);
                if (i is System.Windows.Forms.ToolStripDropDownItem dd) Walk(dd.DropDownItems);
            }
        }
        Walk(menu.Items);
    }
}
