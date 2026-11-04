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
    [InlineData("", "", "", "Primary", "Укажите основной IP")]
    [InlineData("203.0.113.10:53", "", "", "Primary", "не похож на IP-адрес")]
    [InlineData("", "", "http://dns.example.test/dns-query", "Doh", "https://")]
    [InlineData("", "", "dns.example.test", "Doh", "https://")]
    [InlineData("203.0.113.10", "не адрес", "", "Secondary", "не адрес")]
    public void Wrong_input_points_to_its_field(string primary, string secondary, string doh, string expected, string text)
    {
        var (server, error, field) = AddServerForm.Validate("Имя", primary, secondary, doh);
        Assert.Null(server);
        Assert.Equal(expected, field.ToString());
        Assert.Contains(text, error);
    }
}
