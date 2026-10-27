using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Dnex.Controls;

/// <summary>
/// Анимированные иконки useanimations.com (MIT, https://useanimations.com), запеченные в кадры скриптом
/// tools/icons/bake.py (Assets/animated-icons.json.gz). Кадр — набор контуров с обводкой или заливкой в поле
/// Size×Size; цвет задает тот, кто рисует, поэтому иконки следуют за темой. Кадры иконки разбираются при первом показе.
/// </summary>
public static class AnimatedIcons
{
    public sealed record Item(bool Fill, double Width, double Opacity, Geometry Geometry, Geometry[] Clips);

    public sealed class Anim
    {
        public double Fps { get; init; }
        public double Size { get; init; }
        /// <summary>В покое показывается последний кадр (галочка, крестик), а не первый.</summary>
        public bool RestAtEnd { get; init; }
        public Item[][] Frames { get; init; } = Array.Empty<Item[]>();
        public int RestFrame => RestAtEnd ? Frames.Length - 1 : 0;
    }

    private static Dictionary<string, JsonElement>? _raw;
    private static readonly Dictionary<string, Anim?> Parsed = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> Raw()
    {
        if (_raw != null) return _raw;
        _raw = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = typeof(AnimatedIcons).Assembly.GetManifestResourceStream("animated-icons.json.gz");
            if (s == null) return _raw;
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(gz);
            foreach (var p in doc.RootElement.GetProperty("icons").EnumerateObject()) _raw[p.Name] = p.Value.Clone();
        }
        catch (Exception) { /* без файла иконки остаются обычными */ }
        return _raw;
    }

    /// <summary>Анимация иконки или null, если для нее анимации нет.</summary>
    public static Anim? Get(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        if (Parsed.TryGetValue(kind, out var cached)) return cached;
        Anim? anim = null;
        if (Raw().TryGetValue(kind, out var e))
        {
            try
            {
                var frames = new List<Item[]>();
                foreach (var f in e.GetProperty("frames").EnumerateArray())
                {
                    var items = new List<Item>();
                    foreach (var it in f.EnumerateArray())
                    {
                        var path = it[3].GetString() ?? "";
                        if (path.Length == 0) continue;
                        var geo = Geometry.Parse(path);
                        geo.Freeze();
                        var clips = it.GetArrayLength() > 4
                            ? it[4].EnumerateArray().Select(c => { var g = Geometry.Parse(c.GetString()!); g.Freeze(); return g; }).ToArray()
                            : Array.Empty<Geometry>();
                        items.Add(new Item(it[0].GetString() == "f", it[1].GetDouble(), it[2].GetDouble(), geo, clips));
                    }
                    frames.Add(items.ToArray());
                }
                if (frames.Count > 0)
                    anim = new Anim
                    {
                        Fps = e.GetProperty("fps").GetDouble(), Size = e.GetProperty("size").GetDouble(),
                        RestAtEnd = e.GetProperty("rest").GetString() == "end", Frames = frames.ToArray(),
                    };
            }
            catch (Exception) { anim = null; }
        }
        Parsed[kind] = anim;
        return anim;
    }

    /// <summary>Нарисовать кадр цветом brush в поле size×size.</summary>
    public static void Draw(DrawingContext dc, Anim anim, int frame, Brush brush, double size)
    {
        var items = anim.Frames[Math.Clamp(frame, 0, anim.Frames.Length - 1)];
        double k = size / anim.Size;
        dc.PushTransform(new ScaleTransform(k, k));
        foreach (var it in items)
        {
            foreach (var clip in it.Clips) dc.PushClip(clip);
            if (it.Opacity < 0.999) dc.PushOpacity(it.Opacity);
            if (it.Fill) dc.DrawGeometry(brush, null, it.Geometry);
            else
            {
                var pen = new Pen(brush, it.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, pen, it.Geometry);
            }
            if (it.Opacity < 0.999) dc.Pop();
            foreach (var _ in it.Clips) dc.Pop();
        }
        dc.Pop();
    }
}

/// <summary>Кадр анимированной иконки: рисует текущий кадр цветом Foreground иконки.</summary>
internal sealed class AnimatedGlyph : FrameworkElement
{
    private readonly Icon _owner;
    public AnimatedIcons.Anim Anim { get; }
    private int _frame;

    public AnimatedGlyph(Icon owner, AnimatedIcons.Anim anim)
    {
        _owner = owner;
        Anim = anim;
        _frame = anim.RestFrame;
        Width = Height = anim.Size;
    }

    public int Frame
    {
        get => _frame;
        set { if (_frame == value) return; _frame = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc) => AnimatedIcons.Draw(dc, Anim, _frame, _owner.Foreground, Anim.Size);
}
