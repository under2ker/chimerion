namespace Dnex.Services;

/// <summary>
/// Размер UDP-ответа по правилам DNS: не больше, чем клиент объявил в EDNS (без EDNS 512 байт). Длинный ответ
/// заменяется усеченным с флагом TC, и клиент повторяет запрос по TCP, который прокси тоже принимает.
/// </summary>
internal static class DnsUdpLimit
{
    internal const int Classic = 512;
    internal const int Max = 4096;

    /// <summary>Размер из записи OPT запроса, в пределах 512–4096; при ошибке разбора 512.</summary>
    internal static int MaxSize(byte[] query)
    {
        try
        {
            if (query.Length < 12) return Classic;
            int qd = (query[4] << 8) | query[5], an = (query[6] << 8) | query[7], ns = (query[8] << 8) | query[9], ar = (query[10] << 8) | query[11];
            int i = 12;
            for (int q = 0; q < qd; q++) i = SkipName(query, i) + 4;
            for (int r = 0; r < an + ns; r++) i = SkipRecord(query, i);
            for (int r = 0; r < ar; r++)
            {
                int start = SkipName(query, i);
                int type = (query[start] << 8) | query[start + 1];
                if (type == 41) return Math.Clamp((query[start + 2] << 8) | query[start + 3], Classic, Max);
                i = SkipRecord(query, i);
            }
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentException) { }
        return Classic;
    }

    /// <summary>Ответ, который помещается в UDP для этого запроса. Помещающийся возвращается без изменений.</summary>
    internal static byte[] Fit(byte[] query, byte[] answer)
    {
        int limit = MaxSize(query);
        if (answer.Length <= limit) return answer;
        int end = 12;
        int qd = (answer[4] << 8) | answer[5];
        try
        {
            for (int q = 0; q < qd; q++) end = SkipName(answer, end) + 4;
            if (end > answer.Length || end > limit) { end = 12; qd = 0; }
        }
        catch (IndexOutOfRangeException) { end = 12; qd = 0; }
        catch (ArgumentException) { end = 12; qd = 0; }
        var cut = answer.AsSpan(0, end).ToArray();
        cut[2] |= 0x02;                                   // TC: ответ усечен
        cut[4] = (byte)(qd >> 8); cut[5] = (byte)qd;
        for (int k = 6; k < 12; k++) cut[k] = 0;           // без ответов, полномочий и дополнительных записей
        return cut;
    }

    private static int SkipName(byte[] d, int i)
    {
        for (int guard = 0; guard < 128; guard++)
        {
            int len = d[i];
            if (len == 0) return i + 1;
            if ((len & 0xC0) == 0xC0) { _ = d[i + 1]; return i + 2; }
            if ((len & 0xC0) != 0) throw new ArgumentException("Некорректное имя DNS");
            i += len + 1;
        }
        throw new ArgumentException("Слишком длинное имя DNS");
    }

    private static int SkipRecord(byte[] d, int i)
    {
        i = SkipName(d, i);
        int rdLength = (d[i + 8] << 8) | d[i + 9];
        int next = i + 10 + rdLength;
        if (next > d.Length) throw new ArgumentException("Запись DNS выходит за пакет");
        return next;
    }
}
