using System.Windows;
using System.Windows.Media;

namespace Dnex.Services;

/// <summary>
/// Свои цвета интерфейса. Пользователь выбирает несколько основных цветов, а оттенки наведения, рамок, фокуса и
/// подложек выводятся из них сами: так состояния (наведение, нажатие, выделение) не спорят с выбором.
/// Цвет текста на кнопках и галочках подбирается по контрасту (белый или темный).
/// Применение: кисти в Application.Resources заменяются новыми, XAML ссылается на них через DynamicResource,
/// поэтому все окна меняют цвет сразу, без перезапуска.
/// </summary>
public static class ThemeColors
{
    /// <summary>Настраиваемый цвет: ключ палитры, название и где он виден.</summary>
    /// <param name="LightDefault">Значение для светлой темы; пусто — выводится из цвета акцента.</param>
    public sealed record Slot(string Key, string Title, string Where, string Default, string LightDefault = "");

    public static readonly IReadOnlyList<Slot> Slots = new[]
    {
        new Slot("Accent", "Акцент", "ссылки, выбранные карточки, рамки фокуса, значки, полосы логотипа", "#C5F23A", "#C5F23A"),
        new Slot("AccentButton", "Основные кнопки", "«Подключить», «Сохранить», «Найти новые серверы»", "#C5F23A", "#C5F23A"),
        new Slot("StrongButton", "Дополнительные кнопки", "«Перепроверить», «Проверить сейчас», «Рекомендуемые»", "#23262A", "#ECEEE8"),
        new Slot("ToggleOn", "Галочки и переключатели", "отмеченные галочки во всех окнах", "#C5F23A", "#B8E52C"),
        new Slot("AccentSoft", "Активная вкладка", "«Рабочие», «Общий DNS» и другие вкладки", "#2A3413"),
        new Slot("Selection", "Выделенные строки", "выделение в таблице серверов и журнале", "#2B3816"),
        new Slot("MenuBg", "Меню и подменю", "меню по правой кнопке, «Режим», «Выделить», меню в трее", "#1C1E21", "#FFFFFF"),
        new Slot("Lime", "Метки «рекомендуем»", "метки «рекомендуем», «лучший», подключенный сервер", "#C5F23A", "#C5F23A"),
    };

    /// <summary>Готовые наборы: расцветки логотипа CHIMERION. Цвета для слотов, которых нет в наборе, берутся стандартные.</summary>
    public static readonly IReadOnlyList<(string Name, Dictionary<string, string> Colors)> Presets = new (string, Dictionary<string, string>)[]
    {
        ("Стандартный", new()),
        ("Белый", new() { ["Accent"] = "#F5F5F0", ["AccentButton"] = "#E9E9E3", ["ToggleOn"] = "#F5F5F0", ["AccentSoft"] = "#2C2E31", ["Selection"] = "#303338", ["Lime"] = "#F5F5F0" }),
        ("Голубой", new() { ["Accent"] = "#4FE3F0", ["AccentButton"] = "#36CAD8", ["ToggleOn"] = "#4FE3F0", ["AccentSoft"] = "#123236", ["Selection"] = "#143A40", ["MenuBg"] = "#16202A", ["Lime"] = "#4FE3F0" }),
        ("Фиолетовый", new() { ["Accent"] = "#A78BFA", ["AccentButton"] = "#6D4BD8", ["ToggleOn"] = "#A78BFA", ["AccentSoft"] = "#2E2452", ["Selection"] = "#372C63", ["StrongButton"] = "#2A2833", ["Lime"] = "#A78BFA" }),
        ("Изумруд", new() { ["Accent"] = "#34D399", ["AccentButton"] = "#0B7A54", ["ToggleOn"] = "#34D399", ["AccentSoft"] = "#123B2E", ["Selection"] = "#17473A", ["Lime"] = "#34D399" }),
        ("Янтарь", new() { ["Accent"] = "#F5B03C", ["AccentButton"] = "#A8650A", ["ToggleOn"] = "#F5B03C", ["AccentSoft"] = "#3D2E12", ["Selection"] = "#4A3816", ["Lime"] = "#F5B03C" }),
        ("Монохром", new() { ["Accent"] = "#A3A8B0", ["AccentButton"] = "#4B5058", ["ToggleOn"] = "#A3A8B0", ["AccentSoft"] = "#2A2D31", ["Selection"] = "#33363B", ["Lime"] = "#C9CDD3" }),
        ("Розовый", new() { ["Accent"] = "#F472B6", ["AccentButton"] = "#BE3B84", ["ToggleOn"] = "#F472B6", ["AccentSoft"] = "#3D1A2E", ["Selection"] = "#4A2038", ["Lime"] = "#F472B6" }),
        ("Коралл", new() { ["Accent"] = "#FF8A65", ["AccentButton"] = "#C2502C", ["ToggleOn"] = "#FF8A65", ["AccentSoft"] = "#3E2219", ["Selection"] = "#4B291E", ["Lime"] = "#FF8A65" }),
        ("Синий", new() { ["Accent"] = "#60A5FA", ["AccentButton"] = "#2563EB", ["ToggleOn"] = "#60A5FA", ["AccentSoft"] = "#172A45", ["Selection"] = "#1B3354", ["MenuBg"] = "#171D27", ["Lime"] = "#60A5FA" }),
        ("Мята", new() { ["Accent"] = "#7FF0D2", ["AccentButton"] = "#17A383", ["ToggleOn"] = "#7FF0D2", ["AccentSoft"] = "#163A33", ["Selection"] = "#1A453C", ["Lime"] = "#7FF0D2" }),
        ("Рубин", new() { ["Accent"] = "#F87171", ["AccentButton"] = "#C0303A", ["ToggleOn"] = "#F87171", ["AccentSoft"] = "#3F1A1C", ["Selection"] = "#4C2023", ["StrongButton"] = "#2C2426", ["Lime"] = "#F87171" }),
    };

    /// <summary>Сменились цвета: открытые окна, которые рисуют части интерфейса кодом, перерисовывают их.</summary>
    public static event Action? Changed;

    public static bool TryParse(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim();
        if (!h.StartsWith('#')) h = "#" + h;
        if (h.Length != 7) return false; // только #RRGGBB: прозрачность в интерфейсе не нужна и ломала бы контраст
        try { c = (Color)ColorConverter.ConvertFromString(h); return true; }
        catch (FormatException) { return false; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Текущая тема: темная (по умолчанию) или светлая.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Ключ своего цвета в настройках: светлая тема хранит свои цвета отдельно, с приставкой «light:».</summary>
    public static string CustomKey(string key, bool dark) => dark ? key : "light:" + key;

    /// <summary>Свой цвет слота для темы или null.</summary>
    public static Color? Custom(IReadOnlyDictionary<string, string>? custom, string key, bool dark) =>
        custom != null && custom.TryGetValue(CustomKey(key, dark), out var hex) && TryParse(hex, out var c) ? c : null;

    /// <summary>Цвет слота: свой из настроек или стандартный (для темной темы).</summary>
    public static Color Get(IReadOnlyDictionary<string, string>? custom, string key) => Get(custom, key, true);

    public static Color Get(IReadOnlyDictionary<string, string>? custom, string key, bool dark)
    {
        if (Custom(custom, key, dark) is { } c) return c;
        var slot = Slots.First(s => s.Key == key);
        var def = dark || slot.LightDefault.Length == 0 ? slot.Default : slot.LightDefault;
        return TryParse(def, out var d) ? d : Colors.Gray;
    }

    /// <summary>
    /// Базовые цвета тем (фон, поверхности, текст, статусы, служебные). Настраиваемые слоты — в Slots.
    /// Светлая: почти белый фон, белые карточки, графитовый текст; статусы темнее, чтобы читались на белом.
    /// </summary>
    private static readonly Dictionary<string, string> DarkBase = new()
    {
        ["Bg"] = "#0F1012", ["Surface"] = "#17191C", ["Surface2"] = "#1F2125", ["SurfaceHover"] = "#272A2F", ["Border"] = "#2B2E33",
        ["Text"] = "#F3F4F1", ["Muted"] = "#8F949B", ["Idle"] = "#5C6373", ["Ok"] = "#3DD68C", ["Warn"] = "#F5B941", ["Bad"] = "#F2626E",
        ["OkSoft"] = "#13281E", ["WarnSoft"] = "#3A2D12", ["BadSoft"] = "#3A171C", ["Chrome"] = "#0B0C0D",
        ["ScrollThumb"] = "#3A3E44", ["ScrollThumbHover"] = "#4C5158", ["ChipOk"] = "#173A2A", ["ChipWarn"] = "#3A2F17", ["ChipBad"] = "#3A1C20",
        ["GridLine"] = "#212428", ["ControlBorder"] = "#4C5158", ["BadButton"] = "#C23B47", ["HoverOverlay"] = "#FFFFFF",
    };
    private static readonly Dictionary<string, string> LightBase = new()
    {
        ["Bg"] = "#F2F3EF", ["Surface"] = "#FFFFFF", ["Surface2"] = "#F1F2EE", ["SurfaceHover"] = "#E7E9E3", ["Border"] = "#D9DCD5",
        ["Text"] = "#16181B", ["Muted"] = "#5F646B", ["Idle"] = "#A2A6AC", ["Ok"] = "#13824A", ["Warn"] = "#9A6100", ["Bad"] = "#C92A38",
        ["OkSoft"] = "#E2F3E9", ["WarnSoft"] = "#FAF0DA", ["BadSoft"] = "#FBE3E5", ["Chrome"] = "#E8EAE5",
        ["ScrollThumb"] = "#C3C7C0", ["ScrollThumbHover"] = "#A6ABA3", ["ChipOk"] = "#DCF1E4", ["ChipWarn"] = "#F8ECD2", ["ChipBad"] = "#F9DEE1",
        ["GridLine"] = "#EAEBE7", ["ControlBorder"] = "#9EA39B", ["BadButton"] = "#C23B47", ["HoverOverlay"] = "#000000",
    };

    /// <summary>Цвет того же тона, затемненный до нужного контраста с фоном (для текста акцента на светлой теме).</summary>
    public static Color EnsureContrast(Color c, Color bg, double ratio)
    {
        for (double t = 0; t <= 1.0001; t += 0.04)
        {
            var x = Mix(c, Dark, t);
            if (Contrast(x, bg) >= ratio) return x;
        }
        return Dark;
    }

    // ---------- Контраст по WCAG ----------

    private static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    public static double Luminance(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    public static double Contrast(Color a, Color b)
    {
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    public static readonly Color White = Colors.White;
    public static readonly Color Dark = Color.FromRgb(0x11, 0x12, 0x14);
    private static Color Base(string key) => TryParse((IsDark ? DarkBase : LightBase)[key], out var c) ? c : Colors.Gray;
    private static Color Surface => Base("Surface");
    private static Color TextColor => Base("Text");

    /// <summary>Текст поверх заливки: белый, если читается (4.5:1), иначе темный.</summary>
    public static Color OnFill(Color fill) => Contrast(White, fill) >= 4.5 || Contrast(White, fill) >= Contrast(Dark, fill) ? White : Dark;

    /// <summary>Смесь двух цветов: t = 0 — первый, 1 — второй.</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Предупреждение о читаемости для слота или null. Проверяется то, как цвет реально используется:
    /// акцент и метки — как текст на темной карточке, заливки — как фон под белым текстом.
    /// </summary>
    public static string? Warning(string key, Color c) => key switch
    {
        // На светлой теме акцент и метки как текст программа затемняет сама, предупреждать не о чем
        "Accent" or "Lime" when IsDark && Contrast(c, Surface) < 3 => "плохо читается на темном фоне: выберите цвет светлее",
        "Selection" or "MenuBg" or "AccentSoft" or "StrongButton" when Contrast(TextColor, c) < 4.5
            => IsDark ? "текст на этом фоне читается плохо: выберите цвет темнее" : "текст на этом фоне читается плохо: выберите цвет светлее",
        "Selection" or "MenuBg" or "AccentSoft" when Contrast(c, Base("Bg")) < 1.15 && c != Base("Surface") => "почти не отличается от фона окна",
        "AccentButton" or "ToggleOn" when Contrast(OnFill(c), c) < 4.5 => "надпись на кнопке будет плохо видна",
        _ => null,
    };

    /// <summary>Применить цвета ко всей программе сразу. custom = null или пустой — стандартные; theme — «dark» или «light».</summary>
    public static void Apply(IReadOnlyDictionary<string, string>? custom, string? theme = null)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        if (theme != null) IsDark = theme != "light";
        var dark = IsDark;
        // Новая кисть в ресурсах: XAML (DynamicResource) и Ui.Ref подхватывают ее сами. WPF замораживает кисти
        // в словаре ресурсов, поэтому перекрасить на месте нельзя: части, нарисованные кодом, окна перерисовывают
        // по событию Changed.
        void Set(string key, Color c) => res[key] = new SolidColorBrush(c);
        foreach (var (k, hex) in dark ? DarkBase : LightBase) if (TryParse(hex, out var c)) Set(k, c);

        var bg = Base("Bg");
        var surface = Base("Surface");
        var paper = dark ? bg : White;                     // с чем смешивать для мягких подложек
        var accentHue = Get(custom, "Accent", dark);
        // Свой акцент без своих производных цветов: выделение, вкладки, метки и кнопки выводятся из акцента.
        // Раньше они оставались стандартными лаймовыми, и при синем акценте выделенная строка была оливковой
        bool ownAccent = Custom(custom, "Accent", dark) != null;
        Color Derived(string key, Func<Color> fromAccent) => Custom(custom, key, dark) ?? (ownAccent ? fromAccent() : Get(custom, key, dark));
        var button = Derived("AccentButton", () => dark ? Mix(accentHue, Dark, 0.25) : accentHue);
        var strong = Get(custom, "StrongButton", dark);
        var toggle = Derived("ToggleOn", () => accentHue);
        var menu = Get(custom, "MenuBg", dark);
        var limeHue = Derived("Lime", () => accentHue);
        // Светлая: лайм на белом как текст не читается — акцент и метки затемняются до контраста 4.5, заливки остаются
        var accent = dark ? accentHue : EnsureContrast(accentHue, surface, 4.5);
        var lime = dark ? limeHue : EnsureContrast(limeHue, Mix(limeHue, White, 0.8), 4.5);
        var tab = Custom(custom, "AccentSoft", dark) ?? (!dark ? Mix(accentHue, White, 0.78) : ownAccent ? Mix(accentHue, bg, 0.84) : Get(custom, "AccentSoft", true));
        var sel = Custom(custom, "Selection", dark) ?? (!dark ? Mix(accentHue, White, 0.72) : ownAccent ? Mix(accentHue, bg, 0.79) : Get(custom, "Selection", true));

        Set("Accent", accent);
        Set("AccentHover", dark ? Mix(accent, White, 0.15) : Mix(accent, Dark, 0.15));
        Set("AccentBorder", dark ? Mix(accent, bg, 0.35) : Mix(accent, White, 0.25));
        Set("Focus", dark ? Mix(accent, White, 0.3) : accent);
        Set("AccentButton", button);
        Set("OnAccentButton", OnFill(button));
        Set("StrongButton", strong);
        Set("OnStrongButton", OnFill(strong));
        Set("ToggleOn", toggle);
        Set("OnToggle", OnFill(toggle));
        Set("AccentSoft", tab);
        Set("InfoSoft", Mix(accentHue, paper, dark ? 0.82 : 0.84));
        Set("Selection", sel);
        Set("SelectionInactive", Mix(sel, paper, dark ? 0.2 : 0.35));
        // Строка подключенного сервера: градиент от акцента; у выбранной подключенной он переходит в цвет выделения
        Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
        Brush Gradient(Color from, Color to)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            g.GradientStops.Add(new GradientStop(from, 0));
            g.GradientStops.Add(new GradientStop(to, 1));
            g.Freeze();
            return g;
        }
        res["ConnectedRow"] = Gradient(A(accentHue, dark ? (byte)0x55 : (byte)0x40), A(accentHue, 0));
        res["ConnectedRowSelected"] = Gradient(A(accentHue, dark ? (byte)0x80 : (byte)0x66), sel);
        Set("MenuBg", menu);
        Set("MenuHover", dark ? Mix(menu, White, 0.07) : Mix(menu, Dark, 0.06));
        Set("Lime", lime);
        Set("LimeSoft", Mix(limeHue, paper, dark ? 0.82 : 0.8));
        Set("ChipLime", Mix(limeHue, paper, dark ? 0.76 : 0.72));
        // Логотип: первые две полосы цвета акцента, если выбран свой акцент или набор; иначе исходные цвета знака —
        // белые на темной теме, черные на светлой. Третья полоса оливковая всегда
        var customAccent = Custom(custom, "Accent", dark) != null;
        Set("LogoStripe", customAccent ? accent : dark ? Color.FromRgb(0xF5, 0xF5, 0xF0) : Dark);
        Set("LogoTail", Color.FromRgb(0xB5, 0xB2, 0x9C));
        // Логотип ChatGPT одноцветный: цвет текста темы (светлый на темной, графитовый на светлой)
        if (res["LogoOpenAI"] is DrawingImage { Drawing: DrawingGroup g } di)
        {
            var copy = di.CloneCurrentValue();
            foreach (var gd in ((DrawingGroup)copy.Drawing).Children.OfType<GeometryDrawing>()) gd.Brush = new SolidColorBrush(Base("Text"));
            copy.Freeze();
            res["LogoOpenAI"] = copy;
        }
        Changed?.Invoke();
    }
}
