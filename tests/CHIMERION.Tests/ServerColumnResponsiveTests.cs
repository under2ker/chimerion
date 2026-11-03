using Xunit;

namespace Dnex.Tests;

public class ServerColumnResponsiveTests
{
    [Fact]
    public void Columns_reappear_when_the_table_becomes_wide_again()
    {
        var wide = MainWindow.ServerColumnVisibility(1600, true);
        Assert.True(wide.Services && wide.Source && wide.Ai && wide.Stability && wide.Location);
        var narrow = MainWindow.ServerColumnVisibility(1000, true);
        Assert.True(narrow.Services);
        Assert.False(narrow.Stability);
        Assert.Equal(wide, MainWindow.ServerColumnVisibility(1600, true));
        Assert.Equal((false, false, false, false, false), MainWindow.ServerColumnVisibility(1600, false));
    }
}
