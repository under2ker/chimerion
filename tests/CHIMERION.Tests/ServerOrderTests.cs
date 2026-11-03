using System.ComponentModel;
using System.Text.Json;
using System.Windows.Data;
using Dnex.Models;
using Xunit;

namespace Dnex.Tests;

public class ServerOrderTests
{
    [Fact]
    public async Task Manual_addition_stays_above_connected_and_favorite_after_sorting()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var connected = new DnsServer { Name = "connected", IsConnected = true, IsFavorite = true };
                var added = new DnsServer { Name = "added", PinnedAfterManualAdd = true };
                var view = new ListCollectionView(new[] { connected, added });
                view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Name), ListSortDirection.Descending));
                MainWindow.EnsurePinnedSort(view.SortDescriptions, true);
                Assert.Same(added, view.GetItemAt(0));
                Assert.Same(connected, view.GetItemAt(1));
                Assert.DoesNotContain("PinnedAfterManualAdd", JsonSerializer.Serialize(added));
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(DnsServer.Name), ListSortDirection.Ascending));
                MainWindow.EnsurePinnedSort(view.SortDescriptions, false);
                Assert.Same(added, view.GetItemAt(0));
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task;
    }
}
