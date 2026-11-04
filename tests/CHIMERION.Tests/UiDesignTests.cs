using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Один поток WPF с настоящими ресурсами App.xaml на все проверки внешнего вида (приложение в процессе одно).</summary>
internal static class Wpf
{
    private static readonly Dispatcher D;

    static Wpf()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? d = null;
        var t = new Thread(() =>
        {
            var app = Application.Current ?? new App();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (app.Resources.Count == 0) ((App)app).InitializeComponent();
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        D = d!;
    }

    public static void Run(Action a)
    {
        Exception? error = null;
        D.Invoke(() => { try { a(); } catch (Exception e) { error = e; } });
        if (error != null) throw new Exception(error.Message, error);
    }
}

public class UiDesignTests
{
    private static Color Res(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    private static void Theme(string theme) => ThemeColors.Apply(null, theme);

    [Fact]
    public void TextBoxHintStandsWhereCaretAndTextStart()
    {
        Wpf.Run(() =>
        {
            Theme("dark");
            foreach (var multi in new[] { false, true })
            {
                var tb = new TextBox { Tag = "Например, мой DNS", Width = 300, Padding = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Top };
                if (multi) { tb.AcceptsReturn = true; tb.Height = 90; tb.VerticalContentAlignment = VerticalAlignment.Top; }
                var w = new Window { Content = tb, Width = 400, Height = 300, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000 };
                w.Show();
                try
                {
                    tb.ApplyTemplate(); tb.UpdateLayout();
                    var hint = (TextBlock)tb.Template.FindName("hint", tb);
                    var hintAt = hint.TranslatePoint(new Point(hint.Padding.Left, 0), tb);
                    var caret = tb.GetRectFromCharacterIndex(0);
                    // Раньше отступ поля применялся дважды (Margin у PART_ContentHost и сам WPF): курсор стоял на 12 px правее подсказки
                    Assert.InRange(Math.Abs(hintAt.X - caret.Left), 0, 0.5);
                    if (multi) Assert.InRange(Math.Abs(hintAt.Y - caret.Top), 0, 1.5);
                    if (!multi) Assert.InRange(tb.ActualHeight, 34, 40); // выше кнопок (36) поле быть не должно
                }
                finally { w.Close(); }
            }
        });
    }

    [Fact]
    public void CheckedMenuItemShowsMark()
    {
        Wpf.Run(() =>
        {
            Theme("dark");
            var on = new MenuItem { Header = "Любая", IsCheckable = true, IsChecked = true };
            var off = new MenuItem { Header = "Россия", IsCheckable = true, IsChecked = false };
            var plain = new MenuItem { Header = "Копировать" };
            var menu = new ContextMenu { Items = { on, off, plain } };
            var w = new Window { Content = new Border { ContextMenu = menu }, Width = 100, Height = 100, ShowInTaskbar = false, Left = -3000, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            try
            {
                foreach (var m in new[] { on, off, plain }) { m.ApplyTemplate(); m.UpdateLayout(); }
                Visibility Mark(MenuItem m) => ((UIElement)m.Template.FindName("check", m)).Visibility;
                Assert.Equal(Visibility.Visible, Mark(on));
                Assert.Equal(Visibility.Hidden, Mark(off));      // место занято: текст не прыгает при выборе
                Assert.Equal(Visibility.Collapsed, Mark(plain)); // обычный пункт без отступа
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void DangerButtonTextReadsOnRed()
    {
        Wpf.Run(() =>
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                Theme(theme);
                var b = Ui.Action("Подключить все равно", "Danger", () => { });
                var text = ((SolidColorBrush)b.Foreground).Color;
                Assert.True(ThemeColors.Contrast(text, Res("BadButton")) >= 4.5, $"{theme}: текст на красной кнопке {ThemeColors.Contrast(text, Res("BadButton")):0.00}:1");
            }
        });
    }

    [Fact]
    public void LightThemeTextContrast()
    {
        Wpf.Run(() =>
        {
            Theme("light");
            var backs = new[] { "Surface", "Bg", "Surface2", "SurfaceHover" };
            // Статусные цвета как текст: на плашках и на фонах окна
            foreach (var fg in new[] { "Ok", "Warn", "Bad" })
                foreach (var bg in backs.Concat(new[] { fg + "Soft", "Chip" + fg }))
                    Assert.True(ThemeColors.Contrast(Res(fg), Res(bg)) >= 4.5, $"light {fg} на {bg}: {ThemeColors.Contrast(Res(fg), Res(bg)):0.00}:1");
            // Акцент как текст (ссылки, заголовки раскрывающихся блоков) на всех светлых фонах
            foreach (var bg in backs.Concat(new[] { "AccentSoft", "Selection", "InfoSoft" }))
                Assert.True(ThemeColors.Contrast(Res("Accent"), Res(bg)) >= 4.5, $"light Accent на {bg}: {ThemeColors.Contrast(Res("Accent"), Res(bg)):0.00}:1");
        });
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void ControlEdgesAreVisible(string theme)
    {
        Wpf.Run(() =>
        {
            Theme(theme);
            // Граница галочки и поля ввода, точка «нет данных»: не меньше 3:1 к фону, на котором стоят (WCAG 1.4.11)
            foreach (var bg in new[] { "Surface", "Bg", "Surface2" })
            {
                Assert.True(ThemeColors.Contrast(Res("ControlBorder"), Res(bg)) >= 3, $"{theme} ControlBorder на {bg}");
                Assert.True(ThemeColors.Contrast(Res("Idle"), Res(bg)) >= 3, $"{theme} Idle на {bg}");
            }
        });
    }

    [Fact]
    public void SharedBuildersFollowThemeChange()
    {
        Wpf.Run(() =>
        {
            Theme("dark");
            var section = Ui.Section("Раздел");
            var sep = Ui.Separator();
            var footer = Ui.Footer(null);
            var status = Ui.Status();
            // Ссылки на ресурсы обновляются у элементов в окне, как в программе
            var host = new StackPanel { Children = { section, sep, footer, status } };
            var w = new Window { Content = host, Width = 300, Height = 300, ShowInTaskbar = false, Left = -3000, WindowStartupLocation = WindowStartupLocation.Manual };
            w.Show();
            try
            {
                Theme("light"); // окно открыто, тема сменилась в настройках
                Assert.Equal(Res("Muted"), ((SolidColorBrush)section.Foreground).Color);
                Assert.Equal(Res("Border"), ((SolidColorBrush)sep.Background).Color);
                Assert.Equal(Res("Border"), ((SolidColorBrush)footer.BorderBrush).Color);
                Assert.Equal(Res("Muted"), ((SolidColorBrush)status.Foreground).Color);
                Ui.SetStatus(status, "ошибка", error: true);
                Theme("dark");
                Assert.Equal(Res("Bad"), ((SolidColorBrush)status.Foreground).Color);
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void RowNamesItsControlForScreenReaders()
    {
        Wpf.Run(() =>
        {
            var box = Ui.Toggle(true);
            var row = Ui.Row("Автозапуск", "Вместе с Windows", box);
            Assert.NotNull(System.Windows.Automation.AutomationProperties.GetLabeledBy(box));
            Assert.NotNull(row);
        });
    }

    [Fact]
    public void ChimerionDnsWindowShowsPlansAndKeepsButtonsCompact()
    {
        Wpf.Run(() =>
        {
            Theme("dark");
            var w = ChimerionDnsWindow.Build();
            w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -4000; w.Owner = null; w.ShowInTaskbar = false;
            w.Show();
            try
            {
                w.UpdateLayout();
                var texts = AllOf<TextBlock>(w).Select(t => t.Text).ToList();
                foreach (var (_, _, rub, _) in ChimerionDnsInfo.Plans) Assert.Contains($"{rub} ₽", texts);
                var buttons = AllOf<Button>(w).Where(b => b.Content is string s && s is "Открыть бота" or "Написать" or "Закрыть").ToList();
                Assert.Equal(3, buttons.Count);
                // Левый слот подвала растягивался, и «Написать» занимала полокна
                Assert.All(buttons, b => Assert.InRange(b.ActualWidth, 60, 200));
                Assert.True(w.ActualHeight <= Ui.WorkAreaOf(null).Height);
            }
            finally { w.Close(); }
        });
    }

    private static IEnumerable<T> AllOf<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var inner in AllOf<T>(child)) yield return inner;
        }
    }

    private static string? ProjectFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CHIMERION.csproj"))) dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, name);
    }

    [Fact]
    public void SwitchButtonDoesNotWidenTheHeader()
    {
        // «Переключить сюда» стояла в правой колонке шапки: она росла на 170 px, статистика сужалась и кнопка обновления уезжала вниз
        var path = ProjectFile("MainWindow.xaml");
        if (path == null) return;
        var xaml = File.ReadAllText(path);
        int button = xaml.IndexOf("x:Name=\"SwitchButton\"", StringComparison.Ordinal);
        int bar = xaml.IndexOf("x:Name=\"SelectionBar\"", StringComparison.Ordinal);
        int actions = xaml.IndexOf("x:Name=\"ApplyActions\"", StringComparison.Ordinal);
        int connect = xaml.IndexOf("x:Name=\"ConnectButton\"", StringComparison.Ordinal);
        Assert.True(button > bar, "кнопка должна быть в полосе выбранного сервера");
        Assert.False(button > actions && button < connect, "кнопка не должна стоять в правой колонке шапки");
    }

    [Fact]
    public void PromoRowSellsInsteadOfShowingEmptyCheckResults()
    {
        var promo = ChimerionDnsInfo.PromoRow();
        Assert.StartsWith("от ", promo.PromoTeaser);
        Assert.Contains(ChimerionDnsInfo.Plans[0].Rub.ToString(), promo.PromoTeaser);
        Assert.DoesNotContain("Сервер:", promo.LocationCell); // не «без подмены»: у платного доступа нет такой проверки
        Assert.Equal("", new Dnex.Models.DnsServer { Name = "x" }.PromoTeaser);
        Wpf.Run(() =>
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                Theme(theme);
                Assert.True(Application.Current.Resources["PromoRow"] is LinearGradientBrush);
            }
        });
    }

    [Fact]
    public void WorkAreaIsNotEmpty()
    {
        Wpf.Run(() =>
        {
            var area = Ui.WorkAreaOf(null);
            Assert.True(area.Width > 300 && area.Height > 300);
        });
    }

    [Fact]
    public void NewEnglishStringsAreTranslated()
    {
        Wpf.Run(() => // SetLanguage обходит окна приложения: нужен поток интерфейса
        {
            try
            {
                Loc.SetLanguage("en");
                Assert.Equal("Hide servers", Loc.T("Скрыть серверы"));
                Assert.Equal("In development", Loc.T("В разработке"));
                Assert.Equal("12.3 MB", Loc.T("12.3 МБ"));
                Assert.Equal("Additional colors (custom)", Loc.T("Дополнительные цвета (есть свои)"));
                Assert.Equal("Buy in the bot", Loc.T("Купить в боте"));
                Assert.Equal("Pay with Telegram Stars", Loc.T("Оплата звездами Telegram"));
            }
            finally { Loc.SetLanguage("ru"); }
        });
    }
}
