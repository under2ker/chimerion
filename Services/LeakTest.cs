using System.Net;
using System.Runtime.InteropServices;

namespace Dnex.Services;

/// <summary>Резолвер, который на самом деле пришел к тестовому серверу: адрес, владелец сети, место.</summary>
public sealed record ResolverInfo(string Ip, string Org, string Place);

/// <summary>
/// Тест утечки DNS через публичный тестовый сервис dnscheck.tools (проект addr.tools). Несколько TXT-запросов
/// со случайными метками идут через резолвер Windows, как у браузера. В ответе их сервер пишет, какой резолвер
/// к нему пришел: адрес, владельца и место. Если владельцев несколько, часть запросов идет мимо сервера DNEX.
/// Код addr.tools не используется, только их публичные тестовые имена.
/// </summary>
public static class LeakTest
{
    private const ushort DnsTypeText = 0x0010;
    private const uint DnsQueryBypassCache = 0x00000008;
    private const int DnsFreeRecordList = 1;

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr list, int freeType);

    /// <summary>
    /// TXT-записи имени через резолвер Windows, мимо его кэша. Раскладка DNS_RECORD: два указателя (pNext, pName),
    /// затем wType, wDataLength, Flags, dwTtl, dwReserved (всего 16 байт), затем данные: число строк и массив указателей.
    /// </summary>
    private static List<string> QueryTxt(string name)
    {
        var list = new List<string>();
        if (DnsQuery(name, DnsTypeText, DnsQueryBypassCache, IntPtr.Zero, out var res, IntPtr.Zero) != 0 || res == IntPtr.Zero)
            return list;
        try
        {
            int typeOffset = 2 * IntPtr.Size;
            int dataOffset = typeOffset + 16;
            for (var p = res; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
            {
                if ((ushort)Marshal.ReadInt16(p, typeOffset) != DnsTypeText) continue;
                int count = Marshal.ReadInt32(p, dataOffset);
                for (int i = 0; i < count && i < 32; i++)
                {
                    var sp = Marshal.ReadIntPtr(p, dataOffset + IntPtr.Size + i * IntPtr.Size);
                    if (sp != IntPtr.Zero) list.Add(Marshal.PtrToStringUni(sp) ?? "");
                }
            }
        }
        finally { DnsRecordListFree(res, DnsFreeRecordList); }
        return list;
    }

    /// <summary>
    /// Несколько запросов параллельно, не дольше timeout. Возвращает разные резолверы, которые ответили.
    /// Пусто, если сервис недоступен: тогда утечку не оцениваем.
    /// </summary>
    public static async Task<List<ResolverInfo>> RunAsync(int queries = 6, TimeSpan? timeout = null)
    {
        var tasks = Enumerable.Range(0, queries)
            .Select(_ => Task.Run(() => QueryTxt($"{Random.Shared.Next(0x10000000, int.MaxValue):x8}.test.dnscheck.tools")))
            .ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout ?? TimeSpan.FromSeconds(8)));
        return tasks.Where(t => t.IsCompletedSuccessfully)
            .SelectMany(t => t.Result)
            .Select(ParseFrom).OfType<ResolverInfo>()
            .GroupBy(r => r.Ip).Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Строка ответа «FROM: 83.217.211.149#45634 (UDP) - Baykov Ilya Sergeevich - Dronten, Flevoland, NL».
    /// Другие строки (EDNS, ID) пропускаются.
    /// </summary>
    public static ResolverInfo? ParseFrom(string txt)
    {
        if (!txt.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase)) return null;
        var body = txt[5..].Trim();
        int hash = body.IndexOf('#');
        var ipText = hash > 0 ? body[..hash] : body.Split(' ')[0];
        if (!IPAddress.TryParse(ipText, out var ip)) return null;
        var parts = body.Split(" - ");
        var org = parts.Length > 1 ? parts[1].Trim() : "";
        var place = parts.Length > 2 ? string.Join(" - ", parts[2..]).Trim() : "";
        return new ResolverInfo(ip.ToString(), org, place);
    }

    /// <summary>Похоже на утечку: ответили резолверы разных владельцев сетей.</summary>
    public static bool LooksLikeLeak(IReadOnlyCollection<ResolverInfo> list) =>
        list.Select(r => r.Org).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
}
