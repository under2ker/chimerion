-- Общая карта серверов CHIMERION. Хранятся только счетчики: сервер, сервис, сеть провайдера (AS), час и сколько
-- проверок прошло или нет. IP-адресов пользователей нет: защита от накрутки держится на хэше IP с солью и часом,
-- он живет двое суток.

CREATE TABLE IF NOT EXISTS stats (
  hour    INTEGER NOT NULL,          -- часы с начала эпохи (UTC)
  server  TEXT    NOT NULL,          -- IP сервера или хост DoH
  service TEXT    NOT NULL,          -- ChatGPT, Claude, Gemini...
  asn     INTEGER NOT NULL,          -- сеть провайдера, из которой пришел отчет (Cloudflare cf.asn)
  ok      INTEGER NOT NULL DEFAULT 0,
  fail    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (hour, server, service, asn)
);
CREATE INDEX IF NOT EXISTS stats_asn ON stats (asn, hour);

-- Имена серверов, как их показывают программы (последнее присланное)
CREATE TABLE IF NOT EXISTS servers (
  server  TEXT PRIMARY KEY,
  name    TEXT NOT NULL,
  updated INTEGER NOT NULL
);

-- Названия сетей провайдеров
CREATE TABLE IF NOT EXISTS asns (
  asn     INTEGER PRIMARY KEY,
  org     TEXT NOT NULL
);

-- Сколько отчетов пришло за час с одного адреса: хэш(IP + час + соль), не больше 5
CREATE TABLE IF NOT EXISTS seen (
  k       TEXT PRIMARY KEY,
  hour    INTEGER NOT NULL,
  n       INTEGER NOT NULL DEFAULT 1
);
