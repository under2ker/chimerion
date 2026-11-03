using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Dnex.Services;

/// <summary>Параметры локального посредника. Адрес DoH хранится зашифрованным для пользователя Windows.</summary>
internal sealed class DohRelayConfig
{
    /// <summary>
    /// Общий файл 0.1.120 и 0.1.121. Новые настройки пишутся в файл своего канала: при смене сервера новый посредник
    /// перезаписывал общий файл, и после отказа подключения прежний посредник терял настройки к следующей загрузке.
    /// </summary>
    internal const string LegacyFileName = "doh-relay.json";
    internal const string LegacyErrorFileName = "doh-relay-error.json";
    private static readonly Regex ChannelFile = new("^doh-relay-([0-9a-f]{32})\\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex ChannelErrorFile = new("^doh-relay-error-([0-9a-f]{32})\\.json$", RegexOptions.CultureInvariant);

    public string ProtectedEndpoint { get; set; } = "";
    public string ListenAddress { get; set; } = "";
    public string Channel { get; set; } = "";
    public List<string> BootstrapAddresses { get; set; } = new();

    internal static bool IsChannel(string? channel) => channel is { Length: 32 } && Guid.TryParseExact(channel, "N", out _) && channel == channel.ToLowerInvariant();

    internal static string FileNameFor(string channel) => IsChannel(channel)
        ? "doh-relay-" + channel + ".json" : throw new ArgumentException("Некорректный канал посредника", nameof(channel));

    /// <summary>Файл ошибки свой у каждого канала: сбой прежнего посредника не должен обрывать запуск нового.</summary>
    internal static string ErrorFileNameFor(string channel) => IsChannel(channel)
        ? "doh-relay-error-" + channel + ".json" : throw new ArgumentException("Некорректный канал посредника", nameof(channel));

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
        if (!IsChannel(Channel)) throw new InvalidDataException("Некорректный канал посредника");
        var plain = Storage.Unprotect(ProtectedEndpoint);
        if (!ProtectedEndpoint.StartsWith("dpapi:", StringComparison.Ordinal) || !Uri.TryCreate(plain, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidDataException("Не удалось прочитать защищенный адрес DoH");
        if (!IPAddress.TryParse(ListenAddress, out var listen) || listen.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(listen) || listen.Equals(IPAddress.Loopback))
            throw new InvalidDataException("Некорректный локальный адрес");
        var upstream = BootstrapAddresses.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).Where(ip => ip != null && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)).Cast<IPAddress>().Distinct().ToArray();
        if (upstream.Length == 0) throw new InvalidDataException("Нет адресов сервера DoH");
        return (endpoint, listen, upstream);
    }

    internal void Save() => Storage.SaveRequired(FileNameFor(Channel), this);

    /// <summary>
    /// Настройки канала. Отсутствующий файл не создается: иначе в папке данных появились бы пустые настройки,
    /// по которым задание поднимало бы посредник. Настройки из общего файла прежних сборок берутся, только если канал совпал.
    /// </summary>
    internal static DohRelayConfig? Load(string channel)
    {
        if (!IsChannel(channel)) return null;
        var own = FileNameFor(channel);
        if (File.Exists(Storage.PathOf(own)))
        {
            var config = Storage.LoadOrCreate<DohRelayConfig?>(own, () => null);
            return config is not null && string.Equals(config.Channel, channel, StringComparison.Ordinal) ? config : null;
        }
        var legacy = LoadLegacy();
        return legacy is not null && string.Equals(legacy.Channel, channel, StringComparison.Ordinal) ? legacy : null;
    }

    /// <summary>Каналы всех сохраненных посредников, включая общий файл прежних сборок.</summary>
    internal static List<string> SavedChannels()
    {
        var channels = new List<string>();
        if (Directory.Exists(Storage.Dir))
            foreach (var path in Directory.EnumerateFiles(Storage.Dir, "doh-relay-*.json"))
                if (ChannelFile.Match(Path.GetFileName(path)) is { Success: true } m) channels.Add(m.Groups[1].Value);
        if (LoadLegacy()?.Channel is { } legacy && IsChannel(legacy) && !channels.Contains(legacy)) channels.Add(legacy);
        return channels;
    }

    /// <summary>Каналы, у которых остался файл ошибки, но нет настроек.</summary>
    internal static List<string> OrphanErrorChannels(IReadOnlyCollection<string> saved)
    {
        var orphans = new List<string>();
        if (!Directory.Exists(Storage.Dir)) return orphans;
        foreach (var path in Directory.EnumerateFiles(Storage.Dir, "doh-relay-error-*.json"))
            if (ChannelErrorFile.Match(Path.GetFileName(path)) is { Success: true } m && !saved.Contains(m.Groups[1].Value))
                orphans.Add(m.Groups[1].Value);
        return orphans;
    }

    /// <summary>Удаляет настройки только этого канала. Файл ошибки удаляет вызывающий.</summary>
    internal static void Delete(string channel)
    {
        if (!IsChannel(channel)) return;
        File.Delete(Storage.PathOf(FileNameFor(channel)));
        if (LoadLegacy() is { } legacy && string.Equals(legacy.Channel, channel, StringComparison.Ordinal))
            File.Delete(Storage.PathOf(LegacyFileName));
    }

    private static DohRelayConfig? LoadLegacy() =>
        File.Exists(Storage.PathOf(LegacyFileName)) ? Storage.LoadOrCreate<DohRelayConfig?>(LegacyFileName, () => null) : null;
}
