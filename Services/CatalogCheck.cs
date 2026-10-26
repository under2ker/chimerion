using System.Net;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Что открывает Smart DNS из каталога сервисов (BypassCatalog): Figma, Adobe, Copilot, Midjourney и так далее.
/// Smart DNS подменяет только те сервисы, которые поддерживает: для остальных отдает обычные адреса, и сервис
/// так и остается закрытым. Проверка: несколько доменов набора резолвятся через сервер и через эталонный резолвер.
/// Подмена видна, когда сервер отдает тот же адрес прокси, что и для ИИ-сервисов.
/// </summary>
public static class CatalogCheck
{
    public enum Support
    {
        /// <summary>Сервер ничего не ответил по доменам набора.</summary>
        NoAnswer,
        /// <summary>Сервер отдает обычные адреса: сервис не подменяется.</summary>
        Direct,
        /// <summary>Адреса не совпали ни с обычными, ни с прокси сервера: возможно, подменяет другим прокси, возможно, это просто CDN.</summary>
        Different,
        /// <summary>Тот же прокси, что и для ИИ: сервер этот сервис подменяет.</summary>
        Proxied,
    }

    /// <summary>Сколько доменов набора проверять: первых, без масок.</summary>
    public const int DomainsPerGroup = 2;

    /// <summary>Домены набора для проверки: настоящие имена, без масок и общих адресов Google.</summary>
    public static List<string> Sample(BypassCatalog.Group g) =>
        g.Domains.Where(d => !d.Contains('*') && Uri.CheckHostName(d) == UriHostNameType.Dns && !SplitDns.SharedHosts.Contains(d))
                 .Take(DomainsPerGroup).ToList();

    /// <summary>
    /// Проверить наборы на сервере s. Адреса прокси сервера определяются по ИИ-сервисам из настроек: их сервер
    /// подменяет точно, если подключение вообще работает.
    /// </summary>
    public static async Task<Dictionary<string, Support>> RunAsync(ServerChecker checker, DnsServer s, IEnumerable<CheckTarget> targets,
                                                                   IEnumerable<BypassCatalog.Group> groups, CancellationToken ct)
    {
        var proxy = new HashSet<IPAddress>();
        foreach (var t in targets.Where(t => t.Enabled))
        {
            try
            {
                var (ips, _) = await checker.ResolveViaAsync(s, t.Host, ct).ConfigureAwait(false);
                var reference = await checker.ResolveReferenceAsync(t.Host, ct).ConfigureAwait(false);
                proxy.UnionWith(ips.Where(ip => !reference.Contains(ip)));
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }

        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, Support>();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(groups.Select(async g =>
        {
            var best = Support.NoAnswer;
            foreach (var host in Sample(g))
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { best = Max(best, await OneAsync(checker, s, host, proxy, ct).ConfigureAwait(false)); }
                finally { gate.Release(); }
                if (best == Support.Proxied) break;
            }
            result[g.Id] = best;
        })).ConfigureAwait(false);
        return new Dictionary<string, Support>(result);
    }

    private static Support Max(Support a, Support b) => (Support)Math.Max((int)a, (int)b);

    private static async Task<Support> OneAsync(ServerChecker checker, DnsServer s, string host, HashSet<IPAddress> proxy, CancellationToken ct)
    {
        List<IPAddress> ips;
        try { ips = (await checker.ResolveViaAsync(s, host, ct).ConfigureAwait(false)).Ips; }
        catch (Exception) when (!ct.IsCancellationRequested) { return Support.NoAnswer; }
        if (ips.Count == 0) return Support.NoAnswer;
        if (ips.Any(proxy.Contains)) return Support.Proxied;
        HashSet<IPAddress> reference;
        try { reference = await checker.ResolveReferenceAsync(host, ct).ConfigureAwait(false); }
        catch (Exception) when (!ct.IsCancellationRequested) { return Support.Different; }
        return ips.Any(reference.Contains) ? Support.Direct : Support.Different;
    }

    public static string Describe(Support s) => s switch
    {
        Support.Proxied => "сервер открывает",
        Support.Different => "адреса не обычные, проверьте сами",
        Support.Direct => "сервер не подменяет",
        _ => "сервер не ответил",
    };
}
