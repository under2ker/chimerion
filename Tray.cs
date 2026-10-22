using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Dnex;

/// <summary>Меню трея в палитре программы: темная подложка, те же цвета выделения и границ, что в окне.</summary>
internal sealed class TrayRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Hover = ColorTranslator.FromHtml("#2E3442");
    private static readonly Color Accent = ColorTranslator.FromHtml("#A3E635");
    private static readonly Color Text = ColorTranslator.FromHtml("#E7E9EE");

    public TrayRenderer() : base(new TrayColors()) { }

    // Выделение пункта рисуем сами: стандартное в Windows светлое, с белым текстом.
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            base.OnRenderMenuItemBackground(e);
            return;
        }
        using var bg = new SolidBrush(Hover);
        e.Graphics.FillRectangle(bg, 0, 0, e.Item.Width, e.Item.Height);
        using var bar = new SolidBrush(Accent);
        e.Graphics.FillRectangle(bar, 0, 0, 3, e.Item.Height);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item.Selected && e.Item.Enabled) e.TextColor = Text;
        base.OnRenderItemText(e);
    }
}

internal sealed class TrayColors : ProfessionalColorTable
{
    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public override Color ToolStripDropDownBackground => C("#1C1F26");
    public override Color MenuBorder => C("#2C313C");
    public override Color MenuItemBorder => C("#2B303C");
    public override Color MenuItemSelected => C("#2B303C");
    public override Color MenuItemSelectedGradientBegin => C("#2B303C");
    public override Color MenuItemSelectedGradientEnd => C("#2B303C");
    public override Color MenuItemPressedGradientBegin => C("#203A66");
    public override Color MenuItemPressedGradientEnd => C("#203A66");
    public override Color ImageMarginGradientBegin => C("#1C1F26");
    public override Color ImageMarginGradientMiddle => C("#1C1F26");
    public override Color ImageMarginGradientEnd => C("#1C1F26");
    public override Color SeparatorDark => C("#2C313C");
    public override Color SeparatorLight => C("#1C1F26");
}

/// <summary>Строка пинга в меню трея: значок связи и текст. Уровень столбиков тот же, что в окне (см. PingBars).</summary>
internal sealed class TrayPingStrip : Control
{
    private long? _ms;
    private string _text = "Пинг: —";
    private static readonly Color TextColor = ColorTranslator.FromHtml("#E7E9EE");
    private static readonly int[] Heights = { 5, 8, 11, 14 }; // целые пиксели, как в PingBars

    public TrayPingStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ColorTranslator.FromHtml("#1C1F26");
        Font = new Font("Segoe UI", 9.5f);
        Size = new Size(228, 28);
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
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var (lit, brush) = Dnex.Controls.PingBars.Level(_ms);
        var wpf = ((System.Windows.Media.SolidColorBrush)brush).Color;
        var color = Color.FromArgb(wpf.A, wpf.R, wpf.G, wpf.B);
        var off = ColorTranslator.FromHtml("#3A404C");
        var bottom = ClientSize.Height / 2 + 7;
        const int barWidth = 3, gap = 2;
        int x = 10;
        for (var i = 0; i < Heights.Length; i++)
        {
            using var br = new SolidBrush(i < lit ? color : off);
            g.FillRectangle(br, x, bottom - Heights[i], barWidth, Heights[i]);
            x += barWidth + gap;
        }

        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        using var tb = new SolidBrush(TextColor);
        g.DrawString(_text, Font, tb, new RectangleF(x + 6, 0, ClientSize.Width - x - 6, ClientSize.Height), sf);
    }
}

/// <summary>Ряд ИИ-сервисов в меню трея: точка статуса и название, в одну строку по горизонтали.</summary>
internal sealed class TrayServiceStrip : Control
{
    /// <summary>0 — не проверено, 1 — доступен, 2 — недоступен.</summary>
    private readonly List<string> _names;
    private readonly int[] _state;
    private static readonly Color OkColor = ColorTranslator.FromHtml("#3DD68C");
    private static readonly Color BadColor = ColorTranslator.FromHtml("#F2626E");
    private static readonly Color UnknownColor = ColorTranslator.FromHtml("#8C93A3");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#E7E9EE");

    public IReadOnlyList<string> Names => _names;

    public TrayServiceStrip(IEnumerable<string> names)
    {
        _names = names.ToList();
        _state = new int[_names.Count];
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ColorTranslator.FromHtml("#1C1F26");
        Font = new Font("Segoe UI", 9f);
        Size = new Size(228, 28);
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
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        if (Names.Count == 0) return;

        var cell = ClientSize.Width / (float)Names.Count;
        var cy = ClientSize.Height / 2f;
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        for (var i = 0; i < Names.Count; i++)
        {
            var x = i * cell + 8;
            var dot = _state[i] switch { 1 => OkColor, 2 => BadColor, _ => UnknownColor };
            using (var br = new SolidBrush(dot)) g.FillEllipse(br, x, cy - 4, 8, 8);
            using var tb = new SolidBrush(TextColor);
            g.DrawString(Names[i], Font, tb, new RectangleF(x + 12, 0, cell - 14, ClientSize.Height), sf);
        }
    }
}
