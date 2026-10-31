using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Dnex.Services;

/// <summary>
/// ID устройства: хэш SHA-256 от UUID материнской платы (SMBIOS). Он не меняется при переустановке программы и Windows
/// и не переходит с образом системы на другой компьютер. У плат без настоящего UUID берется идентификатор установки Windows.
/// Сами идентификаторы компьютер не покидают. Файл также подключен к закрытой утилите владельца.
/// </summary>
public static class DeviceIdentity
{
    private const string Salt = "chimerion-usage-v1";
    private const uint Rsmb = 0x52534D42;
    private static readonly Lazy<(string? Value, string Kind)> Cached = new(() =>
        ReadSmbiosUuid() is { } board ? (board, "board") : ReadMachineGuid() is { } windows ? (windows, "windows") : (null, "random"));

    /// <summary>Источник ID. Null: компьютер его не отдал.</summary>
    public static string? ReadSource() => Cached.Value.Value;

    /// <summary>Откуда взят ID: board (плата), windows (UUID платы скрыт или заводской), random (нет ни того, ни другого).</summary>
    public static string SourceKind => Cached.Value.Kind;

    public static string? HashOf(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Salt}|{source.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    private static string? ReadSmbiosUuid()
    {
        try
        {
            uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
            if (size == 0) return null;
            var raw = new byte[size];
            return GetSystemFirmwareTable(Rsmb, 0, raw, size) == 0 ? null : ParseSmbiosUuid(raw);
        }
        catch (Exception) { return null; }
    }

    /// <summary>UUID из таблицы SMBIOS (запись типа 1). Пустые и заводские значения плат не считаются: они одинаковы у многих.</summary>
    public static string? ParseSmbiosUuid(byte[] raw)
    {
        int i = 8; // заголовок RawSMBIOSData
        while (i + 4 <= raw.Length)
        {
            byte type = raw[i], len = raw[i + 1];
            if (len < 4 || type == 127) return null;
            if (type == 1)
            {
                if (len < 25 || i + 24 > raw.Length) return null;
                var uuid = raw.AsSpan(i + 8, 16);
                var hex = Convert.ToHexString(uuid).ToLowerInvariant();
                bool same = uuid.IndexOfAnyExcept(uuid[0]) < 0;
                return same || hex == "03000200040005000006000700080009" ? null : hex;
            }
            int j = i + len; // за записью строки, конец: два нуля подряд
            while (j + 1 < raw.Length && !(raw[j] == 0 && raw[j + 1] == 0)) j++;
            i = j + 2;
        }
        return null;
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception) { return null; }
    }
}
