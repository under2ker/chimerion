using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dnex.Services;

/// <summary>
/// Обращения разработчику («Сообщить о проблеме»). Текст, очищенный отчет диагностики и необязательный контакт уходят на сервер
/// общей карты (backend/crowd/worker.js), владельцу приходит сообщение в Telegram. Ответ и статус программа забирает сама:
/// у каждого обращения свой секретный токен, по нему сервер отдает статус только владельцу обращения.
/// Без согласия на общую карту это работает тоже: отправка происходит только по нажатию кнопки «Отправить».
/// </summary>
public static class Support
{
    public const string StoreFile = "tickets.json";
    public const int MaxText = 4000, MinText = 3;
    public const string BotName = "CHIMERIONDNS_BOT";
    /// <summary>Как часто спрашивать статусы, пока есть открытые обращения. Без открытых обращений запросов нет вовсе.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(30);
    /// <summary>Сервер хранит обращения полгода; программа перестает спрашивать про старые раньше.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(120);

    public const string New = "new", Work = "work", Fixed = "fixed", Closed = "closed";

    public sealed class Ticket
    {
        public int Id { get; set; }
        public string Token { get; set; } = "";
        public string Code { get; set; } = "";
        public DateTime Created { get; set; }
        /// <summary>Начало текста обращения: для списка.</summary>
        public string Preview { get; set; } = "";
        public string Status { get; set; } = New;
        public string? FixVersion { get; set; }
        public string? Reply { get; set; }
        /// <summary>Метка последнего изменения на сервере (секунды Unix).</summary>
        public long Updated { get; set; }
        /// <summary>Ответ связан с чатом в боте: ответ приходит и в Telegram.</summary>
        public bool Tg { get; set; }
        /// <summary>Есть изменение, которое пользователь еще не видел.</summary>
        public bool Unread { get; set; }
        public string? Contact { get; set; }
    }

    public sealed class Store
    {
        public List<Ticket> Items { get; set; } = new();
        public DateTime? LastPoll { get; set; }
        /// <summary>Контакт, который человек указал в прошлый раз: подставляется в форму.</summary>
        public string? ContactKind { get; set; }
        public string? ContactValue { get; set; }
    }

    public sealed record Contact(string Kind, string Value)
    {
        public string Label => Kind switch { "email" => "почта", "tg" => "Telegram", _ => "ID" };
    }

    public static readonly string[] ContactKinds = { "tg", "email", "tgid" };

    public static string KindTitle(string kind) => kind switch
    {
        "email" => "Почта",
        "tgid" => "ID в Telegram или в боте",
        _ => "Telegram (@имя)",
    };

    public static string KindHint(string kind) => kind switch
    {
        "email" => "name@example.com",
        "tgid" => "число, его покажет бот командой /id",
        _ => "@имя",
    };

    private static readonly Regex EmailRe = new(@"^[^\s@<>]{1,64}@[^\s@<>]{1,190}\.[A-Za-z]{2,24}$", RegexOptions.Compiled);
    private static readonly Regex TgNameRe = new(@"^[A-Za-z][A-Za-z0-9_]{4,31}$", RegexOptions.Compiled);
    private static readonly Regex TgIdRe = new(@"^\d{5,15}$", RegexOptions.Compiled);

    /// <summary>
    /// Проверка и приведение контакта к виду, который примет сервер. Пустое поле — контакта нет (null без ошибки).
    /// Ссылка t.me/имя и @имя приводятся к «@имя».
    /// </summary>
    public static bool TryContact(string kind, string? raw, out Contact? contact, out string? error)
    {
        contact = null; error = null;
        var v = (raw ?? "").Trim();
        if (v.Length == 0) return true;
        switch (kind)
        {
            case "email":
                if (EmailRe.IsMatch(v)) { contact = new Contact("email", v); return true; }
                error = "Почта выглядит неверно. Проверьте адрес или оставьте поле пустым";
                return false;
            case "tgid":
                if (TgIdRe.IsMatch(v)) { contact = new Contact("tgid", v); return true; }
                error = "ID это число из 5 и более цифр. Бот CHIMERION покажет его по команде /id";
                return false;
            default:
                v = Regex.Replace(v, @"^(?:https?://)?(?:t\.me/|telegram\.me/)", "", RegexOptions.IgnoreCase).TrimStart('@');
                if (TgNameRe.IsMatch(v)) { contact = new Contact("tg", "@" + v); return true; }
                error = "Имя в Telegram: от 5 символов, латиница, цифры и подчеркивание, например @name_123";
                return false;
        }
    }

    public static string StatusText(string status) => status switch
    {
        Work => "В работе",
        Fixed => "Исправлено",
        Closed => "Закрыто",
        _ => "Получено",
    };

    /// <summary>Строка статуса для списка и уведомлений: «Исправлено в версии 0.1.36».</summary>
    public static string StatusLine(Ticket t) =>
        t.Status == Fixed && !string.IsNullOrEmpty(t.FixVersion) ? $"Исправлено в версии {t.FixVersion}" : StatusText(t.Status);

    /// <summary>Исправление вышло в версии новее той, что стоит у пользователя.</summary>
    public static bool NeedsUpdate(Ticket t) =>
        t.Status == Fixed && Version.TryParse(t.FixVersion, out var v) && v > UpdateChecker.CurrentVersion;

    public static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>Ссылка, по которой бот свяжет обращение с чатом человека: ответ придет и в Telegram.</summary>
    public static string BotLink(Ticket t) => $"https://t.me/{BotName}?start=r{t.Id}-{t.Code}";

    /// <summary>Хранилище, общее для окон и фоновой проверки: одна копия в памяти, чтобы изменения не расходились.</summary>
    public static Store Current { get; } = Load();

    public static Store Load()
    {
        try { return Storage.LoadOrCreate(StoreFile, () => new Store()); } catch (Exception) { return new Store(); }
    }

    public static void Save(Store s) { try { Storage.Save(StoreFile, s); } catch (Exception) { } }

    public static int UnreadCount(Store s) => s.Items.Count(t => t.Unread);

    /// <summary>Есть что спрашивать у сервера: открытые обращения не старше срока хранения.</summary>
    public static bool HasOpen(Store s, DateTime now) => s.Items.Any(t => t.Status != Closed && now - t.Created < Keep);

    /// <summary>Причина, по которой отправка не удалась, человеческими словами. Reason уже можно показывать.</summary>
    public sealed class SendException : Exception
    {
        public SendException(string reason) : base(reason) { }
    }

    /// <summary>Сколько знаков отчета принимает сервер. Длиннее отчет урезается посередине: начало (состояние) и конец (свежий журнал) важнее.</summary>
    public const int MaxReport = 48000;

    public static string? ClipReport(string? report)
    {
        if (report == null || report.Length <= MaxReport) return report;
        const string cut = "\n\n[... отчет сокращен ...]\n\n";
        return report[..20000] + cut + report[^(MaxReport - 20000 - cut.Length)..];
    }

    /// <summary>Тело JSON, сжатое gzip, с заголовком x-body: gzip (свой заголовок: Content-Encoding по дороге могут распаковать).</summary>
    internal static ByteArrayContent Gzip(string json)
    {
        using var ms = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(json));
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.Add("x-body", "gzip");
        return content;
    }

    /// <summary>
    /// Отправить обращение. token создается один раз на форму: повторная отправка после обрыва не создаст второе обращение
    /// (сервер узнает токен). Обращение попадает в хранилище и сохраняется.
    /// </summary>
    public static async Task<Ticket> SendAsync(Store store, string token, string text, Contact? contact, string? report,
                                               CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length < MinText) throw new SendException("Опишите проблему хотя бы парой слов");
        if (text.Length > MaxText) text = text[..MaxText];
        // Без экранирования кириллицы: иначе каждая буква занимает шесть знаков (\uXXXX) и отчет упирается в лимит сервера
        var body = JsonSerializer.Serialize(new
        {
            v = 1, token, app = UpdateChecker.CurrentVersion.ToString(3), text, report = ClipReport(report),
            contact = contact == null ? null : new { kind = contact.Kind, value = contact.Value },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        HttpResponseMessage resp;
        try
        {
            // Сжатым: у части провайдеров большая отправка на Cloudflare замирает на середине, и программа писала
            // «Сервер не отвечает». Сжатый отчет в 10-20 раз меньше. Сервер без поддержки сжатия ответит «bad json»,
            // тогда повторяем как раньше, без сжатия.
            resp = await Crowd.Http.PostAsync(Crowd.BaseUrl() + "/v1/ticket", Gzip(body), ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest
                && (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Contains("bad json"))
            {
                resp.Dispose();
                resp = await Crowd.Http.PostAsync(Crowd.BaseUrl() + "/v1/ticket", new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
        {
            throw new SendException("Сервер не отвечает. Проверьте интернет и попробуйте еще раз: текст сохранится");
        }
        using (resp)
        {
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new SendException((int)resp.StatusCode switch
                {
                    429 => "Слишком много обращений за час. Попробуйте позже",
                    413 => "Обращение слишком большое. Уберите отчет диагностики или сократите текст",
                    400 when json.Contains("bad contact") => "Сервер не принял контакт. Проверьте его или оставьте поле пустым",
                    _ => "Сервер не принял обращение. Попробуйте позже",
                });
            }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                throw new SendException("Сервер ответил непонятно. Попробуйте позже");
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var ticket = store.Items.FirstOrDefault(t => t.Id == id) ?? new Ticket { Id = id };
            ticket.Token = token;
            ticket.Code = code;
            ticket.Created = ticket.Created == default ? DateTime.Now : ticket.Created;
            ticket.Preview = Regex.Replace(text, @"\s+", " ").Trim() is { Length: > 90 } p ? p[..90] + "…" : Regex.Replace(text, @"\s+", " ").Trim();
            ticket.Contact = contact?.Value;
            if (!store.Items.Contains(ticket)) store.Items.Insert(0, ticket);
            if (contact != null) { store.ContactKind = contact.Kind; store.ContactValue = contact.Value; }
            Save(store);
            return ticket;
        }
    }

    /// <summary>
    /// Спросить статусы открытых обращений (одним запросом, до 20). Возвращает обращения, где что-то изменилось: статус,
    /// ответ или версия исправления. У них Unread = true. Сеть недоступна — пусто, в следующий раз.
    /// </summary>
    public static async Task<List<Ticket>> PollAsync(Store store, DateTime? now = null, CancellationToken ct = default)
    {
        var t0 = now ?? DateTime.Now;
        var open = store.Items.Where(t => t.Status != Closed && t.Token.Length > 0 && t0 - t.Created < Keep).Take(20).ToList();
        var changed = new List<Ticket>();
        if (open.Count == 0) return changed;
        var body = JsonSerializer.Serialize(new { v = 1, tokens = open.Select(t => t.Token) });
        try
        {
            using var resp = await Crowd.Http.PostAsync(Crowd.BaseUrl() + "/v1/tickets", new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return changed;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("tickets", out var arr) || arr.ValueKind != JsonValueKind.Array) return changed;
            foreach (var e in arr.EnumerateArray())
            {
                if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                if (open.FirstOrDefault(t => t.Id == id) is not { } t) continue;
                string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                long upd = e.TryGetProperty("updated", out var u) && u.TryGetInt64(out var l) ? l : 0;
                var status = Str("status") is Work or Fixed or Closed or New ? Str("status")! : New;
                var reply = Str("reply");
                bool tg = e.TryGetProperty("tg", out var g) && g.ValueKind == JsonValueKind.True;
                bool differs = status != t.Status || reply != t.Reply || Str("fixVersion") != t.FixVersion;
                t.Tg = tg;
                if (!differs && upd == t.Updated) continue;
                t.Updated = upd;
                if (!differs) continue;
                t.Status = status; t.Reply = reply; t.FixVersion = Str("fixVersion");
                t.Unread = true;
                changed.Add(t);
            }
            store.LastPoll = t0;
            Save(store);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* сервер недоступен: спросим в следующий раз */ }
        return changed;
    }

    /// <summary>Все изменения просмотрены.</summary>
    public static void MarkSeen(Store store)
    {
        if (store.Items.All(t => !t.Unread)) return;
        foreach (var t in store.Items) t.Unread = false;
        Save(store);
    }

    /// <summary>Уведомление об изменении: короткий текст для всплывающей подсказки и трея.</summary>
    public static string NoticeText(Ticket t)
    {
        var s = $"Обращение #{t.Id}: {StatusLine(t)}";
        if (NeedsUpdate(t)) s += ". Обновите программу";
        else if (!string.IsNullOrWhiteSpace(t.Reply)) s += ". Есть ответ";
        return s;
    }
}
