using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dnex.Services;

/// <summary>Хранение данных в %AppData%\CHIMERION в виде JSON.</summary>
public static class Storage
{
    // DNEX_DATA_DIR переопределяет папку данных: для тестов и для копии, которая работает с собственными файлами
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("DNEX_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DNEX");

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

    /// <summary>
    /// Файлы, которые при чтении оказались повреждены и заменены значениями по умолчанию (их копия лежит рядом
    /// с окончанием .broken). Окно сообщает о них после запуска: иначе список серверов или настройки молча
    /// пропали бы, а первое сохранение затерло бы испорченный файл.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Recovered = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static T LoadOrCreate<T>(string file, Func<T> createDefault)
    {
        var path = PathOf(file);
        if (!File.Exists(path))
        {
            var d = createDefault();
            Save(file, d);
            return d;
        }
        string text;
        try { text = ReadWithRetry(path); }
        catch (IOException)
        {
            // Файл занят (антивирус, резервное копирование) дольше секунды: это не порча. Копию .broken не делаем,
            // и вызывающий получит значения по умолчанию только на этот раз, файл на диске остается целым
            return createDefault();
        }
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options) ?? createDefault();
        }
        catch (Exception)
        {
            // Поврежденный файл (не JSON или другой формат) не теряем — откладываем копию.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Recovered.Enqueue(file);
            return createDefault();
        }
    }

    /// <summary>Чтение с повтором: файл бывает ненадолго занят антивирусом или второй записью.</summary>
    private static string ReadWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (i < 5 && File.Exists(path)) { Thread.Sleep(200); }
        }
    }

    public static void Save<T>(string file, T value) => SaveJson(file, JsonSerializer.Serialize(value, Options));

    /// <summary>
    /// Записывает уже готовый JSON: без повторной сериализации и без лишней записи на диск. Сначала во временный файл
    /// со своим именем (две записи одного файла из разных потоков не мешают друг другу), с принудительным сбросом
    /// на диск: при отключении питания остается прежний файл или новый целиком, а не пустой.
    /// </summary>
    public static void SaveJson(string file, string json)
    {
        Directory.CreateDirectory(Dir);
        var path = PathOf(file);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        // Записи одного файла внутри программы идут по очереди: замена файла, пока его заменяет соседний поток,
        // иногда не проходит и с повторами. Между процессами остаются повторы ниже
        lock (FileLocks.GetOrAdd(path, _ => new object()))
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(json);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (IOException) when (i < 5) { Thread.Sleep(100); } // файл ненадолго открыт на чтение
                catch (UnauthorizedAccessException) when (i < 5) { Thread.Sleep(100); }
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }
}
