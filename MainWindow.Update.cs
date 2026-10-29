using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Dnex.Services;

namespace Dnex;

/// <summary>
/// Карточка обновления внизу левого меню (на главной и в панели управления). Единственное место, где проверяется и
/// ставится новая версия: показывает текущую версию, итог проверки и найденное обновление. В узком меню видна одна иконка.
/// </summary>
public partial class MainWindow
{
    private readonly List<UpdateCardView> _updateCards = new();
    private readonly DispatcherTimer _updateResultTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _updateChecking;
    private (string Text, string Color, string? Reason)? _updateResult;

    private sealed class UpdateCardView
    {
        public Border Card = null!;
        public Border Badge = null!;
        public Controls.Icon Icon = null!;
        public TextBlock Title = null!, Sub = null!;
        public StackPanel Text = null!;
    }

    /// <summary>Новая карточка, подписанная на общее состояние. Вызывать для каждого меню.</summary>
    private Border CreateUpdateCard()
    {
        var v = new UpdateCardView();
        v.Icon = new Controls.Icon { Kind = "refresh-cw", Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        v.Badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = v.Icon, VerticalAlignment = VerticalAlignment.Center };
        v.Title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        v.Sub = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        v.Text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        v.Text.Children.Add(v.Title); v.Text.Children.Add(v.Sub);
        var row = new DockPanel();
        DockPanel.SetDock(v.Badge, Dock.Left); row.Children.Add(v.Badge); row.Children.Add(v.Text);
        v.Card = new Border { Child = row, Padding = new Thickness(4, 6, 8, 6), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                              Cursor = Cursors.Hand, Focusable = true, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(v.Card, "Обновление CHIMERION");
        v.Card.MouseEnter += (_, _) => PaintUpdateCard(v);
        v.Card.MouseLeave += (_, _) => PaintUpdateCard(v);
        v.Card.MouseLeftButtonUp += (_, _) => UpdateCard_Click();
        v.Card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; UpdateCard_Click(); } };
        _updateCards.Add(v);
        PaintUpdateCard(v);
        return v.Card;
    }

    private void UpdateCard_Click()
    {
        if (_updateChecking) return;
        CheckUpdate_Click(this, new RoutedEventArgs());
    }

    /// <summary>В узком меню подписи карточки прозрачны и обрезаются краем, как у пунктов меню.</summary>
    private void SetUpdateCardsExpanded(bool expanded)
    {
        foreach (var v in _updateCards.Where(c => c.Card == PanelUpdateHost.Content)) { v.Text.Opacity = expanded ? 1 : 0; v.Card.BorderThickness = new Thickness(expanded ? 1 : 0); }
    }

    private void RefreshUpdateCards() { foreach (var v in _updateCards) PaintUpdateCard(v); }

    private void PaintUpdateCard(UpdateCardView v)
    {
        bool hover = v.Card.IsMouseOver;
        string title, sub, kind, badge, fore, subColor, border, back;
        if (_updateChecking)
        {
            (title, sub, kind) = ("Проверяю…", "Связываюсь с сервером обновлений", "refresh-cw");
            (badge, fore, subColor, border, back) = ("Surface2", "Accent", "Muted", "Border", "Surface2");
        }
        else if (_pendingUpdate is { } info)
        {
            (title, sub, kind) = ($"Доступна {info.Version.ToString(3)}", "Нажмите, чтобы обновить", "circle-arrow-up");
            (badge, fore, subColor, border, back) = ("Accent", "OnToggle", "Accent", "Accent", hover ? "AccentSoft" : "AccentSoft");
        }
        else if (_updateResult is { } r)
        {
            (title, sub, kind) = (r.Color == "Ok" ? "Последняя версия" : "Не удалось проверить", r.Color == "Ok" ? $"Установлена {UpdateChecker.CurrentVersion}" : r.Text, r.Color == "Ok" ? "circle-check" : "triangle-alert");
            (badge, fore, subColor, border, back) = (r.Color == "Ok" ? "ChipOk" : "ChipWarn", r.Color, r.Color, r.Color, "Surface2");
        }
        else
        {
            (title, sub, kind) = ($"Версия {UpdateChecker.CurrentVersion}", "Проверить обновление", "refresh-cw");
            (badge, fore, subColor, border, back) = ("Surface2", "Accent", "Muted", hover ? "Accent" : "Border", hover ? "SurfaceHover" : "Surface2");
        }
        v.Title.Text = title; v.Sub.Text = sub; v.Icon.Kind = kind;
        v.Card.ToolTip = _updateResult?.Reason ?? (_pendingUpdate != null ? "Посмотреть, что нового, и установить" : "Проверить, есть ли новая версия CHIMERION");
        v.Badge.SetResourceReference(Border.BackgroundProperty, badge);
        v.Icon.SetResourceReference(Controls.Icon.ForegroundProperty, fore);
        v.Sub.SetResourceReference(TextBlock.ForegroundProperty, subColor);
        v.Card.SetResourceReference(Border.BorderBrushProperty, border);
        v.Card.SetResourceReference(Border.BackgroundProperty, back);
        v.Card.Opacity = _updateChecking ? .8 : 1;
        // Найдено обновление: значок мягко пульсирует, чтобы карточку заметили
        if (_pendingUpdate != null && !_updateChecking)
        {
            if (v.Badge.Tag as string != "pulse")
            {
                v.Badge.Tag = "pulse";
                v.Badge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .55, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            }
        }
        else if (v.Badge.Tag as string == "pulse") { v.Badge.Tag = null; v.Badge.BeginAnimation(OpacityProperty, null); v.Badge.Opacity = 1; }
    }

    /// <summary>Фон нашел версию: карточка становится «Доступна …» и открывает окно сразу, без новой проверки.</summary>
    private void ShowUpdateAvailable(UpdateInfo info) { _pendingUpdate = info; _updateResult = null; RefreshUpdateCards(); }

    private void ClearUpdateAvailable() { _pendingUpdate = null; RefreshUpdateCards(); }

    /// <summary>Итог проверки на карточке; через 6 с она возвращается к виду с версией.</summary>
    private void ShowUpdateResult(string text, string colorKey, string? reason)
    {
        _updateResult = (text, colorKey, reason);
        RefreshUpdateCards();
        _updateResultTimer.Stop();
        _updateResultTimer.Tick -= ResetUpdateResult;
        _updateResultTimer.Tick += ResetUpdateResult;
        _updateResultTimer.Start();
        if (!IsVisible || WindowState == WindowState.Minimized) ShowToast(text + (reason != null ? ": " + reason : ""), colorKey);
    }

    private void ResetUpdateResult(object? sender, EventArgs e)
    {
        _updateResultTimer.Stop();
        _updateResult = null;
        RefreshUpdateCards();
    }
}
