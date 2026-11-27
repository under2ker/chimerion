// Обнаружение серверов в общей карте на выдуманных данных: локальная SQLite вместо D1, без сети.
// Запуск: node backend/crowd/test-discover.mjs
import { DatabaseSync } from "node:sqlite";
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import worker, { subnetOf, limitKeyOf } from "./worker.js";

const here = dirname(fileURLToPath(import.meta.url));
let failures = 0;
const check = (cond, what) => { console.log((cond ? "ok   " : "FAIL ") + what); if (!cond) failures++; };

function makeDb(withReporters = true) {
  const db = new DatabaseSync(":memory:");
  const files = ["schema.sql", ...readdirSync(here).filter((f) => /^migrate-\d+\.sql$/.test(f)).sort()];
  for (const f of files) {
    for (const st of readFileSync(join(here, f), "utf8").replace(/--.*$/gm, "").split(";").map((s) => s.trim()).filter(Boolean)) {
      if (!withReporters && /reporters/.test(st)) continue;
      try { db.exec(st); } catch { /* колонка уже есть в schema.sql */ }
    }
  }
  const stmt = (sql, args = []) => ({
    bind: (...a) => stmt(sql, a),
    all: async () => ({ results: db.prepare(sql).all(...args) }),
    first: async () => db.prepare(sql).get(...args) ?? null,
    run: async () => db.prepare(sql).run(...args),
  });
  return {
    sqlite: db,
    prepare: (sql) => stmt(sql),
    batch: async (list) => {
      db.exec("BEGIN");
      try { const out = []; for (const s of list) out.push(await s.run()); db.exec("COMMIT"); return out; }
      catch (e) { db.exec("ROLLBACK"); throw e; }
    },
  };
}

const ctx = { waitUntil() {} };

async function send(env, ip, asn, items) {
  const req = new Request("https://crowd.test/v1/report", {
    method: "POST", headers: { "cf-connecting-ip": ip, "content-type": "application/json" },
    body: JSON.stringify({ v: 1, app: "0.1.180", items }),
  });
  req.cf = { asn, asOrganization: "AS" + asn };
  return (await worker.fetch(req, env, ctx)).status;
}

async function discovered(env) {
  const r = await worker.fetch(new Request("https://crowd.test/v1/discover"), env, ctx);
  return (await r.json()).servers.filter((s) => !s.verified).map((s) => s.server);
}

const ok3 = (server) => ["ChatGPT", "Claude", "Gemini"].map((service) => ({ server, service, ok: true, name: "Тест" }));

// Подсети
check(subnetOf("203.0.113.7") === "203.0.113", "IPv4: подсеть /24");
check(subnetOf("2001:db8:1:2::5") === "2001:db8:1", "IPv6: подсеть /48");
check(subnetOf("2001:db8::1") === "2001:db8:0", "IPv6 с :: в начале");
check(subnetOf("") === "", "пустой адрес: без подсети");

// 1. Опыт из аудита: один человек, два адреса в разных сетях, 400 выдуманных сервисов
{
  const env = { DB: makeDb(), SALT: "s" };
  const fake = Array.from({ length: 400 }, (_, i) => ({ server: "198.51.100.66", service: "svc" + i, ok: true }));
  check(await send(env, "203.0.113.10", 64500, fake) === 200, "отчет с выдуманными сервисами принят (как раньше)");
  await send(env, "198.18.0.10", 64501, fake);
  check(!(await discovered(env)).includes("198.51.100.66"), "выдуманные сервисы с двух адресов: в обнаружение не попал");
}

// 2. Тот же человек честно шлет нейросети, но с двух подсетей: мало
{
  const env = { DB: makeDb(), SALT: "s" };
  for (let i = 0; i < 4; i++) {
    await send(env, "203.0.113." + (10 + i), 64500, ok3("198.51.100.66"));
    await send(env, "198.18.0." + (10 + i), 64501, ok3("198.51.100.66"));
  }
  check(!(await discovered(env)).includes("198.51.100.66"), "две подсети, 24 проверки: не попал (нужно 3 подсети)");
  // третья подсеть
  await send(env, "192.0.2.5", 64502, ok3("198.51.100.66"));
  check((await discovered(env)).includes("198.51.100.66"), "третья подсеть: попал");
}

// 3. Обычный хороший сервер от многих людей из разных сетей: попадает, как раньше
{
  const env = { DB: makeDb(), SALT: "s" };
  for (let i = 0; i < 8; i++) await send(env, `100.${64 + i}.${i}.1`, 64500 + (i % 3), ok3("dns.example.net"));
  const found = await discovered(env);
  check(found.includes("dns.example.net"), "сервер у 8 людей из 3 сетей: в обнаружении");
  const row = env.DB.sqlite.prepare("SELECT COUNT(*) AS n FROM reporters").get();
  check(row.n === 8, "хранится по одному хэшу на подсеть");
  const raw = JSON.stringify(env.DB.sqlite.prepare("SELECT * FROM reporters").all());
  check(!/100\.6\d/.test(raw), "адресов отправителей в базе нет");
}

// 4. Неудачные проверки и правило 70% работают как раньше
{
  const env = { DB: makeDb(), SALT: "s" };
  for (let i = 0; i < 8; i++)
    await send(env, `100.${64 + i}.${i}.1`, 64500 + (i % 3), ["ChatGPT", "Claude", "Gemini"].map((service) => ({ server: "198.51.100.9", service, ok: i < 4 })));
  check(!(await discovered(env)).includes("198.51.100.9"), "половина неудачных: не попал");
}

// 5. Без миграции 009: отчеты принимаются, серверов от пользователей нет, проверенные сервером остаются
{
  const env = { DB: makeDb(false), SALT: "s", ADMIN_KEY: "k" };
  check(await send(env, "203.0.113.10", 64500, ok3("dns.example.net")) === 200, "без таблицы reporters отчет принимается");
  const n = env.DB.sqlite.prepare("SELECT COUNT(*) AS n FROM stats").get().n;
  check(n === 3, "счетчики записаны");
  const put = new Request("https://crowd.test/v1/verified", { method: "POST", headers: { "x-admin-key": "k" }, body: JSON.stringify({ servers: [{ server: "dns.verified.test", name: "сайт" }] }) });
  await worker.fetch(put, env, ctx);
  const r = await (await worker.fetch(new Request("https://crowd.test/v1/discover"), env, ctx)).json();
  check(r.servers.length === 1 && r.servers[0].server === "dns.verified.test", "без таблицы: проверенные сервером отдаются, от пользователей пусто");
}

// 6. Ежедневная чистка удаляет старые записи о подсетях
{
  const env = { DB: makeDb(), SALT: "s" };
  const h = Math.floor(Date.now() / 3600000);
  env.DB.sqlite.prepare("INSERT INTO reporters (server, hour, k) VALUES ('x', ?, 'a'), ('x', ?, 'b')").run(h - 72, h);
  const realNow = Date.now;
  // чистка идет в 3 часа UTC
  const at3 = (Math.floor(h / 24) * 24 + 3) * 3600000 + 60000;
  Date.now = () => at3;
  globalThis.fetch = async () => new Response("", { status: 503 }); // ночной поиск без сети
  try { await worker.scheduled({}, env); } finally { Date.now = realNow; }
  const left = env.DB.sqlite.prepare("SELECT k FROM reporters").all().map((x) => x.k);
  check(left.length === 1 && left[0] === "b", "записи старше двух суток удалены");
}

// 7. CR-02: название сервера задает первый отчет, следующие его не меняют
{
  const env = { DB: makeDb(), SALT: "s" };
  const one = (name) => [{ server: "dns.named.test", service: "ChatGPT", ok: true, name }];
  await send(env, "203.0.113.10", 64500, one("Мой DNS"));
  await send(env, "198.18.0.10", 64501, one("НЕ РАБОТАЕТ, качайте с x.test"));
  const nm = () => env.DB.sqlite.prepare("SELECT name FROM servers WHERE server = 'dns.named.test'").get()?.name;
  check(nm() === "Мой DNS", "чужой отчет не переименовал сервер");
  env.DB.sqlite.prepare("INSERT INTO servers (server, name, updated) VALUES ('dns.empty.test', '', 0)").run();
  await send(env, "192.0.2.10", 64502, [{ server: "dns.empty.test", service: "Claude", ok: true, name: "Имя" }]);
  check(env.DB.sqlite.prepare("SELECT name FROM servers WHERE server = 'dns.empty.test'").get().name === "Имя", "пустое название заполняется");
  await send(env, "192.0.2.11", 64502, [{ server: "dns.noname.test", service: "Claude", ok: true }]);
  check(env.DB.sqlite.prepare("SELECT COUNT(*) AS n FROM servers WHERE server = 'dns.noname.test'").get().n === 0, "отчет без названия запись о названии не создает (как раньше)");
}

// 8. CR-03: лимит отчетов в час для IPv6 считается по блоку /64, для IPv4 по адресу (как раньше)
check(limitKeyOf("203.0.113.7") === "203.0.113.7", "IPv4: лимит по самому адресу");
check(limitKeyOf("2001:db8:1:2::5") === limitKeyOf("2001:db8:1:2:ffff:1:2:3"), "IPv6: адреса одного /64 считаются вместе");
check(limitKeyOf("2001:db8:1:2::5") !== limitKeyOf("2001:db8:1:3::5"), "IPv6: соседний /64 отдельно");
check(limitKeyOf("2001:db8::1") === "2001:db8:0:0::/64", "IPv6 с :: раскрывается");
{
  const env = { DB: makeDb(), SALT: "s" };
  const item = [{ server: "dns.rate.test", service: "ChatGPT", ok: true }];
  const codes = [];
  for (let i = 1; i <= 7; i++) codes.push(await send(env, `2001:db8:1:2::${i.toString(16)}`, 64500, item));
  check(codes.slice(0, 5).every((c) => c === 200) && codes.slice(5).every((c) => c === 429), "IPv6: 6-й отчет за час с того же /64 отклонен");
  check(await send(env, "2001:db8:1:3::1", 64500, item) === 200, "IPv6: другой /64 принимается");
  const v4 = [];
  for (let i = 1; i <= 6; i++) v4.push(await send(env, `203.0.113.${i}`, 64501, item));
  check(v4.every((c) => c === 200), "IPv4: разные адреса одной подсети не мешают друг другу (как раньше)");
}

// 9. CR-07: обнаружение и общий рейтинг берутся из кэша Cloudflare, сбой кэша не ломает ответ
{
  const store = new Map();
  const fakeCache = {
    async match(k) { const v = store.get(String(k.url || k)); return v ? new Response(v) : undefined; },
    async put(k, r) { store.set(String(k.url || k), await r.text()); },
  };
  const env = { DB: makeDb(), SALT: "s" };
  let reads = 0; const prep = env.DB.prepare; env.DB.prepare = (sql) => { reads++; return prep(sql); };
  globalThis.caches = { default: fakeCache };
  try {
    const a = await discovered(env); const r1 = reads;
    const b = await discovered(env);
    check(reads === r1 && JSON.stringify(a) === JSON.stringify(b), "обнаружение: второй запрос из кэша, без базы");
    const st1 = await (await worker.fetch(Object.assign(new Request("https://crowd.test/v1/stats?asn=64500"), { cf: {} }), env, ctx)).json();
    const r2 = reads;
    await worker.fetch(Object.assign(new Request("https://crowd.test/v1/stats?asn=64501"), { cf: {} }), env, ctx);
    check(store.has("https://crowd.cache/global-summary") && Array.isArray(st1.global), "общий рейтинг лег в кэш");
    check(reads - r2 < r2 - r1, "другой провайдер: общий рейтинг не пересчитан");
    globalThis.caches = { default: { async match() { throw new Error("down"); }, async put() { throw new Error("down"); } } };
    check(Array.isArray(await discovered(env)), "кэш сломан: обнаружение считается как раньше");
  } finally { delete globalThis.caches; env.DB.prepare = prep; }
}

// 10. CR-18: ночная чистка убирает серверы без единой проверки, остальные не трогает
{
  const env = { DB: makeDb(), SALT: "s" };
  const h = Math.floor(Date.now() / 3600000);
  const q = env.DB.sqlite;
  q.prepare("INSERT INTO servers (server, name, updated) VALUES ('gone.test', 'старый', ?), ('alive.test', 'живой', ?), ('fresh.test', 'новый', ?)").run(h - 300, h - 300, h);
  q.prepare("INSERT INTO stats (hour, server, service, asn, ok, fail) VALUES (?, 'alive.test', 'ChatGPT', 1, 1, 0)").run(h - 2);
  const realNow = Date.now; const at3 = (Math.floor(h / 24) * 24 + 3) * 3600000 + 60000;
  Date.now = () => at3; globalThis.fetch = async () => new Response("", { status: 503 });
  try { await worker.scheduled({}, env); } finally { Date.now = realNow; }
  const left = q.prepare("SELECT server FROM servers ORDER BY server").all().map((x) => x.server);
  check(JSON.stringify(left) === JSON.stringify(["alive.test", "fresh.test"]), "без проверок и старше 8 дней удален; с проверками и свежий остались");
}

// Новая блокировка у провайдера только от разных отправителей (аудит CR-04)
{
  const env = { DB: makeDb(), SALT: "s" };
  const h = Math.floor(Date.now() / 3600000);
  const q = env.DB.sqlite;
  // Неделю youtube.com открывался
  q.prepare("INSERT INTO site_stats (hour, site, asn, bypass, ok, fail) VALUES (?, 'youtube.com', 64500, 0, 10, 0)").run(h - 48);
  const fail = async (ip) => {
    const req = new Request("https://crowd.test/v1/report", { method: "POST", headers: { "cf-connecting-ip": ip, "content-type": "application/json" },
      body: JSON.stringify({ v: 1, app: "0.1.180", items: [], sites: [{ site: "youtube.com", ok: false }] }) });
    req.cf = { asn: 64500, asOrganization: "AS64500" };
    return (await worker.fetch(req, env, ctx)).status;
  };
  const incidentsNow = async () => {
    const prevCaches = globalThis.caches;
    globalThis.caches = { default: { match: async () => undefined, put: async () => {} } }; // без кэша: каждый раз из базы
    try {
      const r = await worker.fetch(new Request("https://crowd.test/v1/stats?asn=64500"), env, ctx);
      return (await r.json()).incidents.map((x) => x.site);
    } finally { globalThis.caches = prevCaches; }
  };
  for (let i = 0; i < 5; i++) await fail("203.0.113.50");
  check(!(await incidentsNow()).includes("youtube.com"), "5 отчетов «не открылось» с одного адреса: блокировки нет");
  check(!/203\.0\.113\.50/.test(JSON.stringify(q.prepare("SELECT * FROM site_reporters").all())), "адреса отправителей в site_reporters нет");
  await fail("198.51.100.77");
  check((await incidentsNow()).includes("youtube.com"), "второй отправитель: блокировка показана");
}

console.log(failures ? `FAILED: ${failures}` : "all ok");
process.exit(failures ? 1 : 0);
