using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Dnex.Services;

/// <summary>Параметры локального посредника. Адрес DoH хранится зашифрованным для пользователя Windows.</summary>
internal sealed class DohRelayConfig
{
    internal const string FileName = "doh-relay.json";
    public string ProtectedEndpoint { get; set; } = "";
    public string ListenAddress { get; set; } = "";
    public string Channel { get; set; } = "";
    public List<string> BootstrapAddresses { get; set; } = new();

    internal static DohRelayConfig Create(Uri endpoint, IPAddress listenAddress, IEnumerable<IPAddress> upstream)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Некорректный адрес DoH", nameof(endpoint));
        if (listenAddress.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(listenAddress) || listenAddress.Equals(IPAddress.Loopback))
            throw new ArgumentException("Некорректный локальный адрес", nameof(listenAddress));
        var addresses = upstream.Where(ip => !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
            .Distinct().Select(ip => ip.ToString()).ToList();
        if (addresses.Count == 0) throw new ArgumentException("Нет адресов сервера DoH", nameof(upstream));
        return new DohRelayConfig { ProtectedEndpoint = Storage.Protect(endpoint.AbsoluteUri), ListenAddress = listenAddress.ToString(), BootstrapAddresses = addresses, Channel = Guid.NewGuid().ToString("N") };
    }

    internal (Uri Endpoint, IPAddress Listen, IPAddress[] Upstream) Decode()
    {
        if (!Guid.TryParseExact(Channel, "N", out _)) throw new InvalidDataException("Некорректный канал посредника");
        var plain = Storage.Unprotect(ProtectedEndpoint);
        if (!ProtectedEndpoint.StartsWith("dpapi:", StringComparison.Ordinal) || !Uri.TryCreate(plain, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidDataException("Не удалось прочитать защищенный адрес DoH");
        if (!IPAddress.TryParse(ListenAddress, out var listen) || listen.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(listen) || listen.Equals(IPAddress.Loopback))
            throw new InvalidDataException("Некорректный локальный адрес");
        var upstream = BootstrapAddresses.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).Where(ip => ip != null && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)).Cast<IPAddress>().Distinct().ToArray();
        if (upstream.Length == 0) throw new InvalidDataException("Нет адресов сервера DoH");
        return (endpoint, listen, upstream);
    }

    internal void Save() => Storage.SaveRequired(FileName, this);
    internal static DohRelayConfig? Load() => Storage.LoadOrCreate<DohRelayConfig?>(FileName, () => null);
}
