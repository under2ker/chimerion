namespace Dnex.Tests;

/// <summary>Сведения окна «CHIMERION DNS»: цены совпадают с ботом и магазином, в окне нет адреса сервера и токенов.</summary>
public class ChimerionDnsInfoTests
{
    [Fact]
    public void Plans_match_the_bot_and_the_shop()
    {
        Assert.Equal(new[] { (30, 299, 299), (90, 799, 799), (365, 2490, 2490) },
            ChimerionDnsInfo.Plans.Select(p => (p.Days, p.Rub, p.Stars)));
        Assert.All(ChimerionDnsInfo.Plans, p => Assert.StartsWith(p.Days + " ", p.Title));
    }

    [Fact]
    public void Links_are_https_and_texts_follow_project_rules()
    {
        Assert.All(new[] { ChimerionDnsInfo.BotUrl, ChimerionDnsInfo.ChannelUrl, ChimerionDnsInfo.ShopUrl }, u => Assert.StartsWith("https://", u));
        var text = string.Join(" ", ChimerionDnsInfo.Benefits) + ChimerionDnsInfo.Region;
        Assert.DoesNotContain("ё", text);
        Assert.DoesNotContain("—", text);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", text);
    }
}
