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
            // Файл занят (антивирус, резервное копирование) дольше 10 секунд: это не порча. Копию .broken не делаем,
            // вызывающий получит значения по умолчанию, а запись этого файла будет запрещена, пока его не прочтут:
            // иначе первое же сохранение затерло бы настоящие настройки значениями по умолчанию
            lock (Unread) Unread.Add(path);
            Trace?.Invoke($"Хранилище: {file} занят другой программой, прочитать не удалось. Запись в него отложена");
            return createDefault();
        }
        lock (Unread) Unread.Remove(path);
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

    /// <summary>Файлы, которые не удалось прочитать из-за блокировки: перезаписывать их нельзя (там настоящие данные).</summary>
    private static readonly HashSet<string> Unread = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Записи, которые не прошли из-за блокировки: путь → последний JSON. Фоновый повтор до PendingFor.</summary>
    private static readonly Dictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static Task? _pendingTask;

    /// <summary>Сколько ждать занятый файл при чтении и сколько повторять отложенную запись. Тесты уменьшают.</summary>
    internal static TimeSpan ReadWait = TimeSpan.FromSeconds(10), PendingFor = TimeSpan.FromMinutes(2);

    /// <summary>Сообщения о блокировках и отложенных записях для журнала (MainWindow подписывается).</summary>
    public static Action<string>? Trace;

    /// <summary>Файл, который при запуске не прочитали из-за блокировки, снова доступен: его стоит перечитать.</summary>
    public static Action<string>? Reread;

    /// <summary>Есть ли записи, которые еще ждут, пока файл освободится.</summary>
    public static bool HasPending { get { lock (Pending) return Pending.Count > 0; } }

    /// <summary>Чтение с повтором: файл бывает занят антивирусом, резервным копированием или второй записью.</summary>
    private static string ReadWithRetry(string path)
    {
        var until = DateTime.UtcNow + ReadWait;
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (DateTime.UtcNow < until && File.Exists(path)) { Thread.Sleep(200); }
        }
    }

    public static void Save<T>(string file, T value) => SaveJson(file, JsonSerializer.Serialize(value, Options));

    /// <summary>Критические данные должны быть записаны до изменения системы, без фонового повтора.</summary>
    public static void SaveRequired<T>(string file, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = PathOf(file);
        var json = JsonSerializer.Serialize(value, Options);
        lock (FileLocks.GetOrAdd(path, _ => new object()))
        {
            lock (Unread)
                if (Unread.Contains(path)) throw new IOException($"Не удалось сохранить {file}: файл не был прочитан");
            if (!TryWrite(path, json)) throw new IOException($"Не удалось сохранить {file}: файл занят или недоступен");
            lock (Pending) Pending.Remove(path);
        }
    }

    // Точка синхронизации для проверки порядка отложенной записи.
    internal static Action<string>? PendingSnapshot;

    /// <summary>
    /// Записывает уже готовый JSON: без повторной сериализации и без лишней записи на диск. Сначала во временный файл
    /// со своим именем (две записи одного файла из разных потоков не мешают друг другу), с принудительным сбросом
    /// на диск: при отключении питания остается прежний файл или новый целиком, а не пустой.
    /// </summary>
    public static void SaveJson(string file, string json)
    {
        Directory.CreateDirectory(Dir);
        var path = PathOf(file);
        lock (FileLocks.GetOrAdd(path, _ => new object()))
        {
            // Файл не прочитан из-за блокировки: сначала пробуем прочитать. Пока не прочли, запись откладывается
            bool unread; lock (Unread) unread = Unread.Contains(path);
            if (unread)
            {
                try { File.ReadAllText(path); lock (Unread) Unread.Remove(path); }
                catch (IOException) { Defer(path, json); return; }
                // Файл снова читается, а в памяти значения по умолчанию, полученные во время блокировки: эту запись
                // пропускаем, чтобы не затереть настоящие данные, и просим программу перечитать файл
                Trace?.Invoke($"Хранилище: {file} освободился. Сохранение пропущено, файл перечитан, чтобы не затереть данные");
                Reread?.Invoke(file);
                return;
            }
            if (!TryWrite(path, json)) Defer(path, json);
            else lock (Pending) Pending.Remove(path); // свежая запись прошла: отложенная старая больше не нужна
        }
    }

    /// <summary>Отложить запись: фоновый повтор раз в секунду, пока файл не освободится (не дольше PendingFor).</summary>
    private static void Defer(string path, string json)
    {
        lock (Pending)
        {
            bool first = !Pending.ContainsKey(path);
            Pending[path] = json; // остается последняя версия
            if (first) Trace?.Invoke($"Хранилище: {Path.GetFileName(path)} занят другой программой, сохраню, когда освободится");
            if (_pendingTask is { IsCompleted: false }) return;
            _pendingTask = Task.Run(FlushPendingAsync);
        }
    }

    private static async Task FlushPendingAsync()
    {
        var until = DateTime.UtcNow + PendingFor;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, PendingFor.TotalMilliseconds / 10))).ConfigureAwait(false);
            KeyValuePair<string, string>[] items;
            lock (Pending)
            {
                items = Pending.ToArray();
                if (items.Length == 0) { _pendingTask = null; return; }
            }
            foreach (var (path, json) in items)
            {
                PendingSnapshot?.Invoke(path);
                lock (FileLocks.GetOrAdd(path, _ => new object()))
                {
                    lock (Pending)
                        if (!Pending.TryGetValue(path, out var current) || current != json) continue;
                    bool unread; lock (Unread) unread = Unread.Contains(path);
                    if (unread)
                    {
                        try { File.ReadAllText(path); lock (Unread) Unread.Remove(path); }
                        catch (IOException) { continue; }
                        // Отложенная запись сделана из значений по умолчанию: не пишем, а перечитываем настоящий файл
                        lock (Pending) Pending.Remove(path);
                        Trace?.Invoke($"Хранилище: {Path.GetFileName(path)} освободился и перечитан");
                        Reread?.Invoke(Path.GetFileName(path));
                        continue;
                    }
                    if (!TryWrite(path, json)) continue;
                    lock (Pending) { if (Pending.TryGetValue(path, out var cur) && cur == json) Pending.Remove(path); }
                    Trace?.Invoke($"Хранилище: {Path.GetFileName(path)} сохранен после блокировки");
                }
            }
            if (DateTime.UtcNow > until)
            {
                lock (Pending)
                {
                    foreach (var k in Pending.Keys) Trace?.Invoke($"Хранилище: не удалось сохранить {Path.GetFileName(k)}: файл занят дольше {PendingFor.TotalMinutes:0} мин");
                    Pending.Clear();
                    _pendingTask = null;
                }
                return;
            }
        }
    }

    /// <summary>Дождаться отложенных записей (выход из программы, тесты). true — все записано.</summary>
    public static bool FlushPending(TimeSpan timeout)
    {
        Task? t; lock (Pending) t = _pendingTask;
        if (t != null) t.Wait(timeout);
        return !HasPending;
    }

    /// <summary>Одна попытка записи с короткими повторами (до секунды). false — файл занят.</summary>
    private static bool TryWrite(string path, string json)
    {
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
                try { File.Move(tmp, path, overwrite: true); return true; }
                catch (IOException) when (i < 10) { Thread.Sleep(100); } // файл ненадолго открыт на чтение
                catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(100); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
        catch (IOException) { return false; } // не удалось даже создать временный файл (диск, права)
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }
}
