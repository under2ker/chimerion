using System.Windows;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// «Сообщить о проблеме»: человек описывает проблему, при желании оставляет контакт и прикладывает очищенный отчет диагностики.
/// Обращение уходит разработчику (Support), ответ приходит в программу. Показываем, что именно отправится, и ничего не уходит,
/// пока человек не нажал «Отправить».
/// </summary>
internal sealed class ReportWindow
{
    private static ReportWindow? _open;

    /// <param name="report">Очищенный отчет диагностики или null, если проверка еще не завершилась.</param>
    /// <param name="github">Запасной путь: черновик публичного обращения на GitHub.</param>
    public static void Show(Func<string?> report, Action github, Action<string> log)
    {
        if (_open != null) { _open._w.Activate(); return; }
        _open = new ReportWindow(report, github, log);
        _open._w.Show();
    }

    private readonly Window _w;
    private readonly Support.Store _store = Support.Current;
    private readonly Func<string?> _report;
    private readonly Action _github;
    private readonly Action<string> _log;
    private readonly string _token = Support.NewToken();
    private readonly TextBox _text = Ui.MultiLine("", 130);
    private readonly TextBox _contact = new();
    private readonly CheckBox _attach = new() { Content = "Приложить отчет диагностики", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _kindHost = new();
    private Button _send = null!;
    private string _kind;
    private bool _busy;

    private ReportWindow(Func<string?> report, Action github, Action<string> log)
    {
        _report = report;
        _github = github;
        _log = log;
        _kind = _store.ContactKind is { } k && Support.ContactKinds.Contains(k) ? k : "tg";
        _w = DnexDialog.CreateWindow("Сообщить о проблеме", Ui.Wide);
        _w.Closed += (_, _) => _open = null;
        _w.Content = Ui.WindowContent(BuildForm());
        _w.Loaded += (_, _) => _text.Focus();
    }

    private StackPanel BuildForm()
    {
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Сообщить о проблеме",
            "Обращение получит разработчик. Ответ появится в программе: вы увидите уведомление", glyph: "send"));

        root.Children.Add(Ui.Section("Что случилось", first: true));
        _text.MaxLength = Support.MaxText;
        var textBody = new StackPanel();
        textBody.Children.Add(_text);
        textBody.Children.Add(Ui.Hint("Что вы делали и что пошло не так. Чем подробнее, тем быстрее найдем причину", 8));
        root.Children.Add(Ui.Card(textBody, bottom: 0));

        root.Children.Add(Ui.Section("Как с вами связаться"));
        var contactRow = new DockPanel();
        DockPanel.SetDock(_kindHost, Dock.Left);
        _kindHost.Margin = new Thickness(0, 0, 8, 0);
        contactRow.Children.Add(_kindHost);
        _contact.Text = _store.ContactValue != null && _store.ContactKind == _kind ? _store.ContactValue : "";
        contactRow.Children.Add(_contact);
        var contactBody = new StackPanel();
        contactBody.Children.Add(contactRow);
        contactBody.Children.Add(Ui.Hint("Необязательно. Ответ в любом случае появится в программе. Контакт нужен, если хотите получить ответ и вне ее, " +
                                         "или чтобы мы могли уточнить детали", 8));
        root.Children.Add(Ui.Card(contactBody, bottom: 0));
        BuildKindPicker();

        root.Children.Add(Ui.Section("Что отправится"));
        var attachBody = new StackPanel();
        var attachRow = new DockPanel();
        var preview = Ui.Action("Посмотреть", "Ghost", ShowPreview);
        DockPanel.SetDock(preview, Dock.Right);
        attachRow.Children.Add(preview);
        attachRow.Children.Add(_attach);
        attachBody.Children.Add(attachRow);
        var hasReport = _report() != null;
        _attach.IsEnabled = hasReport;
        if (!hasReport) _attach.IsChecked = false;
        attachBody.Children.Add(Ui.Hint(hasReport
            ? "Состояние подключения, версии, журнал и ошибки. Ваш внешний IP, адреса локальной сети, имя пользователя Windows и почта из отчета убраны. " +
              "Адреса DNS-серверов остаются: по ним видно, что не работает"
            : "Отчет диагностики сейчас недоступен. Чтобы приложить его, откройте окно «Диагностика», дождитесь проверки и нажмите «Сообщить о проблеме» там", 8));
        root.Children.Add(Ui.Card(attachBody, bottom: 0));

        _send = Ui.Action("Отправить", "Primary", () => _ = SendAsync());
        _send.IsDefault = false;
        var cancel = Ui.Action("Отмена", "Ghost", () => _w.Close()).With(b => b.IsCancel = true);
        var github = Ui.Action("Открытое обращение на GitHub", "Ghost", () => { _w.Close(); _github(); })
            .With(b => b.ToolTip = "Обращение будет видно всем. Нужен аккаунт GitHub");
        _status.Margin = new Thickness(2, 12, 0, 0);
        _status.Visibility = Visibility.Collapsed;
        root.Children.Add(_status);
        root.Children.Add(Ui.Footer(new StackPanel { Orientation = Orientation.Horizontal, Children = { github } }, _send, cancel));
        return root;
    }

    private void BuildKindPicker()
    {
        _contact.Tag = Support.KindHint(_kind);
        _kindHost.Content = Ui.Picker(Support.KindTitle(_kind), b =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var kind in Support.ContactKinds)
            {
                var mi = new MenuItem { Header = Support.KindTitle(kind), IsCheckable = true, IsChecked = kind == _kind };
                mi.Click += (_, _) =>
                {
                    if (kind == _kind) return;
                    _kind = kind;
                    _contact.Text = _store.ContactKind == kind ? _store.ContactValue ?? "" : "";
                    BuildKindPicker();
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        });
    }

    private string? ReportToSend() => _attach.IsChecked == true ? _report() : null;

    private void ShowPreview()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Loc.T("Сообщение:")).AppendLine(string.IsNullOrWhiteSpace(_text.Text) ? Loc.T("(пока пусто)") : _text.Text.Trim()).AppendLine();
        sb.AppendLine(Loc.T("Контакт:")).AppendLine(string.IsNullOrWhiteSpace(_contact.Text) ? Loc.T("(не указан)") : $"{Loc.T(Support.KindTitle(_kind))}: {_contact.Text.Trim()}").AppendLine();
        sb.AppendLine(string.Format(Loc.T("Версия программы: {0}"), UpdateChecker.CurrentVersion.ToString(3))).AppendLine();
        sb.AppendLine(Loc.T("Отчет диагностики:")).AppendLine(ReportToSend() ?? Loc.T("(не прикладывается)"));
        var w = DnexDialog.CreateWindow("Что отправится", Ui.Wide);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Что отправится",
            "Ровно этот текст уйдет разработчику. Ничего больше с вашего компьютера не отправляется", glyph: "eye-off"));
        var box = Ui.MultiLine(sb.ToString(), 360);
        box.IsReadOnly = true;
        root.Children.Add(Ui.Card(box, bottom: 0));
        var close = Ui.Action("Закрыть", "Primary", () => w.Close());
        close.IsCancel = true;
        root.Children.Add(Ui.Footer(new Border(), close));
        w.Content = Ui.WindowContent(root);
        w.Owner = _w;
        w.ShowDialog();
    }

    private void SetStatus(string text, bool bad)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        _status.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Bad" : "Muted");
    }

    private void Fail(string text) => SetStatus(text, true);

    private async Task SendAsync()
    {
        if (_busy) return;
        if (!Support.TryContact(_kind, _contact.Text, out var contact, out var error)) { Fail(error!); _contact.Focus(); return; }
        if (_text.Text.Trim().Length < Support.MinText) { Fail("Опишите проблему хотя бы парой слов"); _text.Focus(); return; }
        _busy = true;
        _send.IsEnabled = false;
        SetStatus("Отправляю…", false);
        try
        {
            var ticket = await Support.SendAsync(_store, _token, _text.Text, contact, ReportToSend());
            _log($"Обращение #{ticket.Id} отправлено разработчику");
            ShowDone(ticket);
        }
        catch (Support.SendException e) { Fail(e.Message); }
        catch (Exception e)
        {
            SupportTrace.Write($"Обращение: сбой отправки ({e.GetType().Name})");
            Fail("Не получилось отправить. Попробуйте еще раз: текст сохранился");
        }
        finally { _busy = false; _send.IsEnabled = true; }
    }

    /// <summary>Подтверждение вместо формы: номер обращения, что будет дальше и как получить ответ в Telegram.</summary>
    private void ShowDone(Support.Ticket ticket)
    {
        var root = Ui.Root();
        var badge = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Controls.Icon { Kind = "circle-check", Size = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Ref(Controls.Icon.ForegroundProperty, "Ok"),
        }.Ref(Border.BackgroundProperty, "OkSoft");
        root.Children.Add(badge);
        root.Children.Add(new TextBlock
        {
            Text = $"Отчет #{ticket.Id} отправлен", FontSize = 18, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Разработчик уже получил его и скоро ответит. Ответ появится в программе: вы увидите уведомление, а все обращения лежат в «Диагностика → Мои обращения». " +
                   "Программа проверяет ответы раз в полчаса.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(8, 8, 8, 0),
        }.Ref(TextBlock.ForegroundProperty, "Muted"));

        var tgBody = new StackPanel();
        tgBody.Children.Add(Ui.CardTitle("Получить ответ и в Telegram"));
        tgBody.Children.Add(Ui.Hint("Откройте бота и нажмите Start: он свяжет чат с этим обращением, и ответ придет сразу в Telegram. Необязательно", 4));
        tgBody.Children.Add(Ui.Actions(Ui.Action("Открыть бота", "Strong", () => AutoStart.OpenUrl(Support.BotLink(ticket)))));
        var tgCard = Ui.Card(tgBody, bottom: 0);
        tgCard.Margin = new Thickness(0, 20, 0, 0);
        root.Children.Add(tgCard);

        var done = Ui.Action("Готово", "Primary", () => _w.Close());
        done.IsDefault = true;
        done.IsCancel = true;
        var list = Ui.Action("Мои обращения", "Strong", () => { _w.Close(); TicketsWindow.Show(); });
        root.Children.Add(Ui.Footer(new Border(), done, list));
        _w.Content = Ui.WindowContent(root);
        if (badge.Child is FrameworkElement) Ui.Pop(badge, .6);
    }
}
