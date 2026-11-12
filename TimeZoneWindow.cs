using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Dnex.Services;

namespace Dnex;

internal static class TimeZoneWindow
{
    internal const string Title = "Часовой пояс";
    private const string Code = "Claude Code";
    private const string Browser = "Claude в браузере";
    private const string Desktop = "Claude Desktop";
    private const string Windows = "Вся Windows";

    internal static void Show(Action<string> log) => DnexDialog.Present(Build(log));

    private static Window Build(Action<string> log)
    {
        var window = DnexDialog.CreateWindow(Title, Ui.Narrow + 40);
        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, Loc.T(Title), Loc.T("Выберите, где и какое время нужно показать"), glyph: "globe"));

        int mode = 0;
        var modes = new WrapPanel();
        foreach (var (label, index) in new[] { Code, Browser, Desktop, Windows }.Select((text, index) => (text, index)))
        {
            var tab = new RadioButton
            {
                Content = Loc.T(label), GroupName = "time-zone", IsChecked = index == 0,
                Style = (Style)Application.Current.Resources["Tab"],
                Margin = new Thickness(0, 0, 6, 4),
            };
            modes.Children.Add(tab);
        }


        TimeZoneInfo CurrentZone() => Storage.TestCopy ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
        var localId = CurrentZone().Id;
        var zones = ClaudeTimeZone.Zones(Loc.Lang == "en");
        var zonePicker = new ListBox
        {
            Height = 150,
            ItemsSource = zones,
            DisplayMemberPath = nameof(ClaudeTimeZone.Zone.Name),
            SelectedItem = zones.FirstOrDefault(zone => zone.WindowsId == localId),
        }.Ref(Control.BackgroundProperty, "Surface2").Ref(Control.BorderBrushProperty, "ControlBorder")
         .Ref(Control.ForegroundProperty, "Text");
        if (zonePicker.SelectedItem is { } selected) zonePicker.Loaded += (_, _) => zonePicker.ScrollIntoView(selected);
        AutomationProperties.SetName(zonePicker, Loc.T("Часовой пояс"));
        var guide = Ui.Hint("", 10);
        var current = Ui.Hint("", 8);
        var status = Ui.Status();
        var controls = new StackPanel();
        controls.Children.Add(Ui.Section(Loc.T("Где изменить время"), first: true));
        controls.Children.Add(modes);
        controls.Children.Add(Ui.Section(Loc.T("Часовой пояс")));
        controls.Children.Add(zonePicker);
        controls.Children.Add(current);
        controls.Children.Add(guide);
        controls.Children.Add(status);
        root.Children.Add(Ui.Card(controls));

        bool busy = false;
        Button action = null!;
        action = Ui.Action("Скопировать команду", "Primary", () => _ = ApplyAsync());
        var close = Ui.Action(Loc.T("Закрыть"), "Ghost", window.Close).With(b => b.IsCancel = true);
        AutomationProperties.SetName(close, Loc.T("Закрыть выбор пояса"));
        root.Children.Add(Ui.Footer(null, action, close));
        window.Content = root;

        void Refresh()
        {
            action.Content = Loc.T(mode switch
            {
                2 => "Выбрать системный пояс",
                3 => "Применить в Windows",
                _ => "Скопировать команду",
            });
            guide.Text = Loc.T(mode switch
            {
                1 => "На вкладке Claude откройте DevTools (F12), затем Ctrl+Shift+P и «Show Protocol monitor». Вставьте команду в нижнее поле и нажмите Enter. Чтобы вернуть обычное время, закройте DevTools.",
                2 => "Обычный Claude Desktop берет время из Windows. Системная настройка действует на все программы.",
                3 => "Изменение действует на всю Windows. Прежний пояс можно выбрать здесь же.",
                _ => "Вставьте команду в PowerShell. Выбранный пояс действует на этот сеанс и команды, которые он запускает.",
            });
            guide.ToolTip = mode == 1 ? Loc.T("Если пункта нет, включите «Protocol Monitor» в DevTools: F1, раздел «Experiments».") : null;
            var currentZone = CurrentZone();
            current.Text = Loc.T($"Сейчас в Windows: {ClaudeTimeZone.DisplayName(currentZone, Loc.Lang == "en")}");
            Ui.SetStatus(status, "");
        }

        void Copy(string text, string success)
        {
            bool copied = Ui.CopyText(text);
            Ui.SetStatus(status, Loc.T(copied ? success : "Не удалось скопировать: буфер обмена занят"), !copied);
        }

        async Task ApplyAsync()
        {
            if (busy) return;
            if (mode == 2) { ((RadioButton)modes.Children[3]).IsChecked = true; return; }
            if (zonePicker.SelectedItem is not ClaudeTimeZone.Zone zone) return;
            busy = true;
            action.IsHitTestVisible = false;
            modes.IsHitTestVisible = false;
            zonePicker.IsHitTestVisible = false;
            try
            {
                if (mode == 3)
                {
                    if (!DnexDialog.Ask("Изменить часовой пояс Windows?",
                        $"Выбран «{zone.Name}». Новое время будет отображаться во всех программах.",
                        "Изменить пояс", danger: true, windowTitle: Title)) return;
                    Ui.SetStatus(status, Loc.T("Применяю часовой пояс…"));
                    var changed = await ClaudeTimeZone.ApplyWindowsAsync(zone.WindowsId);
                    Refresh();
                    if (!changed) log("Windows не применила часовой пояс");
                    Ui.SetStatus(status, Loc.T(changed ? "Часовой пояс Windows изменен" : "Windows не применила часовой пояс. Проверьте права или настройки времени."), !changed);
                }
                else if (zone.IanaId == null)
                {
                    Ui.SetStatus(status, Loc.T("Этот пояс доступен только в Windows"), true);
                }
                else if (mode == 1)
                {
                    Copy(ClaudeTimeZone.BrowserCommand(zone.IanaId), "Команда скопирована. Вставьте ее в Protocol monitor.");
                }
                else
                {
                    Copy(ClaudeTimeZone.CodeCommand(zone.IanaId), "Команда скопирована. Вставьте ее в PowerShell.");
                }
            }
            catch (Exception)
            {
                log("Не удалось подготовить или применить часовой пояс");
                Ui.SetStatus(status, Loc.T("Не удалось подготовить или применить часовой пояс"), true);
            }
            finally
            {
                busy = false;
                action.IsHitTestVisible = true;
                modes.IsHitTestVisible = true;
                zonePicker.IsHitTestVisible = true;
            }
        }

        for (int i = 0; i < modes.Children.Count; i++)
        {
            int index = i;
            ((RadioButton)modes.Children[i]).Checked += (_, _) => { mode = index; Refresh(); };
        }
        Refresh();
        return window;
    }
}
