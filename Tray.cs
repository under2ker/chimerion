using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Dnex;

/// <summary>
/// Цвета палитры программы (App.xaml) для меню трея на WinForms: те же ключи, что в окнах, без своих копий оттенков.
/// </summary>
internal static class Palette
{
    public static Color Get(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b
            ? Color.FromArgb(b.Color.A, b.Color.R, b.Color.G, b.Color.B)
            : Color.Gray;
}

/// <summary>
/// Меню трея в стиле меню окон программы (App.xaml, ContextMenu/MenuItem): фон «Меню и подменю», скругленная подсветка
/// пункта, тонкие разделители с отступами, стрелка подменю как в окнах. Углы и рамку окна меню скругляет Windows 11
/// (TrayMenu.Round).
/// </summary>
internal sealed class TrayRenderer : ToolStripProfessionalRenderer
{
    private static Color Bg => Palette.Get("MenuBg");
    private static Color Hover => Palette.Get("MenuHover");
    private static Color Text => Palette.Get("Text");
    private static Color Muted => Palette.Get("Muted");
    private static Color Line => Palette.Get("Border");

    public TrayRenderer() : base(new TrayColors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(Bg);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    // Рамку рисует Windows (скругленная, цвета Border), своя прямоугольная не нужна
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!(e.Item.Selected || (e.Item is ToolStripMenuItem { DropDown.Visible: true })) || !e.Item.Enabled) return;
        // Подсветка как у пункта меню в окне: скругленный прямоугольник с отступом от краев
        // Симметрично: отступ 6 слева и справа, 2 сверху и снизу, текст по центру внутри
        var r = new Rectangle(6, 2, e.Item.Width - 12, e.Item.Height - 4);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(r, 5);
        using var b = new SolidBrush(Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Muted : e.Item.ForeColor == Color.Empty || e.Item is ToolStripMenuItem ? Text : e.Item.ForeColor;
        // Значок (его название лежит в Tag пункта) в общей колонке; пункт со значком ставит текст в колонку текста,
        // без значка (подменю без картинок) текст стоит на краю колонки значков
        var kind = e.Item.Tag as string;
        var hasIcon = kind != null;
        if (hasIcon) DrawIcon(e, kind!);
        var left = (hasIcon ? TrayLayout.TextX : TrayLayout.IconX) - e.Item.Bounds.X; // Bounds.X: пункт и надпись WinForms ставит с разным отступом
        var right = e.Item is ToolStripMenuItem { HasDropDownItems: true } ? 30 : 14;
        // Текст по центру строки: высота пунктов задана явно, а WinForms ставил текст к верху, и подсветка
        // выглядела сдвинутой относительно надписи. NoPadding: без своего отступа слева, чтобы текст начинался ровно в колонке
        e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom | TextFormatFlags.HorizontalCenter | TextFormatFlags.Right | TextFormatFlags.LeftAndRightPadding))
                       | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        e.TextRectangle = new Rectangle(left, 0, Math.Max(10, e.Item.Width - left - right), e.Item.Height);
        base.OnRenderItemText(e);
        // Стрелка подменю у правого края: при заданной ширине пункта WinForms место под нее не оставляет
        if (e.Item is ToolStripMenuItem { HasDropDownItems: true } mi) DrawChevron(e.Graphics, mi.Width - 18, mi.Height / 2f);
    }

    /// <summary>Значок или точка статуса в колонке значков: по центру строки, цвет как у текста пункта.</summary>
    private static void DrawIcon(ToolStripItemTextRenderEventArgs e, string kind)
    {
        var color = !e.Item.Enabled ? Muted : e.Item.ForeColor == Color.Empty || e.Item is ToolStripMenuItem ? Text : e.Item.ForeColor;
        var x = TrayLayout.IconX - e.Item.Bounds.X;
        var y = (e.Item.Height - TrayIcons.Size) / 2;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (kind == "dot")
        {
            // Точка статуса соединения: круг 8 px посередине колонки значков
            using var b = new SolidBrush(color);
            e.Graphics.FillEllipse(b, x + 4, y + 4, 8, 8);
        }
        else if (TrayIcons.Get(kind, color) is { } bmp)
            e.Graphics.DrawImage(bmp, x, y, TrayIcons.Size, TrayIcons.Size);
    }

    private static void DrawChevron(Graphics g, float cx, float cy)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(Muted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(p, new[] { new PointF(cx - 2, cy - 4), new PointF(cx + 2, cy), new PointF(cx - 2, cy + 4) });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var p = new Pen(Line);
        e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
    }

    /// <summary>Стандартную стрелку не рисуем: своя, тонкий уголок как chevron в меню окон (OnRenderItemText).</summary>
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Окно меню трея: скругленные углы и рамка цвета Border средствами Windows 11 (на Windows 10 углы прямые).</summary>
internal static class TrayMenu
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Round(ToolStripDropDown dd)
    {
        try
        {
            int round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE (33) = DWMWCP_ROUND
            DwmSetWindowAttribute(dd.Handle, 33, ref round, sizeof(int));
            var c = Palette.Get("Border");
            int border = c.R | (c.G << 8) | (c.B << 16); // DWMWA_BORDER_COLOR (34), COLORREF
            DwmSetWindowAttribute(dd.Handle, 34, ref border, sizeof(int));
        }
        catch (Exception) { /* старая Windows: обычные углы */ }
    }
}

internal sealed class TrayColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Palette.Get("MenuBg");
    public override Color MenuBorder => Palette.Get("Border");
    public override Color MenuItemBorder => Palette.Get("MenuHover");
    public override Color MenuItemSelected => Palette.Get("MenuHover");
    public override Color MenuItemSelectedGradientBegin => Palette.Get("MenuHover");
    public override Color MenuItemSelectedGradientEnd => Palette.Get("MenuHover");
    public override Color MenuItemPressedGradientBegin => Palette.Get("MenuHover");
    public override Color MenuItemPressedGradientEnd => Palette.Get("MenuHover");
    public override Color ImageMarginGradientBegin => Palette.Get("MenuBg");
    public override Color ImageMarginGradientMiddle => Palette.Get("MenuBg");
    public override Color ImageMarginGradientEnd => Palette.Get("MenuBg");
    public override Color SeparatorDark => Palette.Get("Border");
    public override Color SeparatorLight => Palette.Get("MenuBg");
}

/// <summary>Строка пинга в меню трея: значок связи и текст. Уровень столбиков тот же, что в окне (см. PingBars).</summary>
internal sealed class TrayPingStrip : Control
{
    private long? _ms;
    private string _text = "Пинг: —";
    private static Color TextColor => Palette.Get("Text");
    private static readonly int[] Heights = { 5, 8, 11, 14 }; // целые пиксели, как в PingBars

    public TrayPingStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Get("MenuBg");
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(TrayWidth.Inner, 28);
    }

    public void SetPing(long? ms, string text)
    {
        _ms = ms;
        _text = text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Palette.Get("MenuBg"));

        var (lit, brush) = Dnex.Controls.PingBars.Level(_ms);
        var wpf = ((System.Windows.Media.SolidColorBrush)brush).Color;
        var color = Color.FromArgb(wpf.A, wpf.R, wpf.G, wpf.B);
        var off = Palette.Get("ScrollThumb");
        var bottom = ClientSize.Height / 2 + 7;
        const int barWidth = 3, gap = 1; // четыре столбика 15 px: помещаются в колонку значков (16 px)
        int x = TrayLayout.IconX - Left; // Left: полосу WinForms ставит правее края меню, считаем от края меню
        for (var i = 0; i < Heights.Length; i++)
        {
            using var br = new SolidBrush(i < lit ? color : off);
            g.FillRectangle(br, x, bottom - Heights[i], barWidth, Heights[i]);
            x += barWidth + gap;
        }

        // Текст в колонке текста: на одной линии с «Стабильность» и надписями пунктов
        var left = TrayLayout.TextX - Left;
        TextRenderer.DrawText(g, Dnex.Services.Loc.T(_text), Font, new Rectangle(left, 0, ClientSize.Width - left, ClientSize.Height), TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis); // на языке интерфейса
    }
}

/// <summary>Ряд ИИ-сервисов в меню трея: точка статуса и название, в одну строку по горизонтали.</summary>
internal sealed class TrayServiceStrip : Control
{
    /// <summary>0 — не проверено, 1 — доступен, 2 — недоступен.</summary>
    private readonly List<string> _names;
    private readonly int[] _state;
    private static Color OkColor => Palette.Get("Ok");
    private static Color BadColor => Palette.Get("Bad");
    private static Color UnknownColor => Palette.Get("Muted");
    private static Color TextColor => Palette.Get("Text");

    public IReadOnlyList<string> Names => _names;

    public TrayServiceStrip(IEnumerable<string> names)
    {
        _names = names.ToList();
        _state = new int[_names.Count];
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Get("MenuBg");
        Font = new Font("Segoe UI", 9f);
        Size = new Size(TrayWidth.Inner, 28);
    }

    public void SetState(string name, int state)
    {
        var i = _names.IndexOf(name);
        if (i < 0 || _state[i] == state) return;
        _state[i] = state;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Palette.Get("MenuBg"));
        if (Names.Count == 0) return;

        // Первая точка стоит по центру колонки значков (как значки пунктов ниже), остальные делят строку поровну
        var start = TrayLayout.IconX + 4 - Left;
        var cell = (ClientSize.Width - start - 6) / (float)Names.Count;
        var cy = ClientSize.Height / 2f;
        for (var i = 0; i < Names.Count; i++)
        {
            var x = start + i * cell;
            var dot = _state[i] switch { 1 => OkColor, 2 => BadColor, _ => UnknownColor };
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, x, cy - 4, 8, 8);
            TextRenderer.DrawText(g, Names[i], Font, new Rectangle((int)x + 12, 0, (int)cell - 14, ClientSize.Height), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>
/// Сетка меню трея: колонка значков (значок 16 px, у точки статуса и столбиков пинга тоже) и колонка текста правее нее.
/// Все строки (статус, пинг, стабильность, сервисы, пункты) считаются от края меню, поэтому левый край общий.
/// </summary>
internal static class TrayLayout
{
    public const int IconX = 14;
    public const int TextX = IconX + TrayIcons.Size + 10;
}

/// <summary>Ширина меню трея: пункты, строки статуса и подменю выравниваются по ней.</summary>
internal static class TrayWidth
{
    public const int Menu = 268;
    /// <summary>Надписи и полосы WinForms ставит с отступом 8 от левого края меню (пункты меню — от края).</summary>
    public const int Inner = Menu - 8;
}
