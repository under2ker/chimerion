using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>
/// Что в системе мешает DNS из CHIMERION: включенный VPN, «безопасный DNS» в браузере, прокси Windows,
/// записи в hosts и DNS на других адаптерах. Только чтение, права администратора не нужны.
/// CHIMERION работает на Windows 10 и 11 (.NET 8 не запускается на Windows 7 и 8).
/// </summary>
public static class SystemCheck
{
    /// <summary>Одна проблема: что мешает и как это отключить.</summary>
    /// <summary>Помеха. Action и ActionText: что можно открыть или исправить по кнопке в «Диагностике» (см. DiagItem).</summary>
    public sealed record Issue(string Title, string Fix, string? Action = null, string? ActionText = null);

    // Признаки VPN в имени или описании адаптера. Виртуальные сети Hyper-V и WSL (vEthernet) VPN не считаются.
    private static readonly string[] VpnWords =
    {
        "vpn", "tap-windows", "tap-win32", "wireguard", "wintun", "openvpn", "anyconnect", "fortinet", "forticlient",
        "globalprotect", "pangp", "nordlynx", "proton", "outline", "amnezia", "zerotier", "tailscale", "radmin",
        "softether", "cloudflare warp", "sing-tun", "hiddify", "v2ray", "xray", "clash", "kaspersky secure connection",
    };

    // Сети для игр и локальной сети поверх интернета: трафик в интернет через них обычно не идет, даже если у
    // адаптера есть свой шлюз или DNS (у Radmin VPN бывает). Помеха, только если через них идет маршрут в интернет
    private static readonly string[] LanOverlay = { "radmin", "hamachi", "zerotier" };

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>Номер адаптера, через который идет маршрут в интернет (к 8.8.8.8). -1 — не узнали.</summary>
    private static int InternetInterface()
    {
        try { return GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0), out var idx) == 0 ? (int)idx : -1; }
        catch (Exception) { return -1; }
    }

    // Служебные туннели Windows: всегда есть в системе и VPN не являются
    private static readonly string[] NotVpn = { "teredo", "isatap", "6to4", "ip-https", "loopback", "vethernet", "hyper-v" };

    /// <summary>
    /// Работающие VPN-адаптеры, которые могут увести DNS мимо CHIMERION: имя для показа пользователю. VPN для
    /// локальной сети поверх интернета (Radmin VPN, ZeroTier, Hamachi) без своих DNS и без шлюза запросы
    /// не перехватывает, о нем не предупреждаем: так было в журнале пользователя с Radmin VPN.
    /// </summary>
    public static List<string> ActiveVpns()
    {
        try
        {
            int inet = InternetInterface();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n =>
                {
                    var ip = n.GetIPProperties();
                    int idx = -2;
                    try { idx = ip.GetIPv4Properties()?.Index ?? -2; } catch (Exception) { }
                    return IsInterferingVpn(n.Name, n.Description, n.NetworkInterfaceType, ip.UnicastAddresses.Count,
                        ip.DnsAddresses.Count(a => !a.IsIPv6SiteLocal), ip.GatewayAddresses.Count(g => !g.Address.Equals(IPAddress.Any)),
                        internetRoute: inet < 0 || idx == inet);
                })
                .Select(n => string.IsNullOrWhiteSpace(n.Description) ? n.Name : $"{n.Name} ({n.Description})")
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException) { return new(); }
    }

    /// <summary>Похож на VPN и может перехватить DNS: у него свои DNS-серверы или шлюз (через него идет трафик).</summary>
    /// <param name="internetRoute">Через этот адаптер идет маршрут в интернет. Для сетей вроде Radmin VPN это единственный
    /// признак помехи: без маршрута они запросы к DNS не перехватывают, даже со своим шлюзом.</param>
    internal static bool IsInterferingVpn(string name, string description, NetworkInterfaceType type, int unicast, int dns, int gateways, bool internetRoute = false)
    {
        var text = (name + " " + description).ToLowerInvariant();
        if (NotVpn.Any(text.Contains)) return false;
        if (LanOverlay.Any(text.Contains)) return internetRoute;
        bool vpn = type is NetworkInterfaceType.Ppp || VpnWords.Any(text.Contains) || (type == NetworkInterfaceType.Tunnel && unicast > 0);
        return vpn && (dns > 0 || gateways > 0 || type is NetworkInterfaceType.Ppp);
    }

    /// <summary>
    /// Есть ли сеть вообще, без DNS: TCP до двух публичных адресов. Нужна сторожу: после сна, при загрузке
    /// и при смене Wi-Fi сети нет, и DNS-сервер в этом не виноват.
    /// </summary>
    public static async Task<bool> NetworkUpAsync()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        foreach (var ip in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await sock.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), 443), cts.Token);
                return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>Все найденные проблемы. dnexAddresses — адреса текущего подключения CHIMERION (их DNS на адаптерах не считаются чужими).</summary>
    public static List<Issue> Run(IReadOnlyCollection<string> dnexAddresses)
    {
        var list = new List<Issue>();
        foreach (var vpn in ActiveVpns())
            list.Add(new Issue($"Включен VPN: {vpn}",
                "VPN подставляет свои DNS, и DNS из CHIMERION не действует. Отключите VPN на время работы CHIMERION.",
                "open:ms-settings:network-vpn", "Открыть настройки VPN"));

        list.AddRange(BrowserSecureDns());
        if (SystemProxy() is { } proxy)
            list.Add(new Issue($"В Windows включен прокси: {proxy}",
                "Браузер отправляет запросы через прокси, и DNS системы не используется. Отключите: Параметры → Сеть и Интернет → Прокси.",
                "open:ms-settings:network-proxy", "Открыть настройки прокси"));
        if (HostsOverrides() is { Count: > 0 } hosts)
            list.Add(new Issue($"В файле hosts заданы адреса для: {string.Join(", ", hosts)}",
                @"Записи в hosts важнее DNS. Удалите их из C:\Windows\System32\drivers\etc\hosts (нужен редактор с правами администратора).",
                "hosts", "Открыть hosts"));
        list.AddRange(OtherAdapterDns(dnexAddresses));
        list.AddRange(Ipv6Dns(dnexAddresses));
        return list;
    }

    /// <summary>
    /// «Безопасный DNS» в браузере со своим провайдером: браузер сам ходит к DoH-серверу и не использует DNS системы.
    /// Режим «автоматически» не мешает: он включает шифрование только у серверов из своего списка.
    /// </summary>
    private static IEnumerable<Issue> BrowserSecureDns()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var chromium = new (string Name, string Dir, string Path)[]
        {
            ("Google Chrome", @"Google\Chrome\User Data", "Настройки → Конфиденциальность и безопасность → Безопасность → Использовать безопасный DNS"),
            ("Microsoft Edge", @"Microsoft\Edge\User Data", "Настройки → Конфиденциальность, поиск и службы → Безопасность → Использовать безопасный DNS"),
            ("Яндекс Браузер", @"Yandex\YandexBrowser\User Data", "Настройки → Системные → Сеть → DNS-сервер"),
            ("Brave", @"BraveSoftware\Brave-Browser\User Data", "Настройки → Конфиденциальность и безопасность → Безопасность → Использовать безопасный DNS"),
        };
        foreach (var b in chromium)
        {
            var mode = ChromiumDohMode(Path.Combine(local, b.Dir, "Local State"));
            if (mode == "secure")
                yield return new Issue($"{b.Name}: включен безопасный DNS со своим провайдером",
                    $"Браузер обходит DNS системы, и CHIMERION в нем не действует. Отключите: {b.Path}.");
        }
        if (FirefoxTrrOn())
            yield return new Issue("Firefox: включен DNS через HTTPS",
                "Браузер обходит DNS системы, и CHIMERION в нем не действует. Отключите: Настройки → Приватность и защита → DNS через HTTPS → Выкл.");
    }

    private static string? ChromiumDohMode(string localState)
    {
        try
        {
            if (!File.Exists(localState)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            return doc.RootElement.TryGetProperty("dns_over_https", out var d) && d.TryGetProperty("mode", out var m)
                ? m.GetString() : null;
        }
        catch (Exception) { return null; } // файл занят браузером или другой формат: проблему не выдумываем
    }

    /// <summary>Firefox: network.trr.mode 2 (сначала DoH) или 3 (только DoH) в любом профиле.</summary>
    private static bool FirefoxTrrOn()
    {
        try
        {
            var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profiles)) return false;
            var rx = new Regex(@"user_pref\(""network\.trr\.mode"",\s*([23])\)");
            return Directory.EnumerateDirectories(profiles)
                .Select(d => Path.Combine(d, "prefs.js"))
                .Any(f => File.Exists(f) && rx.IsMatch(File.ReadAllText(f)));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Прокси Windows (WinINet): адрес или сценарий автонастройки. Null, если выключен.</summary>
    private static string? SystemProxy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k == null) return null;
            if (k.GetValue("ProxyEnable") is int on && on == 1 && k.GetValue("ProxyServer") is string server && server.Length > 0)
                return server;
            if (k.GetValue("AutoConfigURL") is string pac && pac.Length > 0) return "сценарий " + pac;
            return null;
        }
        catch (Exception) { return null; }
    }

    private static readonly string[] AiDomains =
        // У Google только адреса самих ИИ: записи для fonts.googleapis.com или storage.googleapis.com к ИИ отношения не имеют
        { "openai.com", "chatgpt.com", "oaistatic.com", "anthropic.com", "claude.ai", "claude.com", "gemini.google.com", "generativelanguage.googleapis.com" };

    /// <summary>Домены ИИ-сервисов, для которых в hosts прописан адрес.</summary>
    private static List<string> HostsOverrides()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (!File.Exists(path)) return new();
            return File.ReadLines(path)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .SelectMany(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
                .Where(h => AiDomains.Any(d => h.Equals(d, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>
    /// DNS по IPv6 на сетевой карте или Wi-Fi, не от CHIMERION: обычно его раздает роутер. Windows отправляет туда часть
    /// запросов, и они идут мимо сервера к DNS провайдера. Служебные fec0::/10 не считаются.
    /// </summary>
    private static IEnumerable<Issue> Ipv6Dns(IReadOnlyCollection<string> dnex)
    {
        List<(string Name, List<string> Dns)> found;
        try
        {
            found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Select(n => (n.Name, n.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal)
                    .Select(a => a.ToString().Split('%')[0])
                    .Where(a => !dnex.Contains(a)).ToList()))
                .Where(x => x.Item2.Count > 0)
                .ToList();
        }
        catch (NetworkInformationException) { yield break; }

        foreach (var (name, dns) in found)
            yield return new Issue($"На адаптере «{name}» есть DNS по IPv6: {string.Join(", ", dns)}",
                "Часть запросов может идти по IPv6 мимо CHIMERION к DNS роутера или провайдера. Подключитесь заново: " +
                "у сервера без IPv6 CHIMERION на время подключения выключает IPv6 на адаптере. Если предупреждение осталось, выключите IPv6 в свойствах адаптера.");
    }

    /// <summary>
    /// Работающие не физические адаптеры со своими DNS (виртуальные сети, туннели). CHIMERION меняет DNS только
    /// на физических адаптерах, и Windows может отправлять запросы через такой адаптер в обход CHIMERION.
    /// </summary>
    private static IEnumerable<Issue> OtherAdapterDns(IReadOnlyCollection<string> dnex)
    {
        List<(string Name, List<string> Dns)> found;
        try
        {
            found = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                .Select(n => (n.Name, n.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString())
                    .Where(a => !dnex.Contains(a) && !a.StartsWith("127.")).ToList()))
                .Where(x => x.Item2.Count > 0)
                .ToList();
        }
        catch (NetworkInformationException) { yield break; }

        foreach (var (name, dns) in found)
            yield return new Issue($"Адаптер «{name}» использует свои DNS: {string.Join(", ", dns)}",
                "Если это VPN или виртуальная сеть, отключите его на время работы CHIMERION: запросы могут идти через него.");
    }
}
