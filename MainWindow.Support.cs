using Dnex.Services;

namespace Dnex;

/// <summary>Ответы разработчика на обращения (Support): раз в полчаса, пока есть открытые обращения, и уведомление об изменениях.</summary>
public partial class MainWindow
{
    private DateTime _supportPolled = DateTime.MinValue;

    private void InitSupport() => TicketsWindow.OnUpdate = UpdateCard_Click;

    /// <summary>
    /// Спросить статусы открытых обращений. Без открытых обращений запросов нет вовсе. Изменение: уведомление в окне
    /// или значком в трее (ответ нельзя пропустить, поэтому и при скрытом окне). Нажатие на уведомление открывает «Мои обращения».
    /// </summary>
    private async Task SupportSyncAsync()
    {
        var store = Support.Current;
        if (!Support.HasOpen(store, DateTime.Now) || DateTime.Now - _supportPolled < Support.PollEvery) return;
        _supportPolled = DateTime.Now;
        var changed = await Support.PollAsync(store);
        if (changed.Count == 0) return;
        foreach (var t in changed) Log($"Обращение #{t.Id}: {Support.StatusLine(t).ToLowerInvariant()}");
        var text = changed.Count == 1 ? Support.NoticeText(changed[0]) : $"Новых ответов по обращениям: {changed.Count}";
        if (!IsVisible || WindowState == System.Windows.WindowState.Minimized)
            Balloon(8000, NoticeTitle("Обращения"), PrivacyGuard.Mask(Loc.T(text)), System.Windows.Forms.ToolTipIcon.Info,
                () => { ShowFromTray(); TicketsWindow.Show(); });
        else
            ShowToast(text + ". Откройте «Диагностика → Мои обращения»", "Ok", "Обращения");
    }
}
