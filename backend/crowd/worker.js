// Общая карта серверов CHIMERION на Cloudflare Workers + D1.
//
// POST /v1/report   программа присылает итоги своих проверок: сервер, сервис, работает или нет. Сеть провайдера
//                   (AS) берется из cf.asn, IP не хранится. С одного адреса не больше 5 отчетов в час.
// GET  /v1/stats    рейтинг серверов для сети провайдера того, кто спрашивает (или ?asn=), и общий.
// POST /tg/<secret> Telegram-бот: /dns — что работает сейчас, /status — сколько данных.
// POST /tg-setup    подключить бота (заголовок X-Admin-Key): адрес вебхука и команды.
// GET  /v1/discover серверы, которые у многих работают: программы добавляют их к себе и проверяют сами.
// GET  /v1/probe    открывается ли сайт снаружи России (с узла Cloudflare): «лежит у всех» или «не открывается у вас».
// GET  /v1/services состояние ChatGPT, Claude и Gemini по их страницам статуса.
// GET  /v1/ios      профиль DNS для iPhone (.mobileconfig) с DoH выбранного сервера.
// GET  /            страница со статусом серверов для людей.
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
  if (agg.size === 0) return json({ accepted: 0 });

  const stmts = [];
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
<a class="btn" href="https://github.com/under2ker/chimerion/releases/latest">Скачать</a><a class="btn" href="https://t.me/chimeriondns" style="background:transparent;color:var(--lime);border:1px solid var(--lime)">Telegram-канал</a>
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
      if (req.method === "GET" && url.pathname === "/v1/stats") return await stats(req, env, ctx);
      if (req.method === "POST" && env.TG_WEBHOOK_SECRET && url.pathname === `/tg/${env.TG_WEBHOOK_SECRET}`) return await telegram(req, env);
      if (req.method === "POST" && url.pathname === "/tg-setup") return await setup(req, env);
      if (req.method === "GET" && url.pathname === "/v1/discover") return json({ v: 1, servers: await discover(env) }, 200, { "cache-control": "public, max-age=900" });
      if (req.method === "GET" && url.pathname === "/v1/probe") return await probe(req, env);
      if (req.method === "GET" && url.pathname === "/v1/services") return await services(env, ctx);
      if (req.method === "GET" && url.pathname === "/v1/ios") return ios(req);
      if (req.method === "GET" && url.pathname === "/") return await statusPage(env, ctx);
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
