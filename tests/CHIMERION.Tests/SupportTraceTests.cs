using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

[Collection("scenarios")]
public sealed class SupportTraceTests : IDisposable
{
    public SupportTraceTests()
    {
        SupportTrace.Stop();
        if (Directory.Exists(LogStore.Dir)) Directory.Delete(LogStore.Dir, true);
        PrivacyGuard.ForgetPersonal();
        SupportTrace.Start();
    }

    public void Dispose()
    {
        SupportTrace.Stop();
        if (Directory.Exists(LogStore.Dir)) Directory.Delete(LogStore.Dir, true);
        PrivacyGuard.ForgetPersonal();
    }

    private static string Text()
    {
        SupportTrace.Flush();
        return string.Join("\n", LogStore.ReadAll().Select(l => l.Text));
    }

    [Fact]
    public void Detail_is_written_for_export_with_session_order_and_duration()
    {
        using (var operation = SupportTrace.Begin("Проверка")) operation.Detail("шаг 1");
        var text = Text();
        Assert.Contains("Начало сеанса", text);
        Assert.Contains("сеанс=", text);
        Assert.Contains("операция=", text);
        Assert.Contains("шаг 1", text);
        Assert.Contains("длительность=", text);
        Assert.Contains("Итог: Проверка; завершено", text);
    }

    [Fact]
    public async Task Http_records_status_and_timing_without_url_headers_or_response_body()
    {
        var handler = new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("private-response"), Headers = { { "Set-Cookie", "private-cookie" } },
        });
        using var http = new HttpClient(handler);
        using var response = await SupportTrace.GetAsync(http, "https://example.test/private-path?session=private-query",
            HttpCompletionOption.ResponseHeadersRead, default, "Проверка API");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, handler.Calls);
        var text = Text();
        Assert.Contains("HTTP 403", text);
        Assert.Contains("длительность=", text);
        foreach (var secret in new[] { "example.test", "private-path", "private-query", "private-cookie", "private-response" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task Failure_keeps_exception_codes_and_parent_operation_but_not_exception_message()
    {
        using var http = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("unmarked-private-secret", null, HttpStatusCode.BadGateway)));
        await Assert.ThrowsAsync<HttpRequestException>(() => SupportTrace.RunAsync("Внешняя операция", () =>
            SupportTrace.GetAsync(http, "https://example.test/", HttpCompletionOption.ResponseHeadersRead, default, "Внутренний запрос")));
        var text = Text();
        Assert.Contains("HttpRequestException", text);
        Assert.Contains("HTTP=502", text);
        Assert.Contains("HResult=", text);
        Assert.DoesNotContain("unmarked-private-secret", text);
        Assert.Contains("Итог: Внешняя операция; ошибка", text);
        Assert.Contains("Итог: Внутренний запрос; ошибка", text);
        Assert.DoesNotContain("Начало: Внутренний запрос; родитель=нет", text);
    }

    [Fact]
    public async Task Cancelled_operation_is_not_reported_as_success()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SupportTrace.RunAsync("Отмена",
            () => Task.FromCanceled<int>(cancelled.Token), cancelled.Token));
        Assert.Contains("Итог: Отмена; отменено", Text());
    }

    [Fact]
    public void Direct_disk_writes_are_scrubbed_and_old_timestamp_format_still_loads()
    {
        var now = DateTime.Now;
        LogStore.Append([(now, "server=203.0.113.27; token=private-token; mail=sample@example.test")]);
        File.AppendAllText(Path.Combine(LogStore.Dir, now.ToString("yyyy-MM-dd") + ".log"),
            $"{now:yyyy-MM-dd HH:mm:ss}\tстарая запись 2001:db8::1\n");
        var text = Text();
        foreach (var secret in new[] { "203.0.113.27", "private-token", "sample@example.test", "2001:db8::1" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains("старая запись", text);
    }

    [Fact]
    public async Task Concurrent_operations_are_all_flushed_before_export()
    {
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            using var operation = SupportTrace.Begin($"Параллельная {i}");
            operation.Detail("выполнен шаг");
        })));
        var lines = Text().Split('\n');
        Assert.Equal(50, lines.Count(l => l.Contains("Начало: Параллельная")));
        Assert.Equal(50, lines.Count(l => l.Contains("Итог: Параллельная")));
    }

    [Fact]
    public async Task Ui_identity_accepts_only_source_literals_and_never_evaluates_dynamic_captions()
    {
        await OnSta(() =>
        {
            var button = new Button { Content = "private-input" };
            SupportTraceUi.Identify(button, "Action", 3, "\"Подключить\"");
            Assert.Contains("Подключить", SupportTraceUi.Describe(button));
            SupportTraceUi.Identify(button, "Action", 3, "$\"private-input\"");
            Assert.DoesNotContain("private-input", SupportTraceUi.Describe(button));
            SupportTraceUi.Identify(button, "Action", 3, "\"Подключить \" + value + \" сейчас\"");
            Assert.DoesNotContain("value", SupportTraceUi.Describe(button));
        });
    }

    [Fact]
    public async Task Ui_records_clicks_without_field_values_titles_or_programmatic_checkbox_changes()
    {
        await OnSta(() =>
        {
            SupportTraceUi.Install();
            var button = new Button { Name = "TraceAction", Content = "private-button-caption" };
            SupportTraceUi.Identify(button, "TestAction", 17);
            var check = new CheckBox { Content = "private-check-caption" };
            var combo = new ComboBox { Items = { "private-choice-1", "private-choice-2" } };
            var panel = new StackPanel();
            panel.Children.Add(button); panel.Children.Add(check); panel.Children.Add(combo);
            panel.Children.Add(new TextBox { Text = "private-input" });
            var window = new Window { Title = "private-window-title", Content = panel, Width = 220, Height = 160,
                ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                check.IsChecked = true;
                combo.SelectedIndex = 1;
                Assert.DoesNotContain("Нажатие:", Text());
                Assert.DoesNotContain("Выбор:", Text());
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, check));
            }
            finally { window.Close(); }
        });
        var text = Text();
        Assert.Contains("Button(TestAction:17)", text);
        Assert.Contains("включено=True", text);
        Assert.Contains("Окно закрыто:", text);
        Assert.DoesNotContain("private-", text);
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

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
