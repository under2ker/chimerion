using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Общая карта серверов. С согласия пользователя программа раз в час отправляет итоги своих проверок: адрес
/// сервера (публичный IP или хост DoH), его имя, сервис и работает ли. Ничего о самом пользователе не уходит:
/// сеть провайдера (AS) сервер определяет сам по соединению, IP не хранит (backend/crowd/worker.js).
/// В ответ приходит рейтинг: что работает у пользователей той же сети провайдера и у всех.
/// </summary>
public static class Crowd
{
    /// <summary>Адрес сервера общей карты (Cloudflare Worker).</summary>
    public const string Endpoint = "https://chimerion-crowd.chimerion.workers.dev";
    public const string QueueFile = "crowd-queue.json", StatsFile = "crowd.json";
    public static readonly TimeSpan SendEvery = TimeSpan.FromHours(1), FetchEvery = TimeSpan.FromHours(1);
    /// <summary>Меньше стольких проверок в сети провайдера за сутки — данных мало, рекомендацию не даем.</summary>
    public const int MinChecks = 10;
    /// <summary>Ниже такой доли удачных проверок сервер не советуем, даже если он лучший из плохих.</summary>
    public const int MinRate = 60;
    /// <summary>Сколько итогов держать в очереди, пока отправка не удалась.</summary>
    public const int MaxQueue = 400;

    internal static HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    internal static Func<string> BaseUrl = () => Endpoint;

    public sealed record Item(string Server, string Service, bool Ok, string? Name);

    public sealed class Queue
    {
        public List<Item> Items { get; set; } = new();
        public DateTime? LastSent { get; set; }
    }

    public sealed record Row(string Server, string Service, int Ok, int Fail, int Ok6, int Fail6, string? Name);

    public sealed class Stats
    {
        public int Asn { get; set; }
        public string? Org { get; set; }
        public DateTime Fetched { get; set; }
        public List<Row> Provider { get; set; } = new();
        public List<Row> Global { get; set; } = new();
    }

    /// <summary>Ключ сервера, как его понимает общая карта: хост DoH, иначе основной IP.</summary>
    public static string? KeyOf(DnsServer s)
    {
        if (!string.IsNullOrWhiteSpace(s.DohUrl) && Uri.TryCreate(s.DohUrl.Split('{')[0], UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            return u.Host.ToLowerInvariant();
        return System.Net.IPAddress.TryParse(s.Primary, out var ip) ? ip.ToString() : null;
    }

    /// <summary>
    /// Итоги проверки сервера в очередь: по каждому сервису работает или нет. Желтые плашки («регион», «капча»)
    /// пропускаются: капча Google значит, что доступ есть, а по плашке их не различить.
    /// </summary>
    public static void Enqueue(Queue q, DnsServer s)
    {
        if (KeyOf(s) is not { } key) return;
        foreach (var chip in s.Chips.Where(c => c.State is "ok" or "fail"))
        {
            q.Items.RemoveAll(i => i.Server == key && i.Service == chip.Name);
            q.Items.Add(new Item(key, chip.Name, chip.State == "ok", DnsServer.Clip(s.DisplayName, 48)));
        }
        if (q.Items.Count > MaxQueue) q.Items.RemoveRange(0, q.Items.Count - MaxQueue);
    }

    public static Queue LoadQueue()
    {
        try { return Storage.LoadOrCreate(QueueFile, () => new Queue()); } catch (Exception) { return new Queue(); }
    }

    public static void SaveQueue(Queue q) { try { Storage.Save(QueueFile, q); } catch (Exception) { } }

    /// <summary>Отправить очередь, если пора. true — отправлено (очередь очищена).</summary>
    public static async Task<bool> SendAsync(Queue q, CancellationToken ct = default, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if (q.Items.Count == 0 || q.LastSent is { } last && t - last < SendEvery) return false;
        var body = JsonSerializer.Serialize(new
        {
            v = 1, app = UpdateChecker.CurrentVersion.ToString(3),
            items = q.Items.Select(i => new { server = i.Server, service = i.Service, ok = i.Ok, name = i.Name }),
        });
        using var resp = await Http.PostAsync(BaseUrl() + "/v1/report", new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        // 429: с этого адреса уже прислали (несколько компьютеров за одним IP). Не повторяем до следующего часа
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 429) return false;
        q.Items.Clear();
        q.LastSent = t;
        return true;
    }

    public static Stats? LoadStats()
    {
        try { return File.Exists(Storage.PathOf(StatsFile)) ? Storage.LoadOrCreate(StatsFile, () => new Stats()) : null; } catch (Exception) { return null; }
    }

    /// <summary>Свежий рейтинг с сервера (для сети провайдера, из которой идет запрос).</summary>
    public static async Task<Stats> FetchAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(BaseUrl() + "/v1/stats", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var st = JsonSerializer.Deserialize<Stats>(json, Storage.Options) ?? new Stats();
        st.Fetched = DateTime.Now;
        try { Storage.Save(StatsFile, st); } catch (Exception) { }
        return st;
    }

    /// <summary>Итог по серверу в списке rows: доля удачных проверок всех сервисов за сутки и их число.</summary>
    public static (int Rate, int Total)? RateOf(IEnumerable<Row> rows, DnsServer s, string? service = null)
    {
        if (KeyOf(s) is not { } key) return null;
        int ok = 0, total = 0;
        foreach (var r in rows.Where(r => r.Server == key && (service == null || r.Service == service)))
        {
            ok += r.Ok; total += r.Ok + r.Fail;
        }
        return total == 0 ? null : (ok * 100 / total, total);
    }

    /// <summary>
    /// Лучший из серверов списка у пользователей той же сети провайдера: не меньше MinChecks проверок за сутки и
    /// MinRate удачных, выше доля удачных, при равенстве больше проверок. Мусор и серверы, которые у самого пользователя сейчас не
    /// работают, не предлагаются.
    /// </summary>
    public static (DnsServer Server, int Rate, int Total)? BestForProvider(Stats? st, IEnumerable<DnsServer> servers)
    {
        if (st == null || st.Provider.Count == 0) return null;
        return servers.Where(s => !s.IsJunk && s.Status != ServerStatus.Fail)
            .Select(s => (Server: s, R: RateOf(st.Provider, s)))
            .Where(x => x.R is { Total: >= MinChecks, Rate: >= MinRate })
            .Select(x => (x.Server, x.R!.Value.Rate, x.R!.Value.Total))
            .OrderByDescending(x => x.Rate).ThenByDescending(x => x.Total)
            .Cast<(DnsServer, int, int)?>()
            .FirstOrDefault();
    }

    /// <summary>Название сети: «Rostelecom (AS12389)» или «AS12389».</summary>
    public static string NetName(Stats st) => string.IsNullOrWhiteSpace(st.Org) ? $"AS{st.Asn}" : $"{st.Org} (AS{st.Asn})";
}
