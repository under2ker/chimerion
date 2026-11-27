using System.Text;
using Dnex.Services;
using Xunit;

namespace Dnex.Tests;

/// <summary>Запасной ключ подписи (аудит U-02): подпись им принимается там же, где подпись основным.</summary>
public class ReserveKeyTests
{
    // Подписано запасным ключом при его создании (27.11.2026)
    private const string Message = "CHIMERION: проверка запасного ключа";
    private const string Signature = "4IqZMq/VsL4yiRpTDIf4+6gwrS2ZlW3pcPkTzfZGwRJmEDCD1pt71WUa2oOwjwzp5n+CV01sjsUmDjprjmB4Dg==";

    [Fact]
    public void Reserve_signature_is_accepted_by_default_key_check()
    {
        var data = Encoding.UTF8.GetBytes(Message);
        Assert.True(Updater.VerifySignature(data, Signature));
        Assert.False(Updater.VerifySignature(Encoding.UTF8.GetBytes(Message + "!"), Signature));
    }

    [Fact]
    public void Reserve_key_is_not_used_for_other_keys()
    {
        // Тесты и проверки с явно заданным ключом запасной ключ не расширяет
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        Assert.False(Updater.VerifySignature(Encoding.UTF8.GetBytes(Message), Signature, pub));
        Assert.NotEqual(Updater.PublicKey, Updater.ReserveKey);
    }
}
