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
    /// <summary>Журнал открывается отдельным окном (его держат рядом, пока что-то делают в программе).</summary>
    private void LogToggle_Click(object sender, RoutedEventArgs e) { LogToggle.IsChecked = false; OpenLog(); }

    private void OpenLog() => LogWindow.Show(this, _log, StoredLog, () => ClearLog_Click(this, new RoutedEventArgs()));


    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private DispatcherTimer? _logTimer;
    private const int MaxLogEntries = 500;

    /// <summary>Журнал копится в очереди и выводится пачкой раз в 200 мс — сотни строк от парсера не тормозят интерфейс.</summary>
    /// <summary>
    /// Режим скрытия данных включили или выключили: уже записанные строки журнала маскируются заново (снять маску с
    /// записанного нельзя, личные значения туда больше не попадают), панель проверки перерисовывается.
    /// </summary>
    private void OnPrivacyChanged()
    {
        if (PrivacyGuard.Enabled)
            for (int i = 0; i < _log.Count; i++)
            {
                var masked = PrivacyGuard.Mask(_log[i].Text);
                if (masked != _log[i].Text) _log[i] = new LogEntry(_log[i].Time, masked);
            }
        if (_before != null || _after != null) ShowCompare(DnsSwitcher.GetActive() == null && _after == null);
        Log(!PrivacyGuard.Enabled ? "Режим скрытия данных выключен"
            : PrivacyGuard.HidesWindows ? "Режим скрытия данных включен: окна не попадают в захват экрана, личные данные скрыты"
            : "Режим скрытия данных включен: личные данные на экране скрыты, окна на записи видны");
    }

    private void Log(string msg)
    {
        // Личные значения (IP, место, провайдер, имя пользователя) в журнал не попадают вовсе, ни в память, ни на диск
        _logQueue.Enqueue(new LogEntry(DateTime.Now, PrivacyGuard.Scrub(msg)));
        SupportTrace.Write($"Событие: {msg}");
        if (Dispatcher.CheckAccess()) EnsureLogTimer(); else Dispatcher.InvokeAsync(EnsureLogTimer);
    }

    private void EnsureLogTimer()
    {
        EnsureSupportTray();
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
        var added = new List<LogEntry>();
        while (_logQueue.TryDequeue(out var entry)) { _log.Add(entry); added.Add(entry); }
        if (!SupportTrace.Enabled) LogStore.Append(added.Select(x => (x.Time, x.Text)));
        while (_log.Count > MaxLogEntries) _log.RemoveAt(0);
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

    private void CopyEntries(IEnumerable<LogEntry> items, string done, bool withDate = false)
    {
        var text = LogWindow.Format(items, withDate);
        if (text.Length == 0) return;
        if (Ui.CopyText(text)) ShowToast(done);
        else ShowToast("Не удалось скопировать: буфер обмена занят другой программой", "Warn");
    }

    /// <summary>Весь журнал с диска за 3 дня (все запуски).</summary>
    private List<LogEntry> StoredLog()
    {
        FlushLog();
        SupportTrace.Write("Подготовка полного журнала для копирования или отчета");
        SupportTrace.Flush();
        return LogStore.ReadAll().Select(x => new LogEntry(x.Time, x.Text)).ToList();
    }

    /// <summary>Журнал для отчета диагностики: все записи за 3 дня с датой.</summary>
    /// <summary>Сколько последних строк журнала идет в отчет: отчет копируют и открывают черновиком на GitHub.</summary>
    internal const int ReportLogLines = 4000;

    private IEnumerable<string> LogForReport()
    {
        var all = StoredLog();
        if (all.Count == 0) all = _log.ToList();
        var lines = all.Select(l => $"{l.Time:dd.MM HH:mm:ss} {l.Text}").ToList();
        // Полный журнал за 3 дня бывает в несколько мегабайт: в отчет последние строки, весь журнал копируется в окне «Журнал»
        if (lines.Count <= ReportLogLines) return lines;
        return lines.Skip(lines.Count - ReportLogLines).Prepend($"(показаны последние {ReportLogLines} строк из {lines.Count}; весь журнал: окно «Журнал», «Скопировать журнал за 3 дня»)");
    }
}
