// Общая карта серверов CHIMERION на Cloudflare Workers + D1.
//
// POST /v1/report   программа присылает итоги своих проверок: сервер, сервис, работает или нет. Сеть провайдера
//                   (AS) берется из cf.asn, IP не хранится. С одного адреса не больше 5 отчетов в час.
// GET  /v1/stats    рейтинг серверов для сети провайдера того, кто спрашивает (или ?asn=), и общий.
// POST /tg/<secret> Telegram-бот: /dns — что работает сейчас, /status — сколько данных.
// POST /tg-setup    подключить бота (заголовок X-Admin-Key): адрес вебхука и команды.
//
// Переменные: DB (D1), SALT, TG_BOT_TOKEN, TG_WEBHOOK_SECRET, ADMIN_KEY (секреты wrangler).

const MAX_BODY = 64 * 1024;
const MAX_ITEMS = 400;
const REPORTS_PER_IP = 5;
const KEEP_HOURS = 8 * 24;
const SERVICE_RE = /^[\p{L}\p{N} .+_-]{1,32}$/u;
const HOST_RE = /^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;
const IPV4_RE = /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/;
const IPV6_RE = /^[0-9a-f:]{2,39}$/i;

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
  for (const it of body.items.slice(0, MAX_ITEMS)) {
    const server = serverKey(it?.server);
    const service = typeof it?.service === "string" ? it.service.trim() : "";
    if (!server || !SERVICE_RE.test(service) || typeof it.ok !== "boolean") continue;
    agg.set(`${server}\u0000${service}`, { server, service, ok: it.ok });
    const name = cleanName(it.name);
    if (name) names.set(server, name);
  }
  if (agg.size === 0) return json({ accepted: 0 });

  const stmts = [];
  const up = env.DB.prepare(
    "INSERT INTO stats (hour, server, service, asn, ok, fail) VALUES (?, ?, ?, ?, ?, ?) " +
    "ON CONFLICT (hour, server, service, asn) DO UPDATE SET ok = ok + excluded.ok, fail = fail + excluded.fail");
  for (const { server, service, ok } of agg.values()) stmts.push(up.bind(hour, server, service, asn, ok ? 1 : 0, ok ? 0 : 1));
  const nm = env.DB.prepare("INSERT INTO servers (server, name, updated) VALUES (?, ?, ?) ON CONFLICT (server) DO UPDATE SET name = excluded.name, updated = excluded.updated");
  for (const [server, name] of names) stmts.push(nm.bind(server, name, hour));
  if (asn && org) stmts.push(env.DB.prepare("INSERT INTO asns (asn, org) VALUES (?, ?) ON CONFLICT (asn) DO UPDATE SET org = excluded.org").bind(asn, org));
  await env.DB.batch(stmts);
  return json({ accepted: agg.size, asn });
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
  const [provider, global] = await Promise.all([asn ? summary(env, asn) : [], summary(env, null)]);
  const resp = json({ v: 1, asn, org, updated: new Date().toISOString(), provider, global }, 200, { "cache-control": "public, max-age=300" });
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

async function tgSend(env, chatId, text) {
  await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ chat_id: chatId, text, disable_web_page_preview: true }),
  });
}

const HELP =
  "Бот CHIMERION. Показывает, какие Smart DNS сейчас открывают ChatGPT, Claude и Gemini, по проверкам программ CHIMERION.\n\n" +
  "/dns — лучшие серверы за последние 6 часов\n/status — сколько проверок пришло\n\n" +
  "Программа: github.com/under2ker/chimerion\nКанал: @chimeriondns";

async function telegram(req, env) {
  if (req.headers.get("x-telegram-bot-api-secret-token") !== env.TG_WEBHOOK_SECRET) return new Response("forbidden", { status: 403 });
  const update = await req.json().catch(() => null);
  const msg = update?.message;
  if (!msg?.text || !msg.chat) return new Response("ok");
  const cmd = msg.text.trim().split(/[\s@]/)[0].toLowerCase();
  if (cmd === "/dns") {
    const top = topServers(await summary(env, null));
    const text = top.length === 0
      ? "Пока мало данных: за последние 6 часов нет сервера хотя бы с 10 проверками. Загляните позже."
      : "Лучшие Smart DNS за последние 6 часов:\n\n" + top.map((t, i) =>
          `${i + 1}. ${t.name}${t.name !== t.server ? ` (${t.server})` : ""}\n   работает в ${t.rate}% проверок из ${t.total}: ` +
          Object.entries(t.services).map(([s, r]) => `${s} ${r}%`).join(", ")).join("\n\n") +
        "\n\nУ разных провайдеров результат может отличаться. Программа CHIMERION подбирает сервер под вашего провайдера сама.";
    await tgSend(env, msg.chat.id, text);
  } else if (cmd === "/status") {
    const h = nowHour();
    const r = await env.DB.prepare("SELECT SUM(n) AS reports FROM seen WHERE hour >= ?").bind(h - 24).first();
    const s = await env.DB.prepare("SELECT COUNT(DISTINCT server) AS servers, COUNT(DISTINCT asn) AS asns, SUM(ok + fail) AS checks FROM stats WHERE hour >= ?").bind(h - 24).first();
    await tgSend(env, msg.chat.id, `За сутки: отчетов ${r?.reports || 0}, проверок ${s?.checks || 0}, серверов ${s?.servers || 0}, сетей провайдеров ${s?.asns || 0}.`);
  } else if (cmd === "/start" || cmd === "/help") {
    await tgSend(env, msg.chat.id, HELP);
  }
  return new Response("ok");
}

async function setup(req, env) {
  if (!env.ADMIN_KEY || req.headers.get("x-admin-key") !== env.ADMIN_KEY) return new Response("forbidden", { status: 403 });
  const origin = new URL(req.url).origin;
  const api = (m, body) => fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/${m}`, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body),
  }).then((r) => r.json());
  const hook = await api("setWebhook", { url: `${origin}/tg/${env.TG_WEBHOOK_SECRET}`, secret_token: env.TG_WEBHOOK_SECRET, allowed_updates: ["message"] });
  const cmds = await api("setMyCommands", { commands: [
    { command: "dns", description: "Лучшие Smart DNS сейчас" },
    { command: "status", description: "Сколько данных за сутки" },
    { command: "help", description: "Что умеет бот" },
  ] });
  return json({ webhook: hook.ok, commands: cmds.ok });
}

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    try {
      if (req.method === "POST" && url.pathname === "/v1/report") return await report(req, env);
      if (req.method === "GET" && url.pathname === "/v1/stats") return await stats(req, env, ctx);
      if (req.method === "POST" && env.TG_WEBHOOK_SECRET && url.pathname === `/tg/${env.TG_WEBHOOK_SECRET}`) return await telegram(req, env);
      if (req.method === "POST" && url.pathname === "/tg-setup") return await setup(req, env);
      if (req.method === "GET" && url.pathname === "/") return new Response("CHIMERION crowd");
      return new Response("not found", { status: 404 });
    } catch (e) {
      return json({ error: "internal" }, 500);
    }
  },

  // Раз в сутки: старые счетчики и хэши для защиты от накрутки
  async scheduled(_evt, env) {
    const h = nowHour();
    await env.DB.batch([
      env.DB.prepare("DELETE FROM stats WHERE hour < ?").bind(h - KEEP_HOURS),
      env.DB.prepare("DELETE FROM seen WHERE hour < ?").bind(h - 48),
    ]);
  },
};
