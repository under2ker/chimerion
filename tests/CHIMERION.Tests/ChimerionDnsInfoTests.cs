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

    [Fact]
    public void Promo_row_has_no_addresses_and_stays_on_top()
    {
        var promo = ChimerionDnsInfo.PromoRow();
        Assert.True(promo.IsPromo);
        Assert.False(promo.IsNotPromo);
        Assert.True(string.IsNullOrEmpty(promo.Primary));
        Assert.Null(promo.Secondary);
        Assert.Null(promo.DohUrl);
        Assert.True(promo.PinnedAfterManualAdd);
        promo.PinnedAfterManualAdd = false; // добавление своего сервера снимает закрепление со всех строк
        Assert.True(promo.PinnedAfterManualAdd);
        Assert.Equal(-1, promo.Rank);
        Assert.Equal("Премиум", promo.StatusText);
        Assert.Equal("promo:chimerion-dns", promo.Key);
        Assert.DoesNotMatch(@"\d+\.\d+\.\d+\.\d+", promo.Address);
    }

    [Fact]
    public void Promo_row_is_never_saved_or_exported()
    {
        var own = new Models.DnsServer { Name = "Свой", Primary = "203.0.113.10" };
        var saved = Models.DnsServer.ForSaving(new[] { ChimerionDnsInfo.PromoRow(), own });
        Assert.Equal(own, Assert.Single(saved));
        var json = System.Text.Json.JsonSerializer.Serialize(saved, Services.Storage.Options);
        Assert.DoesNotContain("CHIMERION DNS", json);
        Assert.DoesNotContain("isPromo", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Promo_row_survives_the_list_size_limit()
    {
        var promo = ChimerionDnsInfo.PromoRow();
        var list = new List<Models.DnsServer> { promo };
        for (int i = 0; i < 20_000; i++) list.Add(new Models.DnsServer { Name = "s" + i, Primary = "203.0.113.1", AddedAt = DateTime.Now });
        Assert.True(Services.ServerListGuard.Purge(list, null) > 0);
        Assert.Contains(promo, list);
    }

    [Fact]
    public void Contact_is_in_the_window_texts()
    {
        Assert.Equal("@dev_under2ker", ChimerionDnsInfo.ContactName);
        Assert.Equal("https://t.me/dev_under2ker", ChimerionDnsInfo.ContactUrl);
    }
}
