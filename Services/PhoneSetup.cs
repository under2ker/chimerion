using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using Dnex.Models;

namespace Dnex.Services;

/// <summary>
/// Настройка телефона под тот же Smart DNS. iPhone: профиль .mobileconfig с DNS по HTTPS (ставится по QR-коду через
/// сервер общей карты или файлом). Android: «Частный DNS» понимает только DNS по TLS, туда вписывается имя сервера;
/// программа проверяет, что сервер отвечает по TLS на порту 853.
/// </summary>
public static class PhoneSetup
{
    /// <summary>Адрес DoH сервера без шаблона: https и только он, иначе null.</summary>
    public static string? Doh(DnsServer s) => Crowd.DohOf(s);

    /// <summary>Имя для «Частного DNS» Android: хост DoH (у Smart DNS DoT обычно на том же имени).</summary>
    public static string? DotHost(DnsServer s) => Doh(s) is { } d ? new Uri(d).Host : null;

    /// <summary>Отвечает ли сервер по DNS-over-TLS (порт 853, сертификат на это имя).</summary>
    internal static Func<string, CancellationToken, Task<bool>> DotWorks = DotWorksAsync;

    private static async Task<bool> DotWorksAsync(string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 853, cts.Token);
            await using var ssl = new SslStream(tcp.GetStream());
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return ssl.IsAuthenticated;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>Ссылка, по которой iPhone скачает профиль (сервер общей карты собирает его из адреса DoH и имени).</summary>
    public static string ProfileUrl(DnsServer s) =>
        $"{Crowd.BaseUrl()}/v1/ios?doh={Uri.EscapeDataString(Doh(s)!)}&name={Uri.EscapeDataString(DnsServer.Clip(s.DisplayName, 40))}";

    /// <summary>Профиль iOS/macOS: DNS по HTTPS через сервер для всего устройства. Тот же, что отдает сервер общей карты.</summary>
    public static string MobileConfig(DnsServer s)
    {
        var doh = Doh(s) ?? throw new InvalidOperationException("У сервера нет адреса DoH");
        var host = new Uri(doh).Host;
        var name = SecurityElement.Escape(DnsServer.Clip(s.DisplayName, 40));
        string X(string v) => SecurityElement.Escape(v)!;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>DNSSettings</key>
                  <dict>
                    <key>DNSProtocol</key><string>HTTPS</string>
                    <key>ServerURL</key><string>{X(doh)}</string>
                  </dict>
                  <key>PayloadDisplayName</key><string>{name}</string>
                  <key>PayloadIdentifier</key><string>com.chimerion.dns.{X(host)}</string>
                  <key>PayloadType</key><string>com.apple.dnsSettings.managed</string>
                  <key>PayloadUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                  <key>PayloadVersion</key><integer>1</integer>
                </dict>
              </array>
              <key>PayloadDescription</key><string>DNS через {name} для ChatGPT, Claude и Gemini. Создано CHIMERION.</string>
              <key>PayloadDisplayName</key><string>CHIMERION: {name}</string>
              <key>PayloadIdentifier</key><string>com.chimerion.profile.{X(host)}</string>
              <key>PayloadRemovalDisallowed</key><false/>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
              <key>PayloadVersion</key><integer>1</integer>
            </dict>
            </plist>

            """;
    }

    /// <summary>QR-код картинкой PNG (байты).</summary>
    public static byte[] Qr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return new QRCoder.PngByteQRCode(data).GetGraphic(8, new byte[] { 0x11, 0x13, 0x16 }, new byte[] { 0xFF, 0xFF, 0xFF }, true);
    }

    /// <summary>Лучший сервер для телефона: рабочий, с DoH (иначе iPhone и Android его не возьмут).</summary>
    public static DnsServer? Pick(IEnumerable<DnsServer> servers, Guid? activeId) =>
        servers.Where(s => !s.IsJunk && Doh(s) != null && s.Status is ServerStatus.Ok or ServerStatus.Partial)
               .OrderByDescending(s => s.Id == activeId)
               .ThenBy(s => s.Status == ServerStatus.Ok ? 0 : 1)
               .ThenByDescending(s => s.AllStability ?? 0)
               .FirstOrDefault();

    public static string SaveProfile(DnsServer s, string dir)
    {
        var path = Path.Combine(dir, $"chimerion-{DotHost(s)}.mobileconfig");
        File.WriteAllText(path, MobileConfig(s), new System.Text.UTF8Encoding(false));
        return path;
    }
}
