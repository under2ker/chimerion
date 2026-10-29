using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Dnex;

/// <summary>
/// Окно «Режим»: что работает сейчас и готовые наборы одним щелчком («Только ИИ», «Все сразу», «Игры», «Все выключить»).
/// У каждого режима видно, что изменится с DNS, Telegram и обходом, текущий отмечен. Сами действия выполняет главное
/// окно (ApplyModeAsync), окно показывает ход и итог.
/// </summary>
internal sealed class ModeWindow
{
    /// <summary>Что режим делает с частью: включить, выключить, не трогать, включить обход и для игр.</summary>
    public enum Target { On, Off, Keep, Games }

    public sealed record ModeInfo(int Id, string Title, string Hint, string Icon, bool Recommended, Target Dns, Target Telegram, Target Zapret);

    /// <summary>Состояние сейчас: подключение, Telegram-прокси (и чужая копия), zapret (и обход для игр).</summary>
    public sealed record State(bool Dns, string? DnsName, bool Telegram, bool TelegramForeign, bool Zapret, bool ZapretGames, bool HasWorkingServers);

    private static ModeWindow? _open;
    private readonly Window _w;
    private readonly IReadOnlyList<ModeInfo> _modes;
    private readonly Func<State> _state;
    private readonly Func<int, Action<string>, Task<List<string>>> _apply;
    private readonly StackPanel _now = new();
    private readonly UniformGrid _tiles = new() { Columns = 2 };
    private readonly StackPanel _progress = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _stage = Ui.Hint("", 6);
    private readonly TextBlock _result = Ui.Hint("", 0);
    private bool _busy;
    private int? _running;

    public static void Show(Window owner, IReadOnlyList<ModeInfo> modes, Func<State> state, Func<int, Action<string>, Task<List<string>>> apply)
    {
        if (_open != null) { _open.Render(); _open._w.Activate(); return; }
        _open = new ModeWindow(owner, modes, state, apply);
        _open._w.Show();
    }

    /// <summary>Состояние поменялось снаружи (трей, главная): открытое окно обновляется.</summary>
    public static void Refresh() => _open?.Render();

    /// <summary>Какой режим соответствует состоянию; null — свой набор.</summary>
    public static int? Current(IReadOnlyList<ModeInfo> modes, State s) =>
        modes.FirstOrDefault(m => Fits(m.Dns, s.Dns, false, false) && Fits(m.Telegram, s.Telegram, false, false) && Fits(m.Zapret, s.Zapret, s.ZapretGames, true)
                                  && !(m.Dns == Target.Keep && m.Telegram == Target.Keep && !s.Zapret))?.Id;

    private static bool Fits(Target t, bool on, bool games, bool zapret) => t switch
    {
        Target.On => on && (!zapret || !games),
        Target.Games => on && games,
        Target.Off => !on,
        _ => true,
    };

    private ModeWindow(Window owner, IReadOnlyList<ModeInfo> modes, Func<State> state, Func<int, Action<string>, Task<List<string>>> apply)
    {
        _modes = modes; _state = state; _apply = apply;
        _w = DnexDialog.CreateWindow("Режим", 820);
        if (owner is { IsVisible: true }) { _w.Owner = owner; _w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        Action repaint = () => _w.Dispatcher.Invoke(Render); // смена темы: плашки и рамки в новых цветах
        Services.ThemeColors.Changed += repaint;
        _w.Closed += (_, _) => { _open = null; Services.ThemeColors.Changed -= repaint; };
        _w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) _w.Close();
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4 && !_busy) { var i = e.Key - Key.D1; if (i < _modes.Count) _ = ApplyAsync(_modes[i]); e.Handled = true; }
        };

        var root = Ui.Root();
        root.Children.Add(DnexDialog.Header(DialogKind.Info, "Режим", "Включите нужное одним щелчком: DNS для нейросетей, ускорение Telegram и обход для YouTube и Discord", glyph: "sliders-horizontal"));

        var nowCard = Ui.Card(_now, bottom: 0);
        nowCard.Padding = new Thickness(18, 14, 18, 14);
        root.Children.Add(nowCard);

        root.Children.Add(Ui.Section("Готовые режимы"));
        root.Children.Add(_tiles);
        _progress.Children.Add(new ProgressBar { IsIndeterminate = true });
        _progress.Children.Add(_stage);
        root.Children.Add(_progress);
        _result.Margin = new Thickness(2, 10, 0, 0);
        _result.Visibility = Visibility.Collapsed;
        root.Children.Add(_result);

        var close = Ui.Action("Закрыть", "Ghost", () => { if (!_busy) _w.Close(); }).With(b => b.IsCancel = true);
        root.Children.Add(Ui.Footer(Ui.Hint("Режим можно включить и из трея: правая кнопка по значку → «Режим». Ctrl+1…4 включает режим по порядку", 0)
            .With(t => { t.FontSize = 12; t.VerticalAlignment = VerticalAlignment.Center; }), close));
        _w.Content = Ui.WindowContent(root);
        Render();
    }

    private void Render()
    {
        var s = _state();
        var current = Current(_modes, s);

        // Сейчас: три части с понятным состоянием
        _now.Children.Clear();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var modeName = current is { } c ? _modes.First(m => m.Id == c).Title : "свой набор";
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center,
                                 Child = new TextBlock { Text = current != null ? $"Сейчас: {modeName}" : "Сейчас: свой набор", FontSize = 12.5, FontWeight = FontWeights.SemiBold }
                                     .Ref(TextBlock.ForegroundProperty, current != null ? "Accent" : "Muted") }
            .Ref(Border.BackgroundProperty, current != null ? "AccentSoft" : "Surface2");
        DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
        head.Children.Add(new TextBlock { Text = "Что работает сейчас", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _now.Children.Add(head);
        var parts = new UniformGrid { Columns = 3 };
        parts.Children.Add(Part(null, "shield-check", "Smart DNS для нейросетей", s.Dns ? s.DnsName ?? "Подключен" : "Выключен", s.Dns));
        parts.Children.Add(Part(new[] { "LogoTelegram" }, null, "Ускорение Telegram", s.TelegramForeign ? "Ваша копия работает" : s.Telegram ? "Включено" : "Выключено", s.Telegram || s.TelegramForeign));
        parts.Children.Add(Part(new[] { "LogoYouTube", "LogoDiscord" }, null, "Обход YouTube и Discord", s.Zapret ? (s.ZapretGames ? "Включен, и для игр" : "Включен") : "Выключен", s.Zapret));
        _now.Children.Add(parts);

        // Режимы
        _tiles.Children.Clear();
        foreach (var m in _modes) _tiles.Children.Add(Tile(m, s, m.Id == current));
    }

    /// <summary>Часть состояния: значок или логотипы, название и состояние цветом.</summary>
    private static FrameworkElement Part(string[]? logos, string? icon, string title, string value, bool on)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var glyph = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        if (logos != null)
            foreach (var key in logos)
            {
                var img = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 3, 0), Stretch = Stretch.Uniform };
                img.SetResourceReference(Image.SourceProperty, key);
                glyph.Children.Add(img);
            }
        else glyph.Children.Add(new Controls.Icon { Kind = icon!, Size = 20 }.Ref(Controls.Icon.ForegroundProperty, on ? "Ok" : "Muted"));
        DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Hint(title, 0).With(t => { t.FontSize = 12; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        state.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }
            .Ref(System.Windows.Shapes.Shape.FillProperty, on ? "Ok" : "Muted"));
        state.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }
            .Ref(TextBlock.ForegroundProperty, on ? "Ok" : "Text"));
        text.Children.Add(state);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Плитка режима: значок, название, метки, пояснение, что изменится по частям и кнопка.</summary>
    private FrameworkElement Tile(ModeInfo m, State s, bool current)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                                Child = new Controls.Icon { Kind = m.Icon, Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                    .Ref(Controls.Icon.ForegroundProperty, current ? "OnToggle" : "Accent") }
            .Ref(Border.BackgroundProperty, current ? "Accent" : "AccentSoft");
        DockPanel.SetDock(icon, Dock.Left); head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = m.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (current) line.Children.Add(Ui.Badge("сейчас включен"));
        else if (m.Recommended) line.Children.Add(Ui.Badge());
        titles.Children.Add(line);
        titles.Children.Add(Ui.Hint(m.Hint, 3));
        head.Children.Add(titles);
        body.Children.Add(head);

        // Что изменится: по каждой части отдельной строкой, без догадок
        var changes = new StackPanel { Margin = new Thickness(52, 10, 0, 0) };
        changes.Children.Add(Change("Smart DNS", m.Dns, s.Dns, false, s.Dns ? null : s.HasWorkingServers ? null : "нет рабочих серверов: сначала найдите их"));
        changes.Children.Add(Change("Telegram", m.Telegram, s.Telegram || s.TelegramForeign, false, s.TelegramForeign && m.Telegram == Target.Off ? "ваша копия не выключается" : null));
        changes.Children.Add(Change("Обход zapret", m.Zapret, s.Zapret, s.ZapretGames, null));
        body.Children.Add(changes);

        bool runningThis = _running == m.Id;
        var go = Ui.Action(current ? "Уже включен" : runningThis ? "Включаю…" : $"Включить «{m.Title}»", m.Recommended && !current ? "Primary" : "Strong", () => _ = ApplyAsync(m))
            .With(b => { b.Margin = new Thickness(52, 12, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; b.IsEnabled = !current && !_busy; });
        body.Children.Add(go);

        var card = Ui.Card(body, selected: current, bottom: 0);
        card.Padding = new Thickness(16, 14, 16, 14);
        card.Margin = new Thickness(0, 0, 10, 10);
        return card;
    }

    /// <summary>Строка «что изменится»: часть, значок итога и текст («включится», «останется включен», «не меняется»).</summary>
    private static FrameworkElement Change(string part, Target t, bool on, bool games, string? note)
    {
        var (text, kind, color) = t switch
        {
            Target.On when on && !games => ("останется включен", "check", "Ok"),
            Target.On => (on ? "обход для игр выключится" : "включится", "circle-arrow-up", "Accent"),
            Target.Games when on && games => ("останется включен для игр", "check", "Ok"),
            Target.Games => (on ? "включится и для игр" : "включится, и для игр", "circle-arrow-up", "Accent"),
            Target.Off when on => ("выключится", "x", "Warn"),
            Target.Off => ("останется выключен", "minus", "Muted"),
            _ => ("не меняется", "minus", "Muted"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new Controls.Icon { Kind = kind, Size = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Ref(Controls.Icon.ForegroundProperty, color));
        row.Children.Add(new TextBlock { Text = part + ": ", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Muted"));
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, color == "Muted" ? "Text" : color));
        if (note != null) row.Children.Add(new TextBlock { Text = " · " + note, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Ref(TextBlock.ForegroundProperty, "Warn"));
        return row;
    }

    private async Task ApplyAsync(ModeInfo m)
    {
        if (_busy || Current(_modes, _state()) == m.Id) return;
        _busy = true; _running = m.Id;
        _result.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Visible;
        _stage.Text = $"Включаю «{m.Title}»…";
        Render();
        List<string> failed;
        try { failed = await _apply(m.Id, step => _w.Dispatcher.Invoke(() => _stage.Text = $"«{m.Title}»: {step}…")); }
        catch (Exception e) { failed = new List<string> { e.Message }; }
        finally { _busy = false; _running = null; _progress.Visibility = Visibility.Collapsed; }
        _result.Text = failed.Count == 0 ? $"Режим «{m.Title}» включен" : $"Режим «{m.Title}»: не получилось ({string.Join(", ", failed)}). Подробности в журнале";
        _result.SetResourceReference(TextBlock.ForegroundProperty, failed.Count == 0 ? "Ok" : "Warn");
        _result.Visibility = Visibility.Visible;
        if (Ui.Animate) _result.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ui.Motion(250)));
        Render();
    }
}
