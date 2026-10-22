using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dnex.Services;

/// <summary>Хранение данных в %AppData%\DNEX в виде JSON.</summary>
public static class Storage
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DNEX");

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private const string ProtectedPrefix = "dpapi:";

    /// <summary>Шифрует строку ключом текущего пользователя Windows (DPAPI): файл с Cookie бесполезен на другом ПК или под другим пользователем.</summary>
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(ProtectedPrefix)) return value;
        var bytes = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string value)
    {
        if (!value.StartsWith(ProtectedPrefix)) return value;
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(value[ProtectedPrefix.Length..]), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception) { return ""; } // зашифровано другим пользователем
    }

    public static string PathOf(string file) => Path.Combine(Dir, file);

    public static T LoadOrCreate<T>(string file, Func<T> createDefault)
    {
        var path = PathOf(file);
        if (!File.Exists(path))
        {
            var d = createDefault();
            Save(file, d);
            return d;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? createDefault();
        }
        catch
        {
            // Повреждённый файл не теряем — откладываем копию.
            File.Copy(path, path + ".broken", overwrite: true);
            return createDefault();
        }
    }

    public static void Save<T>(string file, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = PathOf(file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
