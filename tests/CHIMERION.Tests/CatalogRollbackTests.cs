using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Старый подписанный список вместо уже принятого не берется (аудит CR-16).</summary>
public class CatalogRollbackTests
{
    [Theory]
    [InlineData("2026-11-20", "2026-11-10", true)]
    [InlineData("2026-11-10", "2026-11-10", true)]
    [InlineData("2026-11-01", "2026-11-10", false)]
    [InlineData("2026-11-01", null, true)]          // прежнего списка нет
    [InlineData("2026-11-01", "мусор", true)]       // прежняя дата не читается: как раньше
    [InlineData("мусор", "2026-11-10", false)]
    public void Older_list_is_refused(string fresh, string? had, bool ok) => Assert.Equal(ok, RemoteCatalog.NotOlder(fresh, had));
}
