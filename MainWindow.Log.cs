using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dnex.Models;
using Dnex.Services;
using Dnex.Services.Tools;

namespace Dnex;

/// <summary>Журнал</summary>
public partial class MainWindow
{
    private void LogToggle_Changed(object sender, RoutedEventArgs e) => Reveal(LogPanel, LogToggle.IsChecked == true);


    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private ICollectionView? _logView;
    private int _logFilter; // 0 — все, 1 — предупреждения и ошибки, 2 — только ошибки
    private DispatcherTimer? _logTimer;
    private const int MaxLogEntries = 500;

    private bool FilterLog(object o)
    {
        if (o is not LogEntry e) return false;
        return _logFilter switch
        {
            1 => e.Level is LogLevel.Warn or LogLevel.Error,
            2 => e.Level == LogLevel.Error,
            _ => true,
        };
    }

    /// <summary>Журнал копится в очереди и выводится пачкой раз в 200 мс — сотни строк от парсера не тормозят интерфейс.</summary>
    private void Log(string msg)
    {
        _logQueue.Enqueue(new LogEntry(DateTime.Now, msg));
        if (Dispatcher.CheckAccess()) EnsureLogTimer(); else Dispatcher.InvokeAsync(EnsureLogTimer);
    }

    private void EnsureLogTimer()
    {
        if (_logTimer == null)
        {
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _logTimer.Tick += (_, _) => FlushLog();
        }
        if (!_logTimer.IsEnabled) _logTimer.Start();
    }

    private void FlushLog()
    {
        _logTimer?.Stop();
        if (_logQueue.IsEmpty) return;
        while (_logQueue.TryDequeue(out var entry)) _log.Add(entry);
        while (_log.Count > MaxLogEntries) _log.RemoveAt(0);
        UpdateLogCount();
        if (_logView?.Cast<object>().LastOrDefault() is { } last) LogList.ScrollIntoView(last);
    }

    private void UpdateLogCount()
    {
        if (_logView == null) return;
        var shown = _logView.Cast<object>().Count();
        LogCount.Text = shown == _log.Count ? $"{_log.Count} записей" : $"{shown} из {_log.Count}";
    }

    private void LogFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out var i)) _logFilter = i;
        _logView?.Refresh();
        UpdateLogCount();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var saved = _log.ToList();
        _log.Clear();
        _logQueue.Clear();
        Log("Журнал очищен");
        OfferUndo($"Журнал очищен: {saved.Count} строк", () =>
        {
            // Записи, появившиеся после очистки, остаются в конце
            var since = _log.ToList();
            _log.Clear();
            foreach (var entry in saved.Concat(since)) _log.Add(entry);
            return Task.CompletedTask;
        });
    }

    /// <summary>Выбранные строки, которые сейчас видны, в порядке журнала.</summary>
    private List<LogEntry> SelectedLog() =>
        _logView == null ? new() : _logView.Cast<LogEntry>().Where(x => LogList.SelectedItems.Contains(x)).ToList();

    private void CopyEntries(IEnumerable<LogEntry> items, string done)
    {
        var text = string.Join(Environment.NewLine, items.Select(x => $"[{x.TimeText}] {x.Text}"));
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            ShowToast(done);
        }
        catch (Exception) { ShowToast("Не удалось скопировать"); }
    }

    /// <summary>Кнопка «Копировать»: выбранные строки, а если их нет, все видимые.</summary>
    private void CloseLog_Click(object sender, RoutedEventArgs e) => LogToggle.IsChecked = false;

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logView == null) return;
        var sel = SelectedLog();
        if (sel.Count > 0) CopyEntries(sel, sel.Count == 1 ? "Строка скопирована" : $"Скопировано строк: {sel.Count}");
        else CopyEntries(_logView.Cast<LogEntry>(), "Журнал скопирован");
    }

    /// <summary>Контекстное меню и Ctrl+C: выбранные строки, а если ничего не выбрано, текущая строка.</summary>
    private void CopyLine_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedLog();
        if (sel.Count == 0 && LogList.SelectedItem is LogEntry one) sel.Add(one);
        if (sel.Count > 0) CopyEntries(sel, sel.Count == 1 ? "Строка скопирована" : $"Скопировано строк: {sel.Count}");
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_logView != null) CopyEntries(_logView.Cast<LogEntry>(), "Журнал скопирован");
    }

    /// <summary>Двойной щелчок по строке копирует ее одну.</summary>
    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry one) CopyEntries(new[] { one }, "Строка скопирована");
    }

    private void LogList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyLine_Click(sender, e);
            e.Handled = true;
        }
    }
}
