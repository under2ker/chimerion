// Помощник tools/ui-shots.ps1: окна процесса, снимки PrintWindow с ожиданием стабильного кадра, фоновая запись PNG,
// фоновая проверка страницы через UI Automation и многопоточное сравнение с прошлым прогоном.
// Компилируется скриптом один раз (C# 5, .NET Framework) и кэшируется в %TEMP%\chimerion-ui-shots\UiShot-<хэш>.dll.
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.IO;
using System.Linq; using System.Runtime.InteropServices; using System.Text.RegularExpressions; using System.Threading;
using System.Threading.Tasks; using System.Windows.Automation;

public static class UiShot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

    // Видимые окна процесса крупнее подсказок и всплывающих меню
    public static IntPtr[] Windows(int pid) {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            uint id; RECT r;
            GetWindowThreadProcessId(h, out id);
            if (id == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.R - r.L > 200 && r.B - r.T > 120) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // Главное окно процесса: ждем, пока появится (программа загружается)
    public static IntPtr WaitMain(int pid, int maxMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do {
            try { var p = System.Diagnostics.Process.GetProcessById(pid); if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; }
            catch (ArgumentException) { return IntPtr.Zero; }
            Thread.Sleep(20);
        } while (sw.ElapsedMilliseconds < maxMs);
        return IntPtr.Zero;
    }

    // Ждать окна, кроме known: появилось новое (want=true) или новых не осталось (want=false)
    public static IntPtr[] WaitOthers(int pid, IntPtr[] known, bool want, int maxMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            var others = Windows(pid).Where(h => !known.Contains(h)).ToArray();
            if ((others.Length > 0) == want || sw.ElapsedMilliseconds >= maxMs) return others;
            Thread.Sleep(20);
        }
    }

    static Bitmap Grab(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        var b = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
        return b;
    }

    // Хэш по каждому 7-му пикселю и средняя яркость: быстро и ловит любое изменение крупнее точки
    static long Hash(Bitmap b, out double luma) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int n = d.Stride * d.Height / 4; var buf = new int[n]; Marshal.Copy(d.Scan0, buf, 0, n);
            long h = unchecked((long)14695981039346656037UL); double sum = 0; int cnt = 0;
            for (int i = 0; i < n; i += 7) {
                h ^= buf[i]; h *= 1099511628211L;
                if (i % 97 == 0) { int c = buf[i]; sum += ((c >> 16) & 255) * 0.3 + ((c >> 8) & 255) * 0.59 + (c & 255) * 0.11; cnt++; }
            }
            luma = cnt > 0 ? sum / cnt : 0;
            return h ^ ((long)b.Width << 40) ^ ((long)b.Height << 20);
        } finally { b.UnlockBits(d); }
    }

    static readonly List<Task> Pending = new List<Task>();
    public static double LastLuma;

    // Снимок: ждем, что кадр сменился с прошлого (prev, до changeMs: страница еще не перерисовалась), потом что
    // он перестал меняться (загрузка), но не дольше stableMs. PNG пишется в фоне. Возвращает хэш кадра
    public static long Save(IntPtr h, string path, long prev, int changeMs, int stableMs) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double luma;
        var b = Grab(h); long last = Hash(b, out luma);
        while (last == prev && sw.ElapsedMilliseconds < changeMs) { Thread.Sleep(20); b.Dispose(); b = Grab(h); last = Hash(b, out luma); }
        sw.Restart();
        while (sw.ElapsedMilliseconds < stableMs) {
            Thread.Sleep(35);
            var n = Grab(h); long hh = Hash(n, out luma);
            b.Dispose(); b = n;
            if (hh == last) break;
            last = hh;
        }
        LastLuma = luma;
        var bmp = b;
        lock (Pending) Pending.Add(Task.Run(() => { bmp.Save(path, ImageFormat.Png); bmp.Dispose(); }));
        return last;
    }

    public static void Flush() { Task[] all; lock (Pending) { all = Pending.ToArray(); Pending.Clear(); } Task.WaitAll(all); }

    // Проверка страницы в фоне: элемент с таким именем (* в конце — начало имени) появился в окне
    public static Task<bool> ExpectAsync(IntPtr h, string name, int maxMs) {
        return Task.Run(() => {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool prefix = name.EndsWith("*"); string key = name.TrimEnd('*');
            do {
                try {
                    var root = AutomationElement.FromHandle(h);
                    if (!prefix) { if (root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) != null) return true; }
                    else {
                        var cr = new CacheRequest(); cr.Add(AutomationElement.NameProperty); cr.AutomationElementMode = AutomationElementMode.None;
                        using (cr.Activate()) {
                            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                                if ((e.Cached.Name ?? "").StartsWith(key, StringComparison.Ordinal)) return true;
                        }
                    }
                } catch (ElementNotAvailableException) { }
                Thread.Sleep(50);
            } while (sw.ElapsedMilliseconds < maxMs);
            return false;
        });
    }

    // Самая большая область с вертикальной прокруткой, которую есть куда листать, или null. Ищется в фоне,
    // пока снимается кадр: свойства всех областей приходят одним запросом
    public static Task<AutomationElement> FindScrollAsync(IntPtr h) {
        return Task.Run(() => {
            try {
                var cr = new CacheRequest();
                cr.Add(AutomationElement.BoundingRectangleProperty); cr.Add(AutomationElement.IsOffscreenProperty);
                cr.Add(ScrollPattern.Pattern); cr.Add(ScrollPattern.VerticallyScrollableProperty); cr.Add(ScrollPattern.VerticalViewSizeProperty);
                var cond = new AndCondition(new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true),
                                            new PropertyCondition(ScrollPattern.VerticallyScrollableProperty, true));
                AutomationElementCollection all;
                using (cr.Activate()) all = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, cond);
                AutomationElement best = null; double area = 0;
                foreach (AutomationElement e in all) {
                    if ((bool)e.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty)) continue;
                    if ((double)e.GetCachedPropertyValue(ScrollPattern.VerticalViewSizeProperty) >= 99.5) continue;
                    var r = e.Cached.BoundingRectangle;
                    if (r.Width * r.Height > area) { area = r.Width * r.Height; best = e; }
                }
                return best;
            } catch (ElementNotAvailableException) { return null; }
        });
    }

    // Имя снимка без порядкового номера: «dark-ru-012-1600x1040-main» → «dark-ru-1600x1040-main»
    static readonly Regex Seq = new Regex(@"-\d{3}-");
    static string Key(string f) { return Seq.Replace(Path.GetFileNameWithoutExtension(f), "-", 1); }

    // Сравнение с прошлым прогоном, параллельно по всем снимкам. Строки: имя|доля отличий %|diff-файл или статус
    public static string[] Compare(string cur, string prev, string diffDir) {
        var old = Directory.GetFiles(prev, "*.png").GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var files = Directory.GetFiles(cur, "*.png");
        var res = new string[files.Length];
        Directory.CreateDirectory(diffDir);
        Parallel.For(0, files.Length, i => {
            var f = files[i]; string o;
            if (!old.TryGetValue(Key(f), out o)) { res[i] = Path.GetFileName(f) + "|-|новый"; return; }
            using (var a = new Bitmap(f)) using (var b = new Bitmap(o)) {
                if (a.Width != b.Width || a.Height != b.Height) { res[i] = Path.GetFileName(f) + "|100|другой размер"; return; }
                var ra = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var rb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int n = ra.Stride * ra.Height / 4; var pa = new int[n]; var pb = new int[n];
                Marshal.Copy(ra.Scan0, pa, 0, n); Marshal.Copy(rb.Scan0, pb, 0, n);
                a.UnlockBits(ra); b.UnlockBits(rb);
                int diff = 0; var mask = new int[n];
                for (int j = 0; j < n; j++) {
                    int x = pa[j], y = pb[j];
                    int d = Math.Abs(((x >> 16) & 255) - ((y >> 16) & 255)) + Math.Abs(((x >> 8) & 255) - ((y >> 8) & 255)) + Math.Abs((x & 255) - (y & 255));
                    if (d > 40) { diff++; mask[j] = unchecked((int)0xFFFF2050); }
                    else { int g = (((x >> 16) & 255) + ((x >> 8) & 255) + (x & 255)) / 9; mask[j] = unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g; }
                }
                if (diff == 0) { res[i] = Path.GetFileName(f) + "|0|"; return; }
                double pct = 100.0 * diff / n;
                var dn = Path.Combine(diffDir, Path.GetFileName(f));
                using (var m = new Bitmap(a.Width, a.Height, PixelFormat.Format32bppArgb)) {
                    var rm = m.LockBits(new Rectangle(0, 0, m.Width, m.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(mask, 0, rm.Scan0, n); m.UnlockBits(rm); m.Save(dn, ImageFormat.Png);
                }
                res[i] = Path.GetFileName(f) + "|" + pct.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "|diff/" + Path.GetFileName(f);
            }
        });
        return res;
    }
}
