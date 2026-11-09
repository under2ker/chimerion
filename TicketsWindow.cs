using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Мои обращения»: что человек отправлял разработчику, статус и ответ. Открытие окна снимает отметку «новое»
/// (Support.MarkSeen). Если исправление вышло в версии новее установленной, рядом кнопка обновления.
/// </summary>
internal static class TicketsWindow
{
    private static Window? _open;

    /// <summary>Что делает кнопка «Обновить»: главное окно подставляет проверку обновлений.</summary>
    public static Action? OnUpdate { get; set; }

    public static void Show()
    {
        if (_open != null) { _open.Activate(); return; }
        var store = Support.Current;
        Support.MarkSeen(store);
        var w = DnexDialog.CreateWindow("Мои обращения", Ui.Wide);
        _open = w;
        w.Closed += (_, _) => _open = null;
        w.Content = Ui.WindowContent(Build(w, store));
        w.Show();
    }

    private static StackPanel Build(Window w, Support.Store store)
    {
        var root = Ui.Root();
        var count = store.Items.Count;
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Мои обращения",
            count == 0 ? "Здесь появятся ваши сообщения разработчику и ответы на них"
                       : "Статус и ответы приходят сюда сами, программа проверяет их раз в полчаса", glyph: "send"));

        if (count == 0)
            root.Children.Add(Ui.Hint("Обращений пока нет. Если что-то не работает, нажмите «Сообщить о проблеме» в окне «Диагностика»", 0));
        else
        {
            var list = new StackPanel();
            foreach (var t in store.Items.OrderByDescending(t => t.Created)) list.Children.Add(Card(t));
            root.Children.Add(Ui.Scroll(list, 460));
        }

        var close = Ui.Action("Закрыть", "Primary", () => w.Close());
        close.IsCancel = true;
        var write = Ui.Action("Новое обращение", "Strong", () =>
        {
            w.Close();
            ReportWindow.Show(() => null, () => AutoStart.OpenUrl(Diagnostics.IssuesUrl), _ => { });
        });
        root.Children.Add(Ui.Footer(new Border(), close, write));
        return root;
    }

    private static string StatusBrush(Support.Ticket t) => t.Status switch
    {
        Support.Fixed => "Ok",
        Support.Work => "Accent",
        _ => "Muted",
    };

    private static UIElement Card(Support.Ticket t)
    {
        var body = new StackPanel();
        var when = t.Created.ToString("d MMM, HH:mm", CultureInfo.CurrentUICulture);
        body.Children.Add(Ui.CardTitle($"#{t.Id} · {when}", Support.StatusLine(t), StatusBrush(t)));
        body.Children.Add(new TextBlock { Text = t.Preview, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        if (!string.IsNullOrWhiteSpace(t.Reply))
        {
            var reply = new StackPanel();
            reply.Children.Add(new TextBlock { Text = "Ответ разработчика", FontSize = 11, FontWeight = FontWeights.SemiBold }.Ref(TextBlock.ForegroundProperty, "Accent"));
            reply.Children.Add(new TextBlock { Text = t.Reply, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 10, 0, 0), Child = reply }
                .Ref(Border.BackgroundProperty, "Surface2"));
        }

        var actions = new List<Button>();
        if (Support.NeedsUpdate(t) && OnUpdate != null)
            actions.Add(Ui.Action($"Обновить до {t.FixVersion}", "Primary", () => OnUpdate!()));
        if (!t.Tg && t.Status != Support.Closed && t.Code.Length > 0)
            actions.Add(Ui.Action("Получать ответ и в Telegram", "Strong", () => AutoStart.OpenUrl(Support.BotLink(t))));
        if (actions.Count > 0) body.Children.Add(Ui.Actions(actions.ToArray()));
        return Ui.Card(body, bottom: 10);
    }
}
