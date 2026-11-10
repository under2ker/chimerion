// Общая карта серверов CHIMERION на Cloudflare Workers + D1.
//
// POST /v1/report   программа присылает итоги своих проверок: сервер, сервис, работает или нет. Сеть провайдера
//                   (AS) берется из cf.asn, IP не хранится. С одного адреса не больше 5 отчетов в час.
// POST /v1/ping    отметка копии программы при запуске и каждые 6 часов: ID устройства, версия, время работы, признаки подмены.
// GET  /v1/usage   сводка для владельца по ключу администратора: сколько устройств всего и активных.
// GET  /v1/stats    рейтинг серверов для сети провайдера того, кто спрашивает (или ?asn=), и общий. В поле incidents
//                   новые блокировки популярных сервисов в этой сети (из sites в /v1/report).
// POST /tg/<secret> Telegram-бот: /dns — что работает сейчас, /status — сколько данных.
// POST /v1/ticket  обращение из программы («Сообщить о проблеме»): текст, очищенный отчет, необязательный контакт. Владельцу уходит в Telegram.
// POST /v1/tickets статусы и ответы по своим обращениям (по секретным токенам, которые знает только программа).
// POST /tg-setup    подключить бота (заголовок X-Admin-Key): адрес вебхука и команды.
// GET  /v1/discover серверы, которые у многих работают: программы добавляют их к себе и проверяют сами.
// GET  /v1/probe    открывается ли сайт снаружи России (с узла Cloudflare): «лежит у всех» или «не открывается у вас».
// GET  /v1/services состояние ChatGPT, Claude и Gemini по их страницам статуса.
// GET  /v1/ios      профиль DNS для iPhone (.mobileconfig) с DoH выбранного сервера.
// GET  /            страница со статусом серверов для людей.
//
// Переменные: DB (D1), SALT, USAGE_SALT, TG_BOT_TOKEN, TG_WEBHOOK_SECRET, ADMIN_KEY, OWNER_CHAT_ID (секреты wrangler).

const MAX_BODY = 64 * 1024;
const MAX_ITEMS = 400;
const REPORTS_PER_IP = 5;
const KEEP_HOURS = 8 * 24;
const SERVICE_RE = /^[\p{L}\p{N} .+_-]{1,32}$/u;
const HOST_RE = /^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;
const IPV4_RE = /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/;
const IPV6_RE = /^[0-9a-f:]{2,39}$/i;
// Популярные сервисы, о которых программы сообщают, открываются ли они (Services/Incidents.cs, тот же список).
// Другие имена отбрасываются: свои сайты пользователей сюда попасть не могут
export const SITES = new Set(["youtube.com", "discord.com", "t.me", "telegram.org", "whatsapp.com",
  "instagram.com", "x.com", "facebook.com", "twitch.tv", "linkedin.com"]);
const MAX_SITES = 20;
// Новая блокировка: за последние RECENT_HOURS не меньше MIN_RECENT проверок и FAIL_SHARE% неудачных, а в
// какой-то из часов не открылось хотя бы у двух отчетов (один человек так блокировку не изобразит). До этого
// сервис открывался: за неделю не меньше MIN_BASE проверок и OK_SHARE% удачных. Давно закрытое (Instagram)
// блокировкой не считается: о нем и так все знают
const RECENT_HOURS = 6, MIN_RECENT = 4, FAIL_SHARE = 60, MIN_BASE = 5, OK_SHARE = 70, BASE_HOURS = 7 * 24;

const json = (data, status = 200, extra = {}) =>
  new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json; charset=utf-8", ...extra } });

const nowHour = () => Math.floor(Date.now() / 3600000);

/** Ключ сервера: IPv4, IPv6 или хост DoH (из https://host/path). Остальное отбрасывается. */
export function serverKey(raw) {
  if (typeof raw !== "string") return null;
  let s = raw.trim().toLowerCase();
  if (s.startsWith("https://")) {
    try { s = new URL(s).hostname; } catch { return null; }
  }
  if (IPV4_RE.test(s)) {
    // Частные и служебные адреса в общую карту не попадают
    const [a, b] = s.split(".").map(Number);
    if (a === 10 || a === 127 || a === 0 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31) || (a === 169 && b === 254) || a >= 224) return null;
    return s;
  }
  if (s.includes(":")) return IPV6_RE.test(s) && !s.startsWith("fe80") && !s.startsWith("fc") && !s.startsWith("fd") && s !== "::1" ? s : null;
  return HOST_RE.test(s) ? s : null;
}

/** Адрес DoH: https, хост по правилам, путь без пробелов и кавычек, не длиннее 200 символов. */
export function dohUrl(raw) {
  if (typeof raw !== "string" || raw.length > 200) return null;
  try {
    const u = new URL(raw.trim());
    if (u.protocol !== "https:" || !HOST_RE.test(u.hostname) || u.username || u.password) return null;
    if (!/^[A-Za-z0-9/_.~%+-]*$/.test(u.pathname)) return null;
    return `https://${u.hostname.toLowerCase()}${u.port ? ":" + u.port : ""}${u.pathname === "/" ? "/dns-query" : u.pathname}`;
  } catch { return null; }
}

const cleanName = (n) => typeof n === "string" ? n.replace(/[\u0000-\u001f<>]/g, "").trim().slice(0, 48) : "";

async function sha256(text) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function report(req, env) {
  const len = Number(req.headers.get("content-length") || 0);
  if (len > MAX_BODY) return json({ error: "too large" }, 413);
  const text = await req.text();
  if (text.length > MAX_BODY) return json({ error: "too large" }, 413);
  let body;
  try { body = JSON.parse(text); } catch { return json({ error: "bad json" }, 400); }
  if (!body || body.v !== 1 || !Array.isArray(body.items)) return json({ error: "bad format" }, 400);

  const hour = nowHour();
  const ip = req.headers.get("cf-connecting-ip") || "";
  const k = await sha256(`${ip}|${hour}|${env.SALT || ""}`);
  // За одним адресом бывает несколько компьютеров (общий IP у провайдера): до REPORTS_PER_IP отчетов в час
  const seen = await env.DB.prepare("INSERT INTO seen (k, hour, n) VALUES (?, ?, 1) ON CONFLICT (k) DO UPDATE SET n = n + 1 RETURNING n")
    .bind(k, hour).first();
  if ((seen?.n || 0) > REPORTS_PER_IP) return json({ error: "too many reports" }, 429);

  const asn = Number(req.cf?.asn) || 0;
  const org = cleanName(req.cf?.asOrganization || "");
  // Внутри отчета один сервер и сервис считаются один раз: повтор не добавляет веса
  const agg = new Map();
  const names = new Map();
  const urls = new Map();
  for (const it of body.items.slice(0, MAX_ITEMS)) {
    const server = serverKey(it?.server);
    const service = typeof it?.service === "string" ? it.service.trim() : "";
    if (!server || !SERVICE_RE.test(service) || typeof it.ok !== "boolean") continue;
    agg.set(`${server}\u0000${service}`, { server, service, ok: it.ok });
    const name = cleanName(it.name);
    if (name) names.set(server, name);
    const url = dohUrl(it.url);
    if (url && serverKey(url) === server) urls.set(server, url);
  }
  const sites = parseSites(body.sites);
  if (agg.size === 0 && sites.length === 0) return json({ accepted: 0 });

  const stmts = [];
  const ss = env.DB.prepare(
    "INSERT INTO site_stats (hour, site, asn, bypass, ok, fail) VALUES (?, ?, ?, ?, ?, ?) " +
    "ON CONFLICT (hour, site, asn, bypass) DO UPDATE SET ok = ok + excluded.ok, fail = fail + excluded.fail");
  if (asn) for (const { site, ok, bypass } of sites) stmts.push(ss.bind(hour, site, asn, bypass ? 1 : 0, ok ? 1 : 0, ok ? 0 : 1));
  const up = env.DB.prepare(
    "INSERT INTO stats (hour, server, service, asn, ok, fail) VALUES (?, ?, ?, ?, ?, ?) " +
    "ON CONFLICT (hour, server, service, asn) DO UPDATE SET ok = ok + excluded.ok, fail = fail + excluded.fail");
  for (const { server, service, ok } of agg.values()) stmts.push(up.bind(hour, server, service, asn, ok ? 1 : 0, ok ? 0 : 1));
  const nm = env.DB.prepare("INSERT INTO servers (server, name, updated) VALUES (?, ?, ?) ON CONFLICT (server) DO UPDATE SET name = excluded.name, updated = excluded.updated");
  for (const [server, name] of names) stmts.push(nm.bind(server, name, hour));
  const su = env.DB.prepare("UPDATE servers SET url = ? WHERE server = ?");
  for (const [server, url] of urls) stmts.push(su.bind(url, server));
  if (asn && org) stmts.push(env.DB.prepare("INSERT INTO asns (asn, org) VALUES (?, ?) ON CONFLICT (asn) DO UPDATE SET org = excluded.org").bind(asn, org));
  await env.DB.batch(stmts);
  return json({ accepted: agg.size + sites.length, asn });
}

/** Отчеты о популярных сервисах: только из SITES, по одному на сервис и состояние обхода, не больше MAX_SITES. */
export function parseSites(raw) {
  if (!Array.isArray(raw)) return [];
  const by = new Map();
  for (const it of raw.slice(0, MAX_SITES)) {
    const site = typeof it?.site === "string" ? it.site.trim().toLowerCase() : "";
    if (!SITES.has(site) || typeof it.ok !== "boolean") continue;
    const bypass = it.bypass === true;
    by.set(`${site}\u0000${bypass}`, { site, ok: it.ok, bypass });
  }
  return [...by.values()];
}

/**
 * Новые блокировки по строкам сводки (site, bypass, ok6, fail6, maxfail6, okb, failb, since): правила в RECENT_HOURS
 * и соседних константах. fail — доля неудачных за последние часы, since — час первой неудачи в этом окне.
 */
export function pickIncidents(rows) {
  const out = [];
  for (const r of rows) {
    const recent = r.ok6 + r.fail6, base = r.okb + r.failb;
    if (recent < MIN_RECENT || r.fail6 * 100 < FAIL_SHARE * recent || (r.maxfail6 || 0) < 2) continue;
    if (base < MIN_BASE || r.okb * 100 < OK_SHARE * base) continue;
    out.push({ site: r.site, bypass: !!r.bypass, fail: Math.round((100 * r.fail6) / recent), checks: recent, since: r.since });
  }
  return out.sort((a, b) => b.checks - a.checks);
}

async function incidents(env, asn) {
  const h = nowHour();
  const res = await env.DB.prepare(
    `SELECT site, bypass,
            SUM(CASE WHEN hour >= ?2 THEN ok ELSE 0 END) AS ok6, SUM(CASE WHEN hour >= ?2 THEN fail ELSE 0 END) AS fail6,
            MAX(CASE WHEN hour >= ?2 THEN fail ELSE 0 END) AS maxfail6,
            SUM(CASE WHEN hour < ?2 THEN ok ELSE 0 END) AS okb, SUM(CASE WHEN hour < ?2 THEN fail ELSE 0 END) AS failb,
            MIN(CASE WHEN hour >= ?2 AND fail > 0 THEN hour END) AS since
     FROM site_stats WHERE asn = ?3 AND hour >= ?1 GROUP BY site, bypass`).bind(h - BASE_HOURS, h - RECENT_HOURS, asn).all();
  return pickIncidents(res.results || []);
}

/** Сводка за 24 часа и за последние 6: по серверу и сервису. asn = null — по всем сетям. */
async function summary(env, asn) {
  const h = nowHour();
  const where = asn == null ? "hour >= ?1" : "hour >= ?1 AND asn = ?3";
  const q = `SELECT s.server AS server, s.service AS service, SUM(ok) AS ok, SUM(fail) AS fail,
                    SUM(CASE WHEN hour >= ?2 THEN ok ELSE 0 END) AS ok6, SUM(CASE WHEN hour >= ?2 THEN fail ELSE 0 END) AS fail6,
                    MAX(n.name) AS name
             FROM stats s LEFT JOIN servers n ON n.server = s.server
             WHERE ${where} GROUP BY s.server, s.service`;
  const st = env.DB.prepare(q);
  const res = await (asn == null ? st.bind(h - 24, h - 6) : st.bind(h - 24, h - 6, asn)).all();
  return res.results || [];
}

async function stats(req, env, ctx) {
  const url = new URL(req.url);
  const asn = Number(url.searchParams.get("asn")) || Number(req.cf?.asn) || 0;
  const cacheKey = new Request(`${url.origin}/v1/stats?asn=${asn}`);
  const cache = caches.default;
  const hit = await cache.match(cacheKey);
  if (hit) return hit;
  const org = asn ? (await env.DB.prepare("SELECT org FROM asns WHERE asn = ?").bind(asn).first())?.org || null : null;
  // Таблицы site_stats может еще не быть (сервер обновили раньше миграции): рейтинг отдаем и без блокировок
  const [provider, global, inc] = await Promise.all([asn ? summary(env, asn) : [], summary(env, null),
    asn ? incidents(env, asn).catch(() => []) : []]);
  const resp = json({ v: 1, asn, org, updated: new Date().toISOString(), provider, global, incidents: inc }, 200, { "cache-control": "public, max-age=300" });
  ctx.waitUntil(cache.put(cacheKey, resp.clone()));
  return resp;
}

/** Лучшие серверы: по каждому серверу доля удачных проверок всех сервисов за 6 часов, не меньше minChecks проверок. */
export function topServers(rows, minChecks = 10, take = 5) {
  const by = new Map();
  for (const r of rows) {
    const e = by.get(r.server) || { server: r.server, name: r.name || r.server, ok: 0, total: 0, services: {} };
    e.ok += r.ok6; e.total += r.ok6 + r.fail6;
    if (r.ok6 + r.fail6 > 0) e.services[r.service] = Math.round((100 * r.ok6) / (r.ok6 + r.fail6));
    by.set(r.server, e);
  }
  return [...by.values()].filter((e) => e.total >= minChecks)
    .map((e) => ({ ...e, rate: Math.round((100 * e.ok) / e.total) }))
    .sort((a, b) => b.rate - a.rate || b.total - a.total)
    .slice(0, take);
}

async function tgSend(env, chatId, text, markup) {
  try {
    const r = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
      method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ chat_id: chatId, text: String(text).slice(0, 4000), disable_web_page_preview: true, ...(markup ? { reply_markup: markup } : {}) }),
    });
    return await r.json().catch(() => null);
  } catch { return null; }
}

const HELP =
  "Бот CHIMERION. Показывает, какие Smart DNS сейчас открывают ChatGPT, Claude и Gemini, по проверкам программ CHIMERION.\n\n" +
  "Выберите действие кнопкой или командой: /shim (подборка серверов), /status, /id. Личный доступ: /dns.\n\n" +
  "Сайт: chimerion.dev\nИсходный код: github.com/under2ker/chimerion\nКанал: @chimeriondns";

/** Кнопки под сообщениями бота. Владельцу сверху добавляется список открытых обращений. */
export function menuKeyboard(isOwner) {
  const rows = [
    [{ text: "Лучшие DNS сейчас", callback_data: "shim" }, { text: "Статус", callback_data: "status" }],
    [{ text: "Мой ID", callback_data: "id" }, { text: "Скачать программу", url: "https://chimerion.dev" }],
    [{ text: "Канал", url: "https://t.me/chimeriondns" }, { text: "Исходный код", url: "https://github.com/under2ker/chimerion" }],
  ];
  if (isOwner) rows.unshift([{ text: "Открытые обращения", callback_data: "tickets" }]);
  return { inline_keyboard: rows };
}

async function dnsText(env) {
  const top = topServers(await summary(env, null));
  return top.length === 0
    ? "Пока мало данных: за последние 6 часов нет сервера хотя бы с 10 проверками. Загляните позже."
    : "Лучшие Smart DNS за последние 6 часов:\n\n" + top.map((t, i) =>
        `${i + 1}. ${t.name}${t.name !== t.server ? ` (${t.server})` : ""}\n   работает в ${t.rate}% проверок из ${t.total}: ` +
        Object.entries(t.services).map(([s, r]) => `${s} ${r}%`).join(", ")).join("\n\n") +
      "\n\nУ разных провайдеров результат может отличаться. Программа CHIMERION подбирает сервер под вашего провайдера сама.";
}

async function statusText(env) {
  const h = nowHour();
  const r = await env.DB.prepare("SELECT SUM(n) AS reports FROM seen WHERE hour >= ?").bind(h - 24).first();
  const s = await env.DB.prepare("SELECT COUNT(DISTINCT server) AS servers, COUNT(DISTINCT asn) AS asns, SUM(ok + fail) AS checks FROM stats WHERE hour >= ?").bind(h - 24).first();
  return `За сутки: отчетов ${r?.reports || 0}, проверок ${s?.checks || 0}, серверов ${s?.servers || 0}, сетей провайдеров ${s?.asns || 0}.`;
}

const idText = (chatId) => `Ваш id в боте: ${chatId}\nУкажите его как контакт в окне «Сообщить о проблеме», и ответ придет сюда.`;

/** Общие действия для команд и кнопок. true — обработано. */
async function botAction(env, chatId, action, isOwner) {
  const kb = menuKeyboard(isOwner);
  // "dns" остается для кнопок под старыми сообщениями; команду /dns теперь отвечает бот на VPS (личный доступ)
  if (action === "shim" || action === "dns") await tgSend(env, chatId, await dnsText(env), kb);
  else if (action === "status") await tgSend(env, chatId, await statusText(env), kb);
  else if (action === "id") await tgSend(env, chatId, idText(chatId), kb);
  else if (action === "tickets" && isOwner) await sendTicketList(env, chatId);
  else if (action === "help") await tgSend(env, chatId, HELP, kb);
  else return false;
  return true;
}

async function telegram(req, env) {
  if (req.headers.get("x-telegram-bot-api-secret-token") !== env.TG_WEBHOOK_SECRET) return new Response("forbidden", { status: 403 });
  const update = await req.json().catch(() => null);
  const owner = await ownerChat(env);

  // Нажатие кнопки под сообщением бота
  const cb = update?.callback_query;
  if (cb?.id && cb.message?.chat) {
    try {
      await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/answerCallbackQuery`, {
        method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ callback_query_id: cb.id }),
      });
    } catch {}
    const chatId = cb.message.chat.id;
    await botAction(env, chatId, String(cb.data || ""), !!owner && String(chatId) === owner);
    return new Response("ok");
  }

  const msg = update?.message;
  if (!msg?.text || !msg.chat) return new Response("ok");
  const cmd = msg.text.trim().split(/[\s@]/)[0].toLowerCase();
  if (cmd === "/owner") {
    // Один раз: владелец присылает ключ администратора, бот запоминает его чат. Сообщение с ключом удаляется
    const key = msg.text.trim().split(/\s+/)[1] || "";
    if (env.ADMIN_KEY && key === env.ADMIN_KEY) {
      await env.DB.prepare("INSERT INTO meta (k, v) VALUES ('owner', ?) ON CONFLICT (k) DO UPDATE SET v = excluded.v").bind(String(msg.chat.id)).run();
      try {
        await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/deleteMessage`, {
          method: "POST", headers: { "content-type": "application/json" },
          body: JSON.stringify({ chat_id: msg.chat.id, message_id: msg.message_id }),
        });
      } catch {}
      await tgSend(env, msg.chat.id, "Готово: этот чат теперь получает обращения из программы. Ответ на обращение: реплаем на его сообщение. Команды: /tickets, /t 12, /work 12, /fixed 12 0.1.36 текст, /close 12.", menuKeyboard(true));
    } else {
      await tgSend(env, msg.chat.id, key ? "Ключ не подошел. Скопируйте его из crowd-admin.txt целиком, без пробелов и переносов." : "После команды укажите ключ администратора: /owner ключ");
    }
    return new Response("ok");
  }
  const isOwner = !!owner && String(msg.chat.id) === owner;
  if (isOwner && await ownerMessage(env, msg)) return new Response("ok");
  if (cmd === "/start" && /^\/start(?:@\w+)?\s+r\d/i.test(msg.text.trim())) await linkTicket(env, msg);
  else if (cmd === "/start") await botAction(env, msg.chat.id, "help", isOwner);
  else await botAction(env, msg.chat.id, cmd.slice(1), isOwner);
  return new Response("ok");
}

async function setup(req, env) {
  if (!env.ADMIN_KEY || req.headers.get("x-admin-key") !== env.ADMIN_KEY) return new Response("forbidden", { status: 403 });
  const origin = new URL(req.url).origin;
  const api = (m, body) => fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/${m}`, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body),
  }).then((r) => r.json());
  // Сообщения бота принимает бот на VPS (оплата и личные доступы) и пересылает сюда то, что обслуживает Worker,
  // тем же запросом с секретом TG_WEBHOOK_SECRET. Вебхук здесь ставить нельзя: он отключил бы бота на VPS (409),
  // а меню команд задает он же. Только сообщаем, стоит ли вебхук
  const info = await api("getWebhookInfo", {});
  return json({ webhook: false, webhookError: info.result?.url ? "вебхук стоит: бот на VPS снимет его при перезапуске" : "не нужен, сообщения пересылает бот на VPS", commands: true });
}


// ---------- Обращения из программы ----------

const TICKET_TEXT_MAX = 4000;
const TICKET_REPORT_MAX = 48000;
const TICKETS_PER_HOUR = 150;
const TOKEN_RE = /^[0-9a-f]{32}$/;
const EMAIL_RE = /^[^\s@<>]{1,64}@[^\s@<>]{1,190}\.[A-Za-z]{2,24}$/;
const TG_NAME_RE = /^@?[A-Za-z][A-Za-z0-9_]{4,31}$/;
const TG_ID_RE = /^\d{5,15}$/;
const STATUS_RU = { new: "получено", work: "в работе", fixed: "исправлено", closed: "закрыто" };

/** Контакт: почта, @имя в Telegram или числовой ID (в Telegram или в боте). Null, если не похоже. */
export function cleanContact(c) {
  if (!c || typeof c.value !== "string") return null;
  const v = c.value.trim();
  if (c.kind === "email") return EMAIL_RE.test(v) ? { kind: "email", value: v } : null;
  if (c.kind === "tg") return TG_NAME_RE.test(v) ? { kind: "tg", value: "@" + v.replace(/^@/, "") } : null;
  if (c.kind === "tgid") return TG_ID_RE.test(v) ? { kind: "tgid", value: v } : null;
  return null;
}

const cleanText = (t, max) => typeof t === "string" ? t.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, "").trim().slice(0, max) : "";

/** Ключ обращения в базе. Соль постоянная (USAGE_SALT): SALT меняется при каждой выкладке, и токены перестали бы находить свои обращения. */
const ticketKey = (token, env) => sha256(`ticket|${token}|${env.USAGE_SALT || ""}`);

/** Чат владельца: секрет OWNER_CHAT_ID или то, что запомнила команда /owner. */
async function ownerChat(env) {
  if (env.OWNER_CHAT_ID) return String(env.OWNER_CHAT_ID);
  const r = await env.DB.prepare("SELECT v FROM meta WHERE k = 'owner'").first().catch(() => null);
  return r?.v || null;
}

const ago = (sec) => {
  const m = Math.max(0, Math.round((Date.now() / 1000 - sec) / 60));
  return m < 60 ? `${m} мин назад` : m < 1440 ? `${Math.round(m / 60)} ч назад` : `${Math.round(m / 1440)} дн назад`;
};

const CONTACT_RU = { email: "почта", tg: "Telegram", tgid: "ID" };

/** Сообщение владельцу о новом обращении и файл с отчетом. Ответ владельца на это сообщение уходит пользователю. */
async function notifyOwner(env, t, report) {
  const owner = await ownerChat(env);
  if (!owner || !env.TG_BOT_TOKEN) return;
  const contact = t.contact ? `${CONTACT_RU[t.contact_kind] || "контакт"}: ${t.contact}` : "контакт не указан";
  const head = `Обращение #${t.id} · версия ${t.version || "?"} · ${contact}\n\n`;
  const tail = `\n\nОтветьте на это сообщение, и ответ уйдет пользователю.\n/fixed ${t.id} версия [текст] · /work ${t.id} · /close ${t.id} · /t ${t.id}`;
  const sent = await tgSend(env, owner, head + t.text.slice(0, 2500) + tail);
  const mid = sent?.result?.message_id;
  if (!mid) return;
  await env.DB.prepare("UPDATE tickets SET owner_msg = ? WHERE id = ?").bind(mid, t.id).run();
  if (!report) return;
  const f = new FormData();
  f.append("chat_id", owner);
  f.append("reply_parameters", JSON.stringify({ message_id: mid }));
  f.append("document", new Blob([report], { type: "text/plain" }), `chimerion-ticket-${t.id}.txt`);
  try { await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendDocument`, { method: "POST", body: f }); } catch {}
}

// Тело запроса текстом. С 0.1.167 программа сжимает обращение (заголовок x-body: gzip): у части провайдеров
// большие отправки на Cloudflare замирают, а сжатый отчет в разы меньше. Свой заголовок, а не Content-Encoding,
// чтобы по дороге тело никто не распаковывал. limit ограничивает распакованный размер.
async function bodyText(req, limit) {
  if ((req.headers.get("x-body") || "").toLowerCase() !== "gzip") return await req.text();
  const reader = req.body.pipeThrough(new DecompressionStream("gzip")).getReader();
  const parts = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > limit) { await reader.cancel(); return null; }
    parts.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.length; }
  return new TextDecoder().decode(all);
}

async function createTicket(req, env) {
  if (!await allow(env, req, "ticket", 5)) return json({ error: "too many" }, 429);
  let text;
  try { text = await bodyText(req, 6 * (TICKET_REPORT_MAX + TICKET_TEXT_MAX) + 1); } catch { return json({ error: "bad gzip" }, 400); }
  if (text == null) return json({ error: "too large" }, 413);
  // Запас на экранирование (\uXXXX у старых клиентов): отчет и текст дальше обрезаются по своим пределам
  if (text.length > 6 * (TICKET_REPORT_MAX + TICKET_TEXT_MAX)) return json({ error: "too large" }, 413);
  let body;
  try { body = JSON.parse(text); } catch { return json({ error: "bad json" }, 400); }
  if (!body || body.v !== 1 || typeof body.token !== "string" || !TOKEN_RE.test(body.token)) return json({ error: "bad format" }, 400);
  const msg = cleanText(body.text, TICKET_TEXT_MAX);
  if (msg.length < 3) return json({ error: "empty" }, 400);
  const contact = body.contact == null ? null : cleanContact(body.contact);
  if (body.contact != null && !contact) return json({ error: "bad contact" }, 400);

  const key = await ticketKey(body.token, env);
  // Повторная отправка с тем же токеном (связь оборвалась после записи) не создает второго обращения
  const again = await env.DB.prepare("SELECT id, code FROM tickets WHERE key = ?").bind(key).first();
  if (again) return json({ id: again.id, code: again.code });

  const now = Math.floor(Date.now() / 1000);
  const recent = await env.DB.prepare("SELECT COUNT(*) AS n FROM tickets WHERE created > ?").bind(now - 3600).first();
  if ((recent?.n || 0) >= TICKETS_PER_HOUR) return json({ error: "busy" }, 429);

  const report = cleanText(body.report, TICKET_REPORT_MAX) || null;
  const version = typeof body.app === "string" && /^\d+\.\d+\.\d+$/.test(body.app) ? body.app : null;
  const bytes = crypto.getRandomValues(new Uint8Array(4));
  const code = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  const row = await env.DB.prepare(
    "INSERT INTO tickets (key, code, created, updated, version, text, report, contact_kind, contact) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) RETURNING id")
    .bind(key, code, now, now, version, msg, report, contact?.kind ?? null, contact?.value ?? null).first();
  try {
    await notifyOwner(env, { id: row.id, version, text: msg, contact: contact?.value, contact_kind: contact?.kind }, report);
  } catch (e) { console.log("ticket notify", String(e)); }
  return json({ id: row.id, code });
}

/** Статусы своих обращений: программа присылает токены (не больше 20), в ответе только то, что видит пользователь. */
async function ticketStatuses(req, env) {
  if (!await allow(env, req, "tickets", 120)) return json({ error: "too many" }, 429);
  const text = await req.text();
  if (text.length > 4096) return json({ error: "too large" }, 413);
  let body;
  try { body = JSON.parse(text); } catch { return json({ error: "bad json" }, 400); }
  if (!body || body.v !== 1 || !Array.isArray(body.tokens)) return json({ error: "bad format" }, 400);
  const tokens = [...new Set(body.tokens.filter((t) => typeof t === "string" && TOKEN_RE.test(t)))].slice(0, 20);
  if (tokens.length === 0) return json({ tickets: [] });
  const keys = await Promise.all(tokens.map((t) => ticketKey(t, env)));
  const res = await env.DB.prepare(
    `SELECT id, status, fix_version, reply, updated, user_chat IS NOT NULL AS tg FROM tickets WHERE key IN (${keys.map(() => "?").join(",")})`)
    .bind(...keys).all();
  return json({ tickets: (res.results || []).map((r) => ({ id: r.id, status: r.status, fixVersion: r.fix_version, reply: r.reply, updated: r.updated, tg: !!r.tg })) });
}

/** Команда владельца: /tickets, /t 12, /work 12, /fixed 12 0.1.36 текст, /close 12 текст, /reply 12 текст. */
export function parseOwnerCommand(text) {
  const m = /^\/([a-z]+)(?:@\w+)?(?:\s+(\d{1,9}))?(?:\s+([\s\S]+))?$/i.exec((text || "").trim());
  if (!m) return null;
  const cmd = m[1].toLowerCase();
  const id = m[2] ? Number(m[2]) : null;
  let rest = (m[3] || "").trim();
  if (cmd === "tickets" && !id) return { cmd: "list" };
  if (cmd === "t" && id) return { cmd: "show", id };
  if (!id || !["reply", "work", "fixed", "close"].includes(cmd)) return null;
  let version = null;
  if (cmd === "fixed") {
    const v = /^(\d+\.\d+\.\d+)(?:\s+([\s\S]*))?$/.exec(rest);
    if (v) { version = v[1]; rest = (v[2] || "").trim(); }
  }
  if (cmd === "reply" && !rest) return null;
  return { cmd, id, version, text: rest || null };
}

/** Что пользователю пишет бот, если он связал обращение со своим чатом. */
export function userNotice(t) {
  let s = `Обращение #${t.id}: ${STATUS_RU[t.status] || t.status}`;
  if (t.status === "fixed" && t.fix_version) s += ` в версии ${t.fix_version}. Обновите CHIMERION, чтобы получить исправление`;
  if (t.reply) s += `\n\n${t.reply}`;
  return s;
}

async function applyOwner(env, p) {
  const t = await env.DB.prepare("SELECT * FROM tickets WHERE id = ?").bind(p.id).first();
  if (!t) return `Обращения #${p.id} нет`;
  let status = { work: "work", fixed: "fixed", close: "closed" }[p.cmd] || t.status;
  if (p.cmd === "reply" && status === "new") status = "work";
  const reply = p.text ?? t.reply;
  const version = p.version ?? t.fix_version;
  await env.DB.prepare("UPDATE tickets SET status = ?, reply = ?, fix_version = ?, updated = ? WHERE id = ?")
    .bind(status, reply, version, Math.floor(Date.now() / 1000), t.id).run();
  let pushed = false;
  if (t.user_chat) {
    const r = await tgSend(env, t.user_chat, userNotice({ ...t, status, reply, fix_version: version }));
    pushed = !!r?.ok;
  }
  return `#${t.id}: ${STATUS_RU[status]}${version && status === "fixed" ? ` в ${version}` : ""}. ` +
    (pushed ? "Пользователю отправлено в Telegram и появится в программе." : "Появится в программе при ближайшей проверке (до часа).");
}

/** Открытые обращения (новые и в работе), до 20, с кнопками меню. */
async function sendTicketList(env, chatId) {
  const res = await env.DB.prepare("SELECT id, status, version, created, text FROM tickets WHERE status IN ('new', 'work') ORDER BY id DESC LIMIT 20").all();
  const rows = res.results || [];
  const line = (r) => `#${r.id} ${STATUS_RU[r.status]} · ${r.version || "?"} · ${ago(r.created)}\n${r.text.replace(/\s+/g, " ").slice(0, 80)}`;
  await tgSend(env, chatId, rows.length === 0 ? "Открытых обращений нет." : "Открытые обращения:\n\n" + rows.map(line).join("\n\n"), menuKeyboard(true));
}

/** Сообщения владельца: ответ на обращение или команда. true — сообщение разобрано здесь. */
async function ownerMessage(env, msg) {
  const text = msg.text.trim();
  const replyTo = msg.reply_to_message?.message_id;
  if (replyTo && !text.startsWith("/")) {
    const t = await env.DB.prepare("SELECT id FROM tickets WHERE owner_msg = ?").bind(replyTo).first();
    if (!t) return false;
    await tgSend(env, msg.chat.id, await applyOwner(env, { cmd: "reply", id: t.id, text }));
    return true;
  }
  const p = parseOwnerCommand(text);
  if (!p) return false;
  if (p.cmd === "list") {
    await sendTicketList(env, msg.chat.id);
  } else if (p.cmd === "show") {
    const t = await env.DB.prepare("SELECT * FROM tickets WHERE id = ?").bind(p.id).first();
    await tgSend(env, msg.chat.id, !t ? `Обращения #${p.id} нет`
      : `#${t.id} · ${STATUS_RU[t.status]} · версия ${t.version || "?"} · ${ago(t.created)}\n` +
        `${t.contact ? `${CONTACT_RU[t.contact_kind]}: ${t.contact}` : "контакт не указан"}${t.user_chat ? " · связан с ботом" : ""}\n\n${t.text.slice(0, 3000)}` +
        (t.reply ? `\n\nОтвет: ${t.reply}` : ""));
  } else {
    await tgSend(env, msg.chat.id, await applyOwner(env, p));
  }
  return true;
}

/** Пользователь открыл ссылку t.me/бот?start=r12-код: ответы по обращению #12 приходят и в этот чат. */
async function linkTicket(env, msg) {
  const arg = msg.text.trim().split(/\s+/)[1] || "";
  const m = /^r(\d{1,9})-([0-9a-f]{8})$/.exec(arg);
  const row = m ? await env.DB.prepare("UPDATE tickets SET user_chat = ? WHERE id = ? AND code = ? RETURNING id").bind(msg.chat.id, Number(m[1]), m[2]).first() : null;
  await tgSend(env, msg.chat.id, row
    ? `Готово. Ответ по обращению #${row.id} придет сюда, а также появится в программе CHIMERION.`
    : "Ссылка устарела или неверная. Откройте окно «Сообщить о проблеме» в программе и нажмите «Получать ответ в Telegram» еще раз.");
}

/** Тело запроса счетчика: { v: 1, id: 32 шестнадцатеричных знака, ver }. Null, если формат другой. */
async function usageBody(req) {
  const text = await req.text();
  if (text.length > 512) return null;
  let body;
  try { body = JSON.parse(text); } catch { return null; }
  return body && body.v === 1 && typeof body.id === "string" && /^[0-9a-f]{32}$/.test(body.id) ? body : null;
}

// Сутки статистики начинаются в полночь по времени владельца (UTC+5)
const today = () => Math.floor((Date.now() + 5 * 3600000) / 86400000);

/** Сколько секунд отметка считается свежей: период отметок клиента (6 ч) и час запаса. */
const LIVE_WINDOW = 7 * 3600;

const count = (v, max) => (Number.isInteger(v) && v >= 0 ? Math.min(v, max) : 0);

/**
 * Отметка копии программы за сутки. Хранятся хэш ID с постоянной солью, маска для владельца, дни, версия, время работы и признаки
 * подмены. USAGE_SALT не меняется: иначе все устройства посчитались бы заново. IP не хранится.
 * prev: прежний ID, история переносится на новый. Смена токена при том же ID: другая установка Windows или копия. moved: перенос данных.
 */
async function ping(req, env) {
  if (!await allow(env, req, "ping", 1000)) return json({ error: "too many" }, 429);
  const body = await usageBody(req);
  if (!body) return json({ error: "bad format" }, 400);
  const salted = (x) => sha256(`usage|${x}|${env.USAGE_SALT || ""}`);
  const hex = /^[0-9a-f]{32}$/;
  const id = await salted(body.id);
  const d = today();
  if (typeof body.prev === "string" && hex.test(body.prev) && body.prev !== body.id) {
    const prev = await salted(body.prev);
    const taken = await env.DB.prepare("SELECT 1 FROM devices WHERE id = ?").bind(id).first();
    await env.DB.prepare(taken ? "UPDATE devices SET replaced = replaced + 1 WHERE id = ?" : "UPDATE devices SET id = ?2, replaced = replaced + 1 WHERE id = ?1")
      .bind(taken ? id : prev, ...(taken ? [] : [id])).run();
    if (taken) await env.DB.prepare("DELETE FROM devices WHERE id = ?").bind(prev).run();
  }
  // Первая отметка устройства за сутки: для точного счета по дням
  const seen = await env.DB.prepare("SELECT last_day FROM devices WHERE id = ?").bind(id).first();
  if (!seen || seen.last_day < d)
    await env.DB.prepare("INSERT INTO device_days (day, n) VALUES (?, 1) ON CONFLICT (day) DO UPDATE SET n = n + 1").bind(d).run();
  const ver = typeof body.ver === "string" && /^\d+\.\d+\.\d+$/.test(body.ver) ? body.ver : null;
  const src = ["board", "windows", "random"].includes(body.src) ? body.src : "?";
  const token = typeof body.tok === "string" && hex.test(body.tok) ? await salted(body.tok) : "";
  const sec = count(body.sec, 864000), runs = count(body.runs, 10000);
  // seen_at: время отметки, dns: в этот момент программа была подключена к DNS. Нужны для сводки «на связи сейчас», по устройству наружу не отдаются
  const seenAt = Math.floor(Date.now() / 1000), dns = body.dns === true ? 1 : 0;
  await env.DB.prepare(`INSERT INTO devices (id, mask, first_day, last_day, days, version, source, token, moved, total_sec, last_sec, runs, seen_at, dns)
      VALUES (?1, ?2, ?3, ?3, 1, ?4, ?5, ?6, ?7, ?8, ?8, ?9, ?10, ?11)
      ON CONFLICT (id) DO UPDATE SET seen_at = excluded.seen_at, dns = excluded.dns,
        days = days + (excluded.last_day > last_day), version = excluded.version, source = excluded.source,
        token_changes = token_changes + (token <> '' AND excluded.token <> '' AND token <> excluded.token), token = excluded.token,
        moved = moved + excluded.moved, total_sec = total_sec + excluded.total_sec, last_sec = CASE WHEN excluded.last_day > last_day THEN excluded.last_sec ELSE last_sec + excluded.last_sec END, runs = runs + excluded.runs, last_day = excluded.last_day`)
    .bind(id, `${body.id.slice(0, 4)}****${body.id.slice(-4)}`, d, ver, src, token, body.moved === true ? 1 : 0, sec, runs, seenAt, dns).run();
  return json({ ok: true });
}

/**
 * Приблизительная оценка аудитории: разные адреса за сутки, запросившие рейтинг. Работает со всеми версиями программы.
 * Адрес хранится только как хэш с солью суток: сравнить человека между днями нельзя, поэтому оценка считается по одному дню.
 * Метка в кэше экономит запись в базу: программа спрашивает рейтинг каждый час.
 */
async function noteReach(req, env) {
  try {
    const ip = req.headers.get("cf-connecting-ip");
    if (!ip) return;
    const d = today();
    const k = await sha256(`reach|${ip}|${d}|${env.USAGE_SALT || ""}`);
    const mark = new Request(`https://reach.cache/${k}`);
    if (await caches.default.match(mark)) return;
    await caches.default.put(mark, new Response("1", { headers: { "cache-control": "max-age=86400" } }));
    await env.DB.prepare("INSERT OR IGNORE INTO reach (day, k) VALUES (?, ?)").bind(d, k).run();
  } catch (e) { console.log("reach", String(e)); }
}

/** Сводка для владельца: устройства всего и активные, время работы, подозрительные, версии и список устройств. */
async function usageReport(env) {
  const d = today();
  const r = await env.DB.prepare(`SELECT COUNT(*) AS total, SUM(last_day >= ?) AS day, SUM(last_day >= ?) AS week, SUM(last_day >= ?) AS month, SUM(first_day >= ?) AS fresh,
      SUM(total_sec) AS seconds, SUM(source <> 'board' OR replaced > 0 OR token_changes > 0 OR moved > 0) AS flagged FROM devices`)
    .bind(d, d - 6, d - 29, d - 6).first(); // сегодня, 7 дней, 30 дней с учетом сегодняшнего (раньше брали на день больше)
  const v = await env.DB.prepare("SELECT version, COUNT(*) AS n FROM devices WHERE last_day >= ? GROUP BY version ORDER BY n DESC LIMIT 8").bind(d - 29).all();
  // Отметка приходит раз в 6 часов, поэтому «на связи» = отметка не старше 7 часов. Точнее по этим данным не сказать.
  const nowSec = Math.floor(Date.now() / 1000);
  const live = await env.DB.prepare(`SELECT SUM(seen_at >= ?1) AS online, SUM(seen_at >= ?1 AND dns = 1) AS online_dns, SUM(seen_at >= ?1 AND dns = 0) AS online_no_dns,
      SUM(seen_at >= ?2) AS hour, SUM(seen_at >= ?3 AND dns = 1) AS dns_day, MIN(first_day) AS since, MAX(seen_at) AS last_seen FROM devices`)
    .bind(nowSec - LIVE_WINDOW, nowSec - 3600, nowSec - 86400).first();
  const reach = await env.DB.prepare("SELECT day, COUNT(*) AS n FROM reach WHERE day >= ? GROUP BY day").bind(d - 30).all();
  const ids = await env.DB.prepare("SELECT day, n FROM device_days WHERE day >= ?").bind(d - 30).all();
  const byDay = (rows) => Object.fromEntries(rows.results.map((x) => [x.day, x.n]));
  const est = byDay(reach), exact = byDay(ids);
  const last7 = [...Array(7).keys()].map((i) => est[d - 1 - i] || 0).filter((x) => x > 0);
  const estimate = { today: est[d] || 0, yesterday: est[d - 1] || 0, avg7: last7.length ? Math.round(last7.reduce((a, b) => a + b, 0) / last7.length) : 0, peak30: Math.max(0, ...Object.values(est)) };
  const daily = [...Array(14).keys()].map((i) => ({ day: d - i, ids: exact[d - i] || 0, estimate: est[d - i] || 0 }));
  const n = (x) => x || 0;
  return { estimate, daily, total: n(r?.total), day: n(r?.day), week: n(r?.week), month: n(r?.month), fresh: n(r?.fresh), seconds: n(r?.seconds), flagged: n(r?.flagged), versions: v.results, today: d,
    // Без списка устройств: по отдельным людям данные владельцу не нужны, только числа
    live: { windowSec: LIVE_WINDOW, online: n(live?.online), onlineDns: n(live?.online_dns), onlineNoDns: n(live?.online_no_dns), hour: n(live?.hour), dnsDay: n(live?.dns_day),
      since: n(live?.since), lastSeen: n(live?.last_seen), now: nowSec } };
}

/** Не больше limit запросов в час с одного адреса для дорогих проверок (probe). */
async function allow(env, req, kind, limit) {
  const hour = nowHour();
  const k = await sha256(`${kind}|${req.headers.get("cf-connecting-ip") || ""}|${hour}|${env.SALT || ""}`);
  const r = await env.DB.prepare("INSERT INTO seen (k, hour, n) VALUES (?, ?, 1) ON CONFLICT (k) DO UPDATE SET n = n + 1 RETURNING n").bind(k, hour).first();
  return (r?.n || 0) <= limit;
}

/**
 * Серверы для обнаружения: за сутки не меньше 20 проверок, не меньше 70% удачных и отчеты хотя бы из двух сетей
 * провайдеров (один человек с накрученными отчетами так сервер не протолкнет). Программа все равно проверит сама.
 */
export async function discover(env) {
  const h = nowHour();
  const res = await env.DB.prepare(
    `SELECT s.server AS server, MAX(n.name) AS name, MAX(n.url) AS url, SUM(ok) AS ok, SUM(fail) AS fail, COUNT(DISTINCT asn) AS nets
     FROM stats s LEFT JOIN servers n ON n.server = s.server
     WHERE hour >= ? GROUP BY s.server
     HAVING SUM(ok) + SUM(fail) >= 20 AND SUM(ok) * 100 >= 70 * (SUM(ok) + SUM(fail)) AND COUNT(DISTINCT asn) >= 2
     ORDER BY SUM(ok) DESC LIMIT 100`).bind(h - 24).all();
  return (res.results || []).map((r) => ({ server: r.server, name: r.name || r.server, url: r.url || null, rate: Math.round((100 * r.ok) / (r.ok + r.fail)), checks: r.ok + r.fail, nets: r.nets }));
}

/** Открывается ли сайт с узла Cloudflare. Любой HTTP-ответ, даже 403 от защиты от ботов, значит «сайт жив». */
async function probe(req, env) {
  const host = (new URL(req.url).searchParams.get("host") || "").trim().toLowerCase();
  if (!HOST_RE.test(host) || host.endsWith(".workers.dev") || host.endsWith(".local") || host.endsWith(".lan")) return json({ error: "bad host" }, 400);
  if (!(await allow(env, req, "probe", 300))) return json({ error: "too many probes" }, 429);
  const cache = caches.default;
  const key = new Request(`https://probe.cache/${host}`);
  const hit = await cache.match(key);
  if (hit) return hit;
  const t0 = Date.now();
  let out;
  try {
    const r = await fetch(`https://${host}/`, { method: "GET", redirect: "manual", signal: AbortSignal.timeout(8000), headers: { "user-agent": "Mozilla/5.0 CHIMERION-probe" } });
    out = { host, up: r.status < 500 || r.status === 503, status: r.status, ms: Date.now() - t0 };
    try { r.body?.cancel(); } catch {}
  } catch (e) {
    out = { host, up: false, status: 0, ms: Date.now() - t0, error: String(e?.name || "error") };
  }
  const resp = json(out, 200, { "cache-control": "public, max-age=120" });
  await cache.put(key, resp.clone());
  return resp;
}

/**
 * Открытая лента Telegram-канала (t.me/s/канал) для поиска серверов: из России t.me часто не открывается.
 * Только ленты каналов, ничего другого; кэш 30 минут и не больше 200 запросов в час с одного адреса,
 * чтобы адрес нельзя было использовать как открытый прокси.
 */
async function tgFeed(req, env) {
  const u = new URL(req.url);
  const ch = (u.searchParams.get("ch") || "").trim();
  const before = (u.searchParams.get("before") || "").trim();
  if (!/^[A-Za-z][A-Za-z0-9_]{4,31}$/.test(ch) || (before && !/^\d{1,10}$/.test(before))) return json({ error: "bad channel" }, 400);
  const cache = caches.default;
  const key = new Request(`https://tg.cache/${ch.toLowerCase()}/${before}`);
  const hit = await cache.match(key);
  if (hit) return hit;
  if (!(await allow(env, req, "tg", 200))) return json({ error: "too many" }, 429);
  let r;
  try {
    r = await fetch(`https://t.me/s/${ch}${before ? `?before=${before}` : ""}`, { redirect: "manual", signal: AbortSignal.timeout(10000), headers: { "user-agent": "Mozilla/5.0 CHIMERION" } });
  } catch {
    return json({ error: "telegram unavailable" }, 502);
  }
  if (r.status !== 200) return json({ error: `telegram ${r.status}` }, 502);
  const html = (await r.text()).slice(0, 2000000);
  const resp = new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "public, max-age=1800" } });
  await cache.put(key, resp.clone());
  return resp;
}

// ---------- Поиск источников на сервере ----------

const SEARCH_QUERIES = [
  "DNS для ChatGPT в России", "smart DNS для нейросетей бесплатно", "DNS для Claude из России", "DNS для Gemini без VPN",
  "\"предпочтительный DNS\" ChatGPT Claude без VPN", "\"dns-query\" ChatGPT Claude Gemini", "частный DNS для ChatGPT Android",
  "список DNS для обхода блокировки ИИ", "t.me dns chatgpt claude канал", "новый smart dns chatgpt",
];
// Сайты, где списков Smart DNS не бывает: соцсети, видеохостинги, магазины приложений, энциклопедии
const SEARCH_SKIP = /(^|\.)(youtube\.com|youtu\.be|vk\.com|facebook\.com|instagram\.com|x\.com|twitter\.com|wikipedia\.org|apps\.apple\.com|play\.google\.com|bing\.com|microsoft\.com|brave\.com|duckduckgo\.com|reddit\.com|dns-shop\.ru|smartdnsproxy\.com)$/i;

// Описание результата про то, что нужно: DNS (или DoH) рядом с нейросетями, обходом или «без VPN»
const RELEVANT = /(dns|днс|doh|dns-query)[\s\S]*(chatgpt|openai|claude|gemini|нейросет|ии-сервис|без vpn|обход|разблок)|(chatgpt|openai|claude|gemini|нейросет|без vpn)[\s\S]*(dns|днс|doh)/i;

/** Ссылка выдачи Bing бывает переходной (bing.com/ck/a?...&u=a1<base64>): достаем настоящий адрес. */
function unwrapBing(href) {
  try {
    const u = new URL(href);
    if (!u.hostname.endsWith("bing.com")) return href;
    const code = u.searchParams.get("u") || "";
    if (!code.startsWith("a1")) return null;
    let b64 = code.slice(2).replace(/-/g, "+").replace(/_/g, "/");
    while (b64.length % 4) b64 += "=";
    return atob(b64);
  } catch { return null; }
}

async function searchEngine(engine, q) {
  // Российский рынок явно: иначе Bing отвечает серверу в датацентре как американцу и путает русские запросы
  const url = engine === "bing" ? `https://www.bing.com/search?setlang=ru&setmkt=ru-RU&cc=RU&q=${encodeURIComponent(q)}` : `https://search.brave.com/search?source=web&country=ru&q=${encodeURIComponent(q)}`;
  try {
    const r = await fetch(url, { signal: AbortSignal.timeout(12000), headers: { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36", "accept-language": "ru-RU,ru;q=0.9" } });
    if (!r.ok) return { status: r.status, urls: [] };
    const html = await r.text();
    const urls = new Set();
    let raw = 0;
    const rejected = [];
    const add = (href) => { try { const u = new URL(href); if (!SEARCH_SKIP.test(u.hostname) && !u.hostname.startsWith("imgs.")) urls.add(u.href); } catch {} };
    if (engine === "bing") {
      // Результат берем, только если в его заголовке и описании речь о DNS для нейросетей или обхода, а не о магазине DNS
      for (const block of html.split('<li class="b_algo"').slice(1)) {
        raw++;
        const m = block.match(/<a[^>]*href="(https?:\/\/[^"]+)"/);
        if (!m) continue;
        const text = block.replace(/<[^>]+>/g, " ").toLowerCase();
        if (RELEVANT.test(text)) add(unwrapBing(m[1].replace(/&amp;/g, "&")) || "");
        else if (rejected.length < 3) rejected.push(text.replace(/\s+/g, " ").trim().slice(0, 160));
      }
    } else {
      for (const m of html.matchAll(/href="(https?:\/\/[^"]+)"/g)) add(m[1].replace(/&amp;/g, "&"));
    }
    return { status: 200, urls: [...urls].slice(0, 15), raw, rejected };
  } catch (e) { return { status: 0, urls: [] }; }
}

/** Ночной поиск: запросы по одному с паузой, найденные страницы хранятся неделю. */
export async function runSearch(env, reset = false) {
  const h = nowHour();
  if (reset) await env.DB.prepare("DELETE FROM found_pages").run();
  const report = { bing: 0, brave: 0, bingRaw: 0, bingRejected: [], errors: [] };
  for (const engine of ["brave"]) {
    for (const q of SEARCH_QUERIES) {
      const { status, urls, raw, rejected } = await searchEngine(engine, q);
      if (status !== 200) { report.errors.push(`${engine} ${status}`); if (status === 429) break; continue; }
      report[engine] += urls.length;
      if (engine === "bing") { report.bingRaw += raw || 0; if (report.bingRejected.length < 3) report.bingRejected.push(...(rejected || []).slice(0, 3 - report.bingRejected.length)); }
      if (urls.length) await env.DB.batch(urls.map((u) => env.DB.prepare(
        "INSERT INTO found_pages (url, engine, seen) VALUES (?, ?, ?) ON CONFLICT (url) DO UPDATE SET seen = excluded.seen").bind(u, engine, h)));
      // Brave отвечает «слишком часто» уже на втором запросе подряд: между запросами к нему 20 секунд
      await new Promise((ok) => setTimeout(ok, engine === "brave" ? 20000 : 1500));
    }
  }
  await env.DB.prepare("DELETE FROM found_pages WHERE seen < ?").bind(h - 24 * 7).run();
  return report;
}

/** Почасовой поиск: один запрос к каждому поисковику, фраза по кругу. Лимиты поисковиков так не срабатывают. */
export async function searchHour(env) {
  const h = nowHour();
  const q = SEARCH_QUERIES[h % SEARCH_QUERIES.length];
  // Bing с серверов Cloudflare отвечает не по запросу (выдача про адреса и магазины), поэтому только Brave
  for (const engine of ["brave"]) {
    const { status, urls } = await searchEngine(engine, q);
    if (status === 200 && urls.length)
      await env.DB.batch(urls.map((u) => env.DB.prepare(
        "INSERT INTO found_pages (url, engine, seen) VALUES (?, ?, ?) ON CONFLICT (url) DO UPDATE SET seen = excluded.seen").bind(u, engine, h)));
  }
  await env.DB.prepare("DELETE FROM found_pages WHERE seen < ?").bind(h - 24 * 7).run();
}

/** Страницы из ночного поиска за неделю: программа обходит их как источники. */
async function pages(env) {
  const res = await env.DB.prepare("SELECT url FROM found_pages WHERE seen >= ? ORDER BY seen DESC LIMIT 300").bind(nowHour() - 24 * 7).all();
  return json({ v: 1, pages: (res.results || []).map((r) => r.url) }, 200, { "cache-control": "public, max-age=3600" });
}

const STATUS_PAGES = {
  ChatGPT: "https://status.openai.com/api/v2/status.json",
  Claude: "https://status.claude.com/api/v2/status.json",
};

/** Состояние сервисов по страницам статуса (формат Atlassian Statuspage). indicator: none, minor, major, critical. */
async function services(env, ctx) {
  const cache = caches.default;
  const key = new Request("https://status.cache/v1");
  const hit = await cache.match(key);
  if (hit) return hit;
  const result = {};
  await Promise.all(Object.entries(STATUS_PAGES).map(async ([name, url]) => {
    try {
      const r = await fetch(url, { signal: AbortSignal.timeout(6000) });
      const d = await r.json();
      result[name] = { indicator: d?.status?.indicator || "unknown", text: d?.status?.description || "" };
    } catch { result[name] = { indicator: "unknown", text: "" }; }
  }));
  const resp = json({ v: 1, updated: new Date().toISOString(), services: result }, 200, { "cache-control": "public, max-age=120" });
  ctx.waitUntil(cache.put(key, resp.clone()));
  return resp;
}

const xml = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/** Профиль iOS/macOS: DNS по HTTPS через выбранный сервер для всего устройства. Без подписи: iPhone покажет «не проверен». */
export function mobileconfig(doh, name, uuid1, uuid2) {
  const host = new URL(doh).hostname;
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>PayloadContent</key>
  <array>
    <dict>
      <key>DNSSettings</key>
      <dict>
        <key>DNSProtocol</key><string>HTTPS</string>
        <key>ServerURL</key><string>${xml(doh)}</string>
      </dict>
      <key>PayloadDisplayName</key><string>${xml(name)}</string>
      <key>PayloadIdentifier</key><string>com.chimerion.dns.${xml(host)}</string>
      <key>PayloadType</key><string>com.apple.dnsSettings.managed</string>
      <key>PayloadUUID</key><string>${uuid1}</string>
      <key>PayloadVersion</key><integer>1</integer>
    </dict>
  </array>
  <key>PayloadDescription</key><string>DNS через ${xml(name)} для ChatGPT, Claude и Gemini. Создано CHIMERION.</string>
  <key>PayloadDisplayName</key><string>CHIMERION: ${xml(name)}</string>
  <key>PayloadIdentifier</key><string>com.chimerion.profile.${xml(host)}</string>
  <key>PayloadRemovalDisallowed</key><false/>
  <key>PayloadType</key><string>Configuration</string>
  <key>PayloadUUID</key><string>${uuid2}</string>
  <key>PayloadVersion</key><integer>1</integer>
</dict>
</plist>
`;
}

function ios(req) {
  const p = new URL(req.url).searchParams;
  const doh = dohUrl(p.get("doh") || "");
  if (!doh) return new Response("bad doh", { status: 400 });
  const name = cleanName(p.get("name") || "") || new URL(doh).hostname;
  return new Response(mobileconfig(doh, name, crypto.randomUUID().toUpperCase(), crypto.randomUUID().toUpperCase()), {
    headers: {
      "content-type": "application/x-apple-aspen-config",
      "content-disposition": `attachment; filename="chimerion-${new URL(doh).hostname}.mobileconfig"`,
    },
  });
}

/** Страница статуса для людей: лучшие серверы за 6 часов и по сервисам. */
async function statusPage(env, ctx) {
  const cache = caches.default;
  const key = new Request("https://page.cache/");
  const hit = await cache.match(key);
  if (hit) return hit;
  const rows = await summary(env, null);
  const top = topServers(rows, 10, 15);
  const h = nowHour();
  const s = await env.DB.prepare("SELECT COUNT(DISTINCT server) AS servers, COUNT(DISTINCT asn) AS asns, SUM(ok + fail) AS checks FROM stats WHERE hour >= ?").bind(h - 24).first();
  const esc = (t) => String(t).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
  const color = (r) => (r >= 90 ? "#3dd68c" : r >= 60 ? "#f5b941" : "#f2626e");
  const list = top.length === 0
    ? `<p class="muted">Пока мало данных: ни у одного сервера нет 10 проверок за последние 6 часов. Загляните позже.</p>`
    : `<table><thead><tr><th>Сервер</th><th>Работает</th><th>По сервисам</th><th>Проверок</th></tr></thead><tbody>` +
      top.map((t) => `<tr><td><b>${esc(t.name)}</b>${t.name !== t.server ? `<div class="muted mono">${esc(t.server)}</div>` : ""}</td>` +
        `<td><span class="pct" style="color:${color(t.rate)}">${t.rate}%</span></td>` +
        `<td>${Object.entries(t.services).map(([n, r]) => `<span class="chip" style="border-color:${color(r)}">${esc(n)} ${r}%</span>`).join(" ")}</td>` +
        `<td class="muted">${t.total}</td></tr>`).join("") + `</tbody></table>`;
  const html = `<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>CHIMERION: какие Smart DNS работают сейчас</title>
<meta name="description" content="Живой рейтинг Smart DNS для ChatGPT, Claude и Gemini по проверкам программ CHIMERION за последние 6 часов.">
<style>
:root{--bg:#111316;--card:#1a1d21;--text:#f3f4f1;--muted:#8f949b;--lime:#d4ff3f;--line:#2a2e33}
@media (prefers-color-scheme:light){:root{--bg:#f4f5f2;--card:#fff;--text:#16181b;--muted:#5f646b;--lime:#5c7a00;--line:#e3e5e0}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:860px;margin:0 auto;padding:32px 16px 48px}h1{font-size:28px;margin:0 0 6px;letter-spacing:-.01em}
.muted{color:var(--muted)}.mono{font-family:ui-monospace,Consolas,monospace;font-size:12px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:18px;margin-top:18px;overflow-x:auto}
table{width:100%;border-collapse:collapse}th{text-align:left;font-weight:600;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.06em;padding:0 10px 10px 0}
td{padding:10px 10px 10px 0;border-top:1px solid var(--line);vertical-align:top}.pct{font-size:18px;font-weight:700}
.chip{display:inline-block;border:1px solid;border-radius:999px;padding:1px 8px;font-size:12px;margin:0 4px 4px 0;white-space:nowrap}
.stats{display:flex;gap:24px;flex-wrap:wrap}.stats b{font-size:22px;display:block}
a.btn{display:inline-block;background:var(--lime);color:#111;font-weight:700;text-decoration:none;padding:10px 18px;border-radius:10px;margin:8px 12px 0 0}
a{color:var(--lime)}
</style></head><body><main>
<h1>Какие Smart DNS работают сейчас</h1>
<p class="muted">Живой рейтинг по проверкам программ CHIMERION за последние 6 часов: открываются ли через сервер ChatGPT, Claude и Gemini. У разных провайдеров результат может отличаться, программа подбирает сервер под вашего провайдера сама.</p>
<div class="card stats"><div><b>${s?.checks || 0}</b><span class="muted">проверок за сутки</span></div><div><b>${s?.servers || 0}</b><span class="muted">серверов</span></div><div><b>${s?.asns || 0}</b><span class="muted">сетей провайдеров</span></div></div>
<div class="card">${list}</div>
<div class="card"><b>CHIMERION</b> сам находит Smart DNS, проверяет их и подключает лучший в один клик. Бесплатно, для Windows.<br>
<a class="btn" href="https://chimerion.dev">Скачать</a><a class="btn" href="https://t.me/chimeriondns" style="background:transparent;color:var(--lime);border:1px solid var(--lime)">Telegram-канал</a>
<p class="muted" style="margin-bottom:0">Бот <a href="https://t.me/CHIMERIONDNS_BOT">@CHIMERIONDNS_BOT</a> отвечает на /dns. Обновлено ${new Date().toISOString().slice(11, 16)} UTC.</p></div>
</main></body></html>`;
  const resp = new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "public, max-age=300" } });
  ctx.waitUntil(cache.put(key, resp.clone()));
  return resp;
}

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/v1/report") return await report(req, env);
      if (req.method === "GET" && url.pathname === "/v1/stats") { ctx.waitUntil(noteReach(req, env)); return await stats(req, env, ctx); }
      if (req.method === "POST" && url.pathname === "/v1/ping") return await ping(req, env);
      if (req.method === "POST" && url.pathname === "/v1/ticket") return await createTicket(req, env);
      if (req.method === "POST" && url.pathname === "/v1/tickets") return await ticketStatuses(req, env);
      if (req.method === "GET" && url.pathname === "/v1/usage" && env.ADMIN_KEY && env.OWNER_ID && req.headers.get("x-admin-key") === env.ADMIN_KEY && req.headers.get("x-device-id") === env.OWNER_ID) return json(await usageReport(env));
      if (req.method === "POST" && env.TG_WEBHOOK_SECRET && url.pathname === `/tg/${env.TG_WEBHOOK_SECRET}`) return await telegram(req, env);
      if (req.method === "POST" && url.pathname === "/tg-setup") return await setup(req, env);
      if (req.method === "GET" && url.pathname === "/v1/discover") return json({ v: 1, servers: await discover(env) }, 200, { "cache-control": "public, max-age=900" });
      if (req.method === "GET" && url.pathname === "/v1/probe") return await probe(req, env);
      if (req.method === "GET" && url.pathname === "/v1/services") return await services(env, ctx);
      if (req.method === "GET" && url.pathname === "/v1/ios") return ios(req);
      if (req.method === "GET" && url.pathname === "/v1/tg") return await tgFeed(req, env);
      if (req.method === "GET" && url.pathname === "/v1/pages") return await pages(env);
      // Ручной запуск ночного поиска (проверка): только с ключом администратора
      if (req.method === "POST" && url.pathname === "/v1/search-run" && env.ADMIN_KEY && req.headers.get("x-admin-key") === env.ADMIN_KEY) return json(await runSearch(env, url.searchParams.get("reset") === "1"));
      if (req.method === "GET" && url.pathname === "/") return await statusPage(env, ctx);
      return new Response("not found", { status: 404 });
    } catch (e) {
      console.log("error", new URL(req.url).pathname, String(e?.stack || e)); // причина в журнале сервера (wrangler tail), не в ответе
      return json({ error: "internal" }, 500);
    }
  },

  // Раз в сутки: старые счетчики и хэши для защиты от накрутки
  async scheduled(_evt, env) {
    const h = nowHour();
    // Каждый час: один поисковый запрос. Раз в сутки (в 3 часа UTC): чистка старых счетчиков
    try { await searchHour(env); } catch (e) { console.log("search", String(e)); }
    if (h % 24 !== 3) return;
    await env.DB.batch([
      env.DB.prepare("DELETE FROM stats WHERE hour < ?").bind(h - KEEP_HOURS),
      env.DB.prepare("DELETE FROM site_stats WHERE hour < ?").bind(h - KEEP_HOURS),
      env.DB.prepare("DELETE FROM seen WHERE hour < ?").bind(h - 48),
      env.DB.prepare("DELETE FROM devices WHERE last_day < ?").bind(today() - 180),
      env.DB.prepare("DELETE FROM reach WHERE day < ?").bind(today() - 31),
      env.DB.prepare("DELETE FROM device_days WHERE day < ?").bind(today() - 180),
      // Обращения: отчет стирается через 30 дней, закрытые и исправленные живут 90, остальные 180
      env.DB.prepare("UPDATE tickets SET report = NULL WHERE report IS NOT NULL AND created < ?").bind(Math.floor(Date.now() / 1000) - 30 * 86400),
      env.DB.prepare("DELETE FROM tickets WHERE (status IN ('fixed', 'closed') AND updated < ?1) OR updated < ?2")
        .bind(Math.floor(Date.now() / 1000) - 90 * 86400, Math.floor(Date.now() / 1000) - 180 * 86400),
    ]);
  },
};
