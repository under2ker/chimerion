using Dnex.Services;
using System.IO;
using System.Net;
using Xunit;

namespace DNEX.Tests;

/// <summary>Разбор DNS-ответов из сети: пакеты приходят от недоверенного сервера, поэтому проверяем границы.</summary>
public class DnsWireTests
{
    /// <summary>Собирает ответ с одним A-record для имени a.bc.</summary>
    private static byte[] Response(ushort id, byte[] ip, byte rcode = 0, bool truncate = false)
    {
        var b = new List<byte>
        {
            (byte)(id >> 8), (byte)id, 0x81, rcode, // флаги: ответ, RD, RA
            0, 1, 0, 1, 0, 0, 0, 0,                  // qd=1, an=1
            1, (byte)'a', 2, (byte)'b', (byte)'c', 0, // имя a.bc
            0, 1, 0, 1,                              // тип A, класс IN
            0xC0, 12,                                // сжатое имя (указатель на вопрос)
            0, 1, 0, 1, 0, 0, 0, 60,                 // тип A, класс IN, TTL
            0, 4,                                    // длина RDATA
        };
        b.AddRange(ip);
        var arr = b.ToArray();
        return truncate ? arr[..^2] : arr;
    }

    [Fact]
    public void ParseA_ReadsIPv4Address()
    {
        var ips = DnsWire.ParseA(Response(0x1234, new byte[] { 1, 2, 3, 4 }), 0x1234);
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4") }, ips);
    }

    [Fact]
    public void ParseA_RejectsAnotherQueryId()
    {
        Assert.Throws<DnsWire.IdMismatchException>(() =>
            DnsWire.ParseA(Response(0x1234, new byte[] { 1, 2, 3, 4 }), 0x9999));
    }

    [Fact]
    public void ParseA_ReportsNxDomain()
    {
        Assert.Throws<InvalidDataException>(() =>
            DnsWire.ParseA(Response(0x1234, new byte[] { 1, 2, 3, 4 }, rcode: 3), 0x1234));
    }

    [Fact]
    public void ParseA_RejectsTruncatedPacket()
    {
        Assert.Throws<FormatException>(() =>
            DnsWire.ParseA(Response(0x1234, new byte[] { 1, 2, 3, 4 }, truncate: true), 0x1234));
    }

    [Fact]
    public void ParseA_RejectsTooShortPacket()
    {
        Assert.Throws<FormatException>(() => DnsWire.ParseA(new byte[] { 1, 2, 3 }, null));
    }

    [Fact]
    public void BuildQuery_EncodesLabels()
    {
        var q = DnsWire.BuildQuery("a.bc", 0x1234);
        Assert.Equal(new byte[] { 0x12, 0x34 }, q[..2]);
        Assert.Equal(new byte[] { 1, (byte)'a', 2, (byte)'b', (byte)'c', 0 }, q[12..18]);
    }

    [Fact]
    public void BuildQuery_RejectsEmptyLabel()
    {
        Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery("a..bc", 1));
    }
}
