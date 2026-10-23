using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Dnex.Services;

/// <summary>Минимальный DNS-клиент: A-запросы по UDP/53 и по DoH (RFC 8484), без сторонних библиотек.</summary>
public static class DnsWire
{
    public static byte[] BuildQuery(string host, ushort id, ushort qtype = 1)
    {
        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(id); W16(0x0100); // стандартный запрос, RD=1
        W16(1); W16(0); W16(0); W16(0);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            if (b.Length is 0 or > 63) throw new ArgumentException($"Некорректное имя: {host}");
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        W16(qtype); W16(1);
        return ms.ToArray();
    }

    public sealed class IdMismatchException : Exception { }

    public static List<IPAddress> ParseA(byte[] d, ushort? expectId)
    {
        if (d.Length < 12) throw new FormatException("Слишком короткий DNS-ответ");
        int id = (d[0] << 8) | d[1];
        if (expectId.HasValue && id != expectId.Value) throw new IdMismatchException();
        int rcode = d[3] & 0x0F;
        if (rcode != 0) throw new InvalidDataException(rcode switch
        {
            2 => "SERVFAIL",
            3 => "NXDOMAIN (домен не найден)",
            5 => "REFUSED (сервер отказал)",
            _ => $"RCODE {rcode}",
        });
        int qd = (d[4] << 8) | d[5];
        int an = (d[6] << 8) | d[7];
        int p = 12;
        if (an > 64) throw new FormatException("Подозрительно большой DNS-ответ");
        for (int i = 0; i < qd; i++) { p = SkipName(d, p) + 4; if (p > d.Length) throw new FormatException("Обрезанный DNS-ответ"); }

        var list = new List<IPAddress>();
        for (int i = 0; i < an; i++)
        {
            p = SkipName(d, p);
            if (p + 10 > d.Length) throw new FormatException("Обрезанный DNS-ответ");
            int type = (d[p] << 8) | d[p + 1];
            p += 8; // type, class, ttl
            int len = (d[p] << 8) | d[p + 1];
            p += 2;
            if (p + len > d.Length) throw new FormatException("Обрезанный DNS-ответ");
            if (type == 1 && len == 4) list.Add(new IPAddress(d.AsSpan(p, 4)));
            else if (type == 28 && len == 16) list.Add(new IPAddress(d.AsSpan(p, 16))); // AAAA: адрес IPv6
            p += len;
        }
        return list;
    }

    private static int SkipName(byte[] d, int p)
    {
        while (true)
        {
            if (p >= d.Length) throw new FormatException("Ошибка разбора имени");
            int l = d[p];
            if (l == 0) return p + 1;
            if ((l & 0xC0) == 0xC0) return p + 2; // сжатие
            if (l > 63) throw new FormatException("Некорректная метка в DNS-ответе");
            p += l + 1;
        }
    }

    public static async Task<(List<IPAddress> Ips, long Ms)> ResolveUdpAsync(
        IPAddress server, string host, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        var id = (ushort)Random.Shared.Next(1, 65535);
        var query = BuildQuery(host, id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), cts.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                if (!r.RemoteEndPoint.Address.Equals(server)) continue;
                try { return (ParseA(r.Buffer, id), sw.ElapsedMilliseconds); }
                catch (IdMismatchException) { /* чужой пакет — ждем дальше */ }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Нет ответа от {server} за {timeout.TotalSeconds:0} с");
        }
    }

    private static readonly HttpClient DohHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<(List<IPAddress> Ips, long Ms)> ResolveDohAsync(
        string template, string host, TimeSpan timeout, CancellationToken ct, ushort qtype = 1)
    {
        var url = template.Split('{')[0]; // поддержка шаблонов вида .../dns-query{?dns}
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new ByteArrayContent(BuildQuery(host, 0, qtype)); // 1 — IPv4 (A), 28 — IPv6 (AAAA)
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        req.Headers.Accept.ParseAdd("application/dns-message");
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await DohHttp.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"DoH вернул HTTP {(int)resp.StatusCode}");
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            return (ParseA(bytes, null), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"DoH {url}: нет ответа за {timeout.TotalSeconds:0} с");
        }
    }
}
