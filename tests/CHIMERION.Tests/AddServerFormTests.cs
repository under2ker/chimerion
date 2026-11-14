namespace Dnex.Tests;

/// <summary>Проверка полей окна «Добавить сервер». Адреса выдуманные.</summary>
public class AddServerFormTests
{
    [Fact]
    public void Ip_only_server_takes_name_from_ip()
    {
        var (server, error, field) = AddServerForm.Validate("", " 203.0.113.10 ", "", "");
        Assert.Null(error);
        Assert.Equal(AddServerField.None, field);
        Assert.Equal("203.0.113.10", server!.Name);
        Assert.Equal("203.0.113.10", server.Primary);
        Assert.Null(server.Secondary);
        Assert.Null(server.DohUrl);
        Assert.True(server.Manual);
    }

    [Fact]
    public void Doh_only_server_takes_name_from_host_and_has_no_ip()
    {
        var (server, error, _) = AddServerForm.Validate(null, null, null, "https://dns.example.test/dns-query/token");
        Assert.Null(error);
        Assert.Equal("dns.example.test", server!.Name);
        Assert.Equal("", server.Primary);
        Assert.Equal("https://dns.example.test/dns-query/token", server.DohUrl);
    }

    [Fact]
    public void All_fields_are_kept()
    {
        var (server, _, _) = AddServerForm.Validate(" Мой DNS ", "203.0.113.10", "198.51.100.20", "https://dns.example.test/dns-query");
        Assert.Equal("Мой DNS", server!.Name);
        Assert.Equal("198.51.100.20", server.Secondary);
        Assert.Equal("https://dns.example.test/dns-query", server.DohUrl);
    }

    [Theory]
    [InlineData("2001:db8::53")]
    [InlineData("203.0.113.10")]
    public void Full_ipv4_and_ipv6_are_accepted(string ip)
    {
        var (server, error, _) = AddServerForm.Validate("", ip, "", "");
        Assert.Null(error);
        Assert.Equal(ip, server!.Primary);
    }

    [Theory]
    [InlineData("", "", "", "Primary", "Укажите основной IP")]
    [InlineData("203.0.113.10:53", "", "", "Primary", "Нужен IP вида")]
    [InlineData("1.2.3", "", "", "Primary", "Нужен IP вида")]
    [InlineData("203.0.113", "", "", "Primary", "Нужен IP вида")]
    [InlineData("10", "", "", "Primary", "Нужен IP вида")]
    [InlineData("abc", "", "https://dns.example.test/dns-query", "Primary", "Нужен IP вида")]
    [InlineData("", "", "http://dns.example.test/dns-query", "Doh", "https://")]
    [InlineData("", "", "dns.example.test", "Doh", "https://")]
    [InlineData("203.0.113.10", "", "dns.example.test", "Doh", "https://")]
    [InlineData("203.0.113.10", "не адрес", "", "Secondary", "Нужен IP вида")]
    [InlineData("203.0.113.10", "198.51.100", "", "Secondary", "Нужен IP вида")]
    public void Wrong_input_points_to_its_field(string primary, string secondary, string doh, string expected, string text)
    {
        var (server, error, field) = AddServerForm.Validate("Имя", primary, secondary, doh);
        Assert.Null(server);
        Assert.Equal(expected, field.ToString());
        Assert.Contains(text, error);
    }
}
