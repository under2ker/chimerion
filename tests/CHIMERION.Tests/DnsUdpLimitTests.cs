using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

public class DnsUdpLimitTests
{
    private static readonly byte[] Question = { 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1 };

    private static byte[] Query(int? ednsSize)
    {
        var header = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, (byte)(ednsSize is null ? 0 : 1) };
        var opt = ednsSize is { } size ? new byte[] { 0, 0, 41, (byte)(size >> 8), (byte)size, 0, 0, 0, 0, 0, 0 } : Array.Empty<byte>();
        return header.Concat(Question).Concat(opt).ToArray();
    }

    /// <summary>Ответ на запрос: заголовок с QR, вопрос и тело заданной длины.</summary>
    private static byte[] Answer(int length)
    {
        var head = new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 9, 0, 0, 0, 0 }.Concat(Question).ToArray();
        return head.Concat(new byte[Math.Max(0, length - head.Length)]).ToArray();
    }

    [Theory]
    [InlineData(null, 512)]
    [InlineData(1232, 1232)]
    [InlineData(100, 512)]
    [InlineData(65000, 4096)]
    public void Limit_follows_the_edns_size_of_the_query(int? edns, int expected) =>
        Assert.Equal(expected, DnsUdpLimit.MaxSize(Query(edns)));

    [Fact]
    public void Answer_that_fits_is_returned_unchanged()
    {
        var answer = Answer(500);
        Assert.Same(answer, DnsUdpLimit.Fit(Query(null), answer));
        var big = Answer(1200);
        Assert.Same(big, DnsUdpLimit.Fit(Query(1232), big));
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData(1232, 1500)]
    public void Long_answer_is_truncated_with_tc_and_keeps_the_question(int? edns, int length)
    {
        var cut = DnsUdpLimit.Fit(Query(edns), Answer(length));
        Assert.Equal(12 + Question.Length, cut.Length);
        Assert.Equal(0x02, cut[2] & 0x02);
        Assert.Equal(new byte[] { 0x12, 0x34 }, cut[..2]);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }, cut[4..12]);
        Assert.Equal(Question, cut[12..]);
    }

    [Fact]
    public void Broken_query_falls_back_to_the_classic_limit()
    {
        var broken = new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0x3F, 1, 2 };
        Assert.Equal(512, DnsUdpLimit.MaxSize(broken));
        Assert.Equal(512, DnsUdpLimit.MaxSize(new byte[] { 1, 2, 3 }));
    }
}
