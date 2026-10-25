using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Окно «Доступна новая версия». Показывает описание из релиза и ставит обновление прямо из программы:
/// скачивание с прогрессом, проверка контрольной суммы, замена файла (см. Updater). Новая версия начинает
/// работать после перезапуска, кнопка «Перезапустить» делает его сразу. Если установка не удалась,
/// окно пишет причину и предлагает страницу релиза для ручного скачивания.
/// </summary>
internal static class UpdateWindow
{
    public static void Show(Window owner, UpdateInfo info, Action onSkip, Action onRestart, Action<string> log)
    {
        var res = Application.Current.Resources;
        var w = DnexDialog.CreateWindow("Обновление", 480);
        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        var header = new ContentControl { Focusable = false };
        root.Children.Add(header);

        // Описание из релиза: заголовки разделов выделены, пункты списка с точкой, остальная разметка убрана
        var notes = new TextBlock { Foreground = (Brush)res["Muted"], FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        RenderNotes(notes, info.Notes, (Brush)res["Text"]);
        var body = new ContentControl { Focusable = false, Margin = new Thickness(50, -4, 0, 18) }; // по линии заголовка, как в других окнах
        root.Children.Add(body);
        var row = new DockPanel { LastChildFill = false };
        root.Children.Add(row);
        w.Content = root;

        CancellationTokenSource? cts = null;
        // Закрытие окна во время скачивания отменяет его: программа остается прежней
        w.Closing += (_, _) => cts?.Cancel();

        void Buttons(params Button[] buttons)
        {
            row.Children.Clear();
            foreach (var b in buttons) row.Children.Add(b);
        }
        Button Right(Button b) { DockPanel.SetDock(b, Dock.Right); return b; }
        Button Left(Button b) { DockPanel.SetDock(b, Dock.Left); return b; }
        Button Btn(string text, string style, Action click) => DnexDialog.MakeButton(text, style, click);
        TextBlock Text(string t, string brush = "Muted") => new()
        {
            Text = t, Foreground = (Brush)res[brush], FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18,
        };

        void Offer()
        {
            header.Content = DnexDialog.Header(DialogKind.Ok, $"Доступна версия {info.Version}",
                $"Сейчас установлена {UpdateChecker.CurrentVersion}", glyph: "↑");
            body.Content = new ScrollViewer
            {
                Content = notes, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0),
            };
            var update = Right(Btn("Обновить", "Primary", () => _ = InstallAsync()));
            update.IsDefault = true;
            Buttons(update, Right(Btn("Позже", "Ghost", () => w.Close())),
                    Left(Btn("Пропустить версию", "Ghost", () => { onSkip(); w.Close(); })));
        }

        async Task InstallAsync()
        {
            header.Content = DnexDialog.Header(DialogKind.Info, $"Устанавливаю версию {info.Version}",
                "Программа работает как обычно, подключение не прерывается", glyph: "↓");
            var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 10) };
            var stage = Text("Подготовка…");
            var panel = new StackPanel();
            panel.Children.Add(bar);
            panel.Children.Add(stage);
            body.Content = panel;
            Buttons(Right(Btn("Отмена", "Ghost", () => cts?.Cancel())));

            cts = new CancellationTokenSource();
            var progress = new Progress<(double? Part, string Stage)>(p =>
            {
                bar.IsIndeterminate = p.Part == null;
                if (p.Part is { } part) bar.Value = part;
                stage.Text = p.Stage;
            });
            log($"Обновление: скачиваю версию {info.Version}");
            try
            {
                await Updater.InstallAsync(info.Version, progress, cts.Token);
                log($"Обновление: версия {info.Version} установлена, заработает после перезапуска");
                Done();
            }
            catch (OperationCanceledException)
            {
                log("Обновление отменено, программа не изменена");
                if (w.IsVisible) Offer();
            }
            catch (UpdateException e) { log("Обновление не установилось: " + e.Message); Failed(e.Message); }
            catch (Exception e) { log("Обновление не установилось: " + e.Message); Failed(e.Message); }
            finally { cts = null; }
        }

        void Done()
        {
            header.Content = DnexDialog.Header(DialogKind.Ok, "Обновление установлено",
                $"Версия {info.Version} заработает после перезапуска программы", glyph: "✓");
            body.Content = Text("Перезапуск займет несколько секунд. Подключение DNS сохранится, программа вернется в том же состоянии.");
            var restart = Right(Btn("Перезапустить", "Primary", () => { w.Close(); onRestart(); }));
            restart.IsDefault = true;
            Buttons(restart, Right(Btn("Позже", "Ghost", () => w.Close())));
        }

        void Failed(string reason)
        {
            header.Content = DnexDialog.Header(DialogKind.Error, "Обновление не установилось", "Программа осталась прежней");
            body.Content = Text(reason);
            var retry = Right(Btn("Повторить", "Primary", () => _ = InstallAsync()));
            Buttons(retry, Right(Btn("Закрыть", "Ghost", () => w.Close())),
                    Left(Btn("Страница релиза", "Ghost", () => { AutoStart.OpenUrl(info.PageUrl); w.Close(); })));
        }

        Offer();
        w.Show();
    }

    /// <summary>
    /// Простая разметка из описания релиза: «## Заголовок» жирным с отступом сверху, «- пункт» с точкой,
    /// **жирный** и `код` без символов. Длинное описание обрезается.
    /// </summary>
    internal static void RenderNotes(TextBlock tb, string markdown, Brush headColor)
    {
        tb.Inlines.Clear();
        bool first = true;
        int total = 0;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Replace("**", "").Replace("`", "").TrimEnd();
            if (line.Trim().Length == 0) continue;
            if (total > 2000) { tb.Inlines.Add(new System.Windows.Documents.Run("…")); break; }
            total += line.Length;
            bool head = line.StartsWith('#');
            if (!first) tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            if (head && !first) tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            first = false;
            if (head)
                tb.Inlines.Add(new System.Windows.Documents.Run(line.TrimStart('#', ' ')) { FontWeight = FontWeights.SemiBold, Foreground = headColor });
            else if (line.TrimStart().StartsWith("- "))
                tb.Inlines.Add(new System.Windows.Documents.Run("•  " + line.TrimStart()[2..]));
            else
                tb.Inlines.Add(new System.Windows.Documents.Run(line));
        }
    }

    /// <summary>Убирает разметку Markdown, которую не показываем: заголовки, жирный, код.</summary>
    internal static string CleanNotes(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", "").TrimEnd())
            .Where(l => l.Length > 0);
        var text = string.Join(Environment.NewLine, lines);
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
