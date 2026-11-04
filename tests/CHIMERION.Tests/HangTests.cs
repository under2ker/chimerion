using System.Diagnostics;
using System.Windows.Threading;
using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Сценарии, в которых программа раньше замирала: копирование в буфер и зависший PowerShell.</summary>
[Collection("scenarios")]
public class HangTests
{
    [Fact]
    public async Task Hung_read_only_powershell_script_is_stopped_by_timeout()
    {
        var was = DnsSwitcher.ReadOnlyScriptTimeout;
        DnsSwitcher.ReadOnlyScriptTimeout = TimeSpan.FromSeconds(2);
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, output) = await DnsSwitcher.RunPsAsync("Start-Sleep -Seconds 60; Write-Output 'дождался'");
            Assert.Equal(-1, code);
            Assert.Contains("не ответил", output);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"ожидание {sw.Elapsed}");
        }
        finally { DnsSwitcher.ReadOnlyScriptTimeout = was; }
    }

    [Fact]
    public async Task Read_only_powershell_script_within_timeout_still_returns_output()
    {
        var (code, output) = await DnsSwitcher.RunPsAsync("Write-Output 'готово'");
        Assert.Equal(0, code);
        Assert.Equal("готово", output.Trim());
    }

    [Fact]
    public async Task Copy_to_clipboard_does_not_freeze_the_window_thread()
    {
        await OnSta(() =>
        {
            string? saved = null;
            try { if (System.Windows.Clipboard.ContainsText()) saved = System.Windows.Clipboard.GetText(); } catch (Exception) { }
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var ok = Ui.CopyText("проверка буфера " + i);
                    // Раньше каждый щелчок по ID стоил до 4 с, а при занятом буфере до 11 с
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"копирование заняло {sw.Elapsed}");
                    if (ok) Assert.Equal("проверка буфера " + i, ReadClipboard());
                }
            }
            finally { if (saved != null) Ui.CopyText(saved); }
        });
    }

    /// <summary>
    /// Чтение буфера с повтором: сразу после записи его на миллисекунды открывают программы, которые за ним следят
    /// (общий буфер VirtualBox, RDP, менеджеры буфера), и чтение падает с CLIPBRD_E_CANT_OPEN.
    /// </summary>
    internal static string ReadClipboard()
    {
        for (int i = 0; ; i++)
        {
            try { return System.Windows.Clipboard.GetText(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 20) { Thread.Sleep(50); }
        }
    }

    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
