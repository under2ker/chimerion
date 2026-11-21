using Dnex.Services;

namespace Dnex.Tests;

/// <summary>Серверы общей карты: проверенные сервером CHIMERION берутся большей пачкой, чем присланные пользователями.</summary>
public class CrowdVerifiedTests
{
    private static Crowd.Found F(string s, bool verified) => new(s, s, null, 0, 0, 0, verified);

    [Fact]
    public void Takes_up_to_ten_from_users_and_thirty_verified_skipping_known()
    {
        var found = Enumerable.Range(0, 15).Select(i => F("10.0.0." + i, false))
            .Concat(Enumerable.Range(0, 50).Select(i => F("10.1.0." + i, true))).ToList();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "10.0.0.0", "10.1.0.0" };

        var pick = Crowd.PickNew(found, known);

        Assert.Equal(Crowd.NewFromUsers, pick.Count(f => !f.Verified));
        Assert.Equal(Crowd.NewVerified, pick.Count(f => f.Verified));
        Assert.DoesNotContain(pick, f => known.Contains(f.Server));
        Assert.Equal(pick.Count, pick.Select(f => f.Server).Distinct().Count());
    }

    [Fact]
    public void Old_answer_without_verified_flag_behaves_as_before()
    {
        var found = Enumerable.Range(0, 25).Select(i => new Crowd.Found("10.2.0." + i, null, null, 90, 30, 3)).ToList();
        Assert.Equal(10, Crowd.PickNew(found, new HashSet<string>()).Count);
    }
}

public class ReadyListTests
{
    [Fact]
    public void Ready_list_takes_all_verified_new_ones_without_limit()
    {
        var found = Enumerable.Range(0, 60).Select(i => new Crowd.Found("10.3.0." + i, null, null, 0, 0, 0, true))
            .Append(new Crowd.Found("10.3.0.5", null, null, 0, 0, 0, true))          // повтор
            .Append(new Crowd.Found("10.4.0.1", null, null, 90, 30, 3)).ToList();     // от пользователей: не для этой кнопки
        var known = new HashSet<string> { "10.3.0.0" };
        var r = Crowd.ReadyList(found, known);
        Assert.Equal(59, r.Count);
        Assert.All(r, f => Assert.True(f.Verified));
    }
}
