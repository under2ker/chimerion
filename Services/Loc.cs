using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dnex.Services;

/// <summary>
/// Язык интерфейса: русский (исходный) или английский.
///
/// Текст интерфейса написан в коде по-русски. Перевод делается при показе: словарь i18n/en.json
/// («русский → английский», строки с данными записаны шаблонами вида «Подключено · {0}»), а общий обработчик
/// переводит текст элементов, когда они появляются и когда код меняет их текст. Внутренние данные остаются
/// русскими, поэтому логика (сравнения статусов, разбор страниц) от языка не зависит.
/// Исходный русский текст элемента запоминается: язык переключается сразу и обратно, без перезапуска.
/// </summary>
public static class Loc
{
    public static string Lang { get; private set; } = "ru";
    public static bool En => Lang == "en";
    /// <summary>Язык сменился: окна, которые держат текст вне элементов (меню трея), обновляют его.</summary>
    public static event Action? Changed;

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<(Regex Re, string En, int Literal)> Templates = new();
    /// <summary>Шаблоны, которые начинаются или кончаются знаком из Edge: их сверяют до обрезки краев.</summary>
    private static readonly HashSet<Regex> EdgeTemplates = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static bool _loaded, _hooked;
    private static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    /// <summary>Разделители для перевода по частям, когда строка целиком в словаре не нашлась.</summary>
    private static readonly string[] Separators = { "\n", " · ", " › ", " → ", ". ", "; ", ": ", ", " };

    private static readonly Lazy<Dictionary<string, string>> Upper = new(() =>
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ru, en) in Exact) d.TryAdd(ru.ToUpperInvariant(), en.ToUpperInvariant());
        return d;
    });

    private static readonly char[] Edge = { ' ', ',', '.', ';', ':', '·', '\n', '\r' };

    /// <summary>Шаблон: текст вокруг {n} точно, на месте {n} любые символы.</summary>
    private static void AddTemplate(string ru, string en)
    {
        var pattern = "^" + Regex.Replace(Regex.Escape(ru), @"\\\{(\d)\}", m => $"(?<g{m.Groups[1].Value}>.+?)") + "$";
        Templates.Add((new Regex(pattern, RegexOptions.Singleline | RegexOptions.Compiled), en, Regex.Replace(ru, @"\{\d\}", "").Length));
        if (ru.Length > 0 && (Array.IndexOf(Edge, ru[^1]) >= 0 || Array.IndexOf(Edge, ru[0]) >= 0)) EdgeTemplates.Add(Templates[^1].Re);
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            using var s = typeof(Loc).Assembly.GetManifestResourceStream("i18n.en.json");
            if (s == null) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            foreach (var (ru, en) in map)
            {
                if (!Regex.IsMatch(ru, @"\{\d\}"))
                {
                    Exact[ru] = en;
                    // Обрывки фраз из кода («лучший: », «Программа закроется, »): и вариант без знаков по краям
                    var core = ru.Trim(Edge);
                    if (core.Length > 0 && core != ru) Exact.TryAdd(core, en.Trim(Edge));
                    continue;
                }
                // Шаблоны без русских слов («{0}, {1}», «{0}↵{1}») не нужны: строку по таким знакам режут разделители
                if (!Cyr.IsMatch(Regex.Replace(ru, @"\{\d\}", ""))) continue;
                AddTemplate(ru, en);
                // Обрывок с данными («{0} работают   ·   »): и вариант без знаков по краям
                var core2 = ru.Trim(Edge);
                if (core2 != ru && Regex.IsMatch(core2, @"\{\d\}") && Cyr.IsMatch(core2) && !map.ContainsKey(core2))
                    AddTemplate(core2, en.Trim(Edge));
            }
            // Более конкретные шаблоны (больше постоянного текста) проверяются первыми
            Templates.Sort((a, b) => b.Literal.CompareTo(a.Literal));
            foreach (var (ru, en) in GeoLocation.EnglishNames()) Exact.TryAdd(ru, en);
        }
        catch (Exception) { /* без словаря интерфейс просто остается русским */ }
    }

    /// <summary>Перевод строки на текущий язык. Русский интерфейс и строки без кириллицы возвращаются как есть.</summary>
    public static string T(string? ru)
    {
        if (ru == null) return "";
        if (!En || ru.Length == 0 || !Cyr.IsMatch(ru)) return ru;
        Load();
        if (Cache.TryGetValue(ru, out var hit)) return hit;
        var r = Translate(ru, 0);
        if (Cache.Count < 20000) Cache[ru] = r;
        return r;
    }

    private static string Translate(string s, int depth)
    {
        if (!Cyr.IsMatch(s)) return s;
        if (Exact.TryGetValue(s, out var e)) return e;
        // Заголовки разделов пишутся капсом уже после того, как текст взят из кода («ПРОВЕРИТЬ САЙТ»): ищем их
        // без учета регистра и возвращаем перевод тоже капсом
        if (s == s.ToUpperInvariant() && Upper.Value.TryGetValue(s, out var up)) return up;
        // Шаблон, который сам кончается знаком («{0} мс · {1} пров.»), проверяется по строке целиком, до обрезки краев:
        // иначе точка сокращения осталась бы висеть в переводе
        foreach (var (re, en, _) in Templates)
        {
            if (!EdgeTemplates.Contains(re)) continue;
            var m = re.Match(s);
            if (!m.Success) continue;
            var r = Regex.Replace(en, @"\{(\d)\}", g => Translate(m.Groups["g" + g.Groups[1].Value].Value, depth + 1));
            if (!Cyr.IsMatch(r)) return r;
        }
        // Знаки и пробелы по краям сохраняем, переводим середину («Программа закроется, » -> «The app will close, »)
        var trimmed = s.Trim(Edge);
        if (trimmed.Length != s.Length && trimmed.Length > 0)
        {
            var lead = s[..s.IndexOf(trimmed, StringComparison.Ordinal)];
            var tail = s[(lead.Length + trimmed.Length)..];
            var t = Translate(trimmed, depth);
            if (t != trimmed) return lead + t + tail;
        }
        if (depth > 6) return s;
        // Шаблон принимается, только если строка переводится целиком: короткие шаблоны («{0} и {1}») иначе резали бы
        // строку посреди фразы и давали смесь языков
        foreach (var (re, en, literal) in Templates)
        {
            var m = re.Match(s);
            if (!m.Success) continue;
            var r = Regex.Replace(en, @"\{(\d)\}", g => Translate(m.Groups["g" + g.Groups[1].Value].Value, depth + 1));
            if (!Cyr.IsMatch(r) || Regex.Replace(en, @"\{\d\}", "").Length >= 12 && IsData(m)) return r;
        }
        // По частям: строки, собранные из нескольких фраз. Каждая часть переводится целиком или остается как есть
        foreach (var sep in Separators)
        {
            var i = s.IndexOf(sep, StringComparison.Ordinal);
            if (i <= 0 || i + sep.Length >= s.Length) continue;
            var parts = s.Split(sep);
            var tr = parts.Select(p => Translate(p, depth + 1)).ToArray();
            if (!tr.SequenceEqual(parts)) return string.Join(sep, tr);
        }
        return s;
    }

    /// <summary>
    /// Подставленные значения похожи на данные (название сервера, адрес), а не на текст интерфейса:
    /// в них нет русских служебных слов из словаря. Тогда длинный шаблон принимается и с русскими данными внутри.
    /// </summary>
    private static bool IsData(Match m)
    {
        for (int i = 0; i < 10; i++)
        {
            var g = m.Groups["g" + i];
            if (!g.Success) break;
            if (Cyr.IsMatch(g.Value) && g.Value.Split(' ').Length > 4) return false; // целая фраза, а не название
        }
        return true;
    }

    // ---------- Переключение и перевод элементов ----------

    /// <summary>Исходные русские значения переведенных свойств элемента.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, string>> Originals = new();
    private static bool _setting;

    public static void SetLanguage(string? lang)
    {
        var next = lang == "en" ? "en" : "ru";
        if (!_hooked)
        {
            _hooked = true;
            // Новые окна и элементы (строки таблицы, перерисованные карточки, всплывающие меню): после перестройки
            // интерфейса окна проходятся заново. Событие Loaded для этого не годится: WPF рассылает его только
            // элементам со своим обработчиком. Проход редкий (не чаще раза в 0.4 с) и только в английском режиме
            _sweep.Tick += (_, _) =>
            {
                if (!_dirty || !En) return;
                _dirty = false;
                foreach (var w in Application.Current.Windows.Cast<Window>().ToList()) Walk(w);
                foreach (var p in OpenPopups()) Walk(p);
            };
        }
        if (next == Lang) return;
        Lang = next;
        if (En)
        {
            _layout ??= new FrameworkElement();
            _layout.LayoutUpdated -= OnLayout;
            _layout.LayoutUpdated += OnLayout; // LayoutUpdated общее для всего потока интерфейса: любая перестройка
            _sweep.Start();
        }
        else
        {
            if (_layout != null) _layout.LayoutUpdated -= OnLayout;
            _sweep.Stop();
        }
        Cache.Clear();
        // Все открытые окна: перевести или вернуть русский
        if (Application.Current != null)
            foreach (var w in Application.Current.Windows.Cast<Window>().ToList()) Walk(w);
        Changed?.Invoke();
    }

    private static readonly System.Windows.Threading.DispatcherTimer _sweep = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private static FrameworkElement? _layout;
    private static bool _dirty;
    private static void OnLayout(object? sender, EventArgs e) => _dirty = true;

    /// <summary>Открытые всплывающие окна (меню, подсказки, палитра): у них свое дерево вне окон.</summary>
    private static IEnumerable<DependencyObject> OpenPopups() =>
        PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Select(s => s.RootVisual).OfType<DependencyObject>()
            .Where(r => r is not Window);

    private static void Walk(DependencyObject root)
    {
        Attach(root);
        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Walk(VisualTreeHelper.GetChild(root, i));
        // Свернутые вкладки, всплывающие и скрытые части видны только в логическом дереве
        foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList())
            if (c is not Visual || VisualTreeHelper.GetParent(c) == null) Walk(c);
    }

    /// <summary>Перевести текстовые свойства элемента и следить, чтобы новый текст из кода тоже переводился.</summary>
    private static void Attach(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb:
                if (tb.Inlines.Count > 1 || (tb.Inlines.FirstInline is { } fi && fi is not Run))
                    foreach (var run in tb.Inlines.OfType<Run>().ToList()) Prop(run, Run.TextProperty); // копия: перевод меняет набор фрагментов
                else Prop(tb, TextBlock.TextProperty);
                break;
            case Run r: Prop(r, Run.TextProperty); break;
            case Window w: Prop(w, Window.TitleProperty); break;
            case HeaderedItemsControl h: Prop(h, HeaderedItemsControl.HeaderProperty); break;
            case ContentControl c when c is not Window: Prop(c, ContentControl.ContentProperty); break;
            case TextBox t when t.Tag is string tag && (Cyr.IsMatch(tag) || Originals.TryGetValue(t, out var om) && om.ContainsKey(FrameworkElement.TagProperty)): Prop(t, FrameworkElement.TagProperty); break;
            case DataGrid g:
                foreach (var col in g.Columns)
                    if (col.Header is string hs && Cyr.IsMatch(hs))
                    {
                        if (!Originals.GetOrCreateValue(col).TryGetValue(DataGridColumn.HeaderProperty, out var o)) Originals.GetOrCreateValue(col)[DataGridColumn.HeaderProperty] = o = hs;
                        col.Header = T(o);
                    }
                    else if (col.Header is string hs2 && Originals.TryGetValue(col, out var map) && map.TryGetValue(DataGridColumn.HeaderProperty, out var o2) && !En) col.Header = o2;
                break;
        }
    }

    private static readonly HashSet<(DependencyObject, DependencyProperty)> Watched = new();

    private static void Prop(DependencyObject d, DependencyProperty dp)
    {
        Apply(d, dp);
        if (!En || !Watched.Add((d, dp))) return;
        // Код поменял текст позже: переводим и его. Подписка снимается, когда элемент уходит с экрана
        var dpd = DependencyPropertyDescriptor.FromProperty(dp, d.GetType());
        if (dpd == null) return;
        EventHandler h = (_, _) => { if (!_setting) Apply(d, dp, changed: true); };
        dpd.AddValueChanged(d, h);
        if (d is FrameworkElement fe)
            fe.Unloaded += (_, _) => { dpd.RemoveValueChanged(d, h); Watched.Remove((d, dp)); };
    }

    private static void Apply(DependencyObject d, DependencyProperty dp, bool changed = false)
    {
        if (d.GetValue(dp) is not string cur) return;
        var map = Originals.GetOrCreateValue(d);
        if (changed || !map.ContainsKey(dp))
        {
            // Новый текст: если он не наш перевод, это новый русский исходник
            if (map.TryGetValue(dp, out var prev) && cur == T(prev) && En) return;
            if (!Cyr.IsMatch(cur)) { map.Remove(dp); return; } // код поставил текст без русского («—», число): переводить нечего
            map[dp] = cur;
        }
        if (!map.TryGetValue(dp, out var ru)) return;
        var want = En ? T(ru) : ru;
        if (want == cur) return;
        _setting = true;
        try
        {
            // SetCurrentValue не ломает привязки и стили: следующее обновление привязки снова переводится
            if (!En && BindingOperations.GetBindingExpressionBase(d, dp) is { } be) be.UpdateTarget();
            else d.SetCurrentValue(dp, want);
        }
        finally { _setting = false; }
    }
}
