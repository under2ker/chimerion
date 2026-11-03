using System.Windows.Controls;
using Xunit;

namespace Dnex.Tests;

public class SettingsSwitchTests
{
    [Fact]
    public async Task Backup_can_be_unchecked_when_automatic_switching_is_disabled()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var allow = new CheckBox { IsChecked = true };
                var backup = new CheckBox { IsChecked = true };
                var autoSwitch = new CheckBox { IsChecked = true };
                    SettingsWindow.UpdateSwitchOptions(allow, backup, autoSwitch);
                    allow.IsChecked = false;
                    SettingsWindow.UpdateSwitchOptions(allow, backup, autoSwitch);
                    Assert.True(backup.IsEnabled);
                    Assert.False(autoSwitch.IsEnabled);
                    backup.IsChecked = false;
                    allow.IsChecked = true;
                    SettingsWindow.UpdateSwitchOptions(allow, backup, autoSwitch);
                    Assert.False(backup.IsChecked);
                    Assert.True(autoSwitch.IsEnabled);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task;
    }
}
