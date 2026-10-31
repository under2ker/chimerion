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
  updated INTEGER NOT NULL,
  url     TEXT                       -- полный адрес DoH, если сервер DoH (для обнаружения)
);

-- Названия сетей провайдеров
CREATE TABLE IF NOT EXISTS asns (
  asn     INTEGER PRIMARY KEY,
  org     TEXT NOT NULL
);

-- Страницы, которые сервер нашел в поисковиках (ночная задача): программы берут их как источники.
-- Поисковики видят один запрос в сутки от сервера, а не сотни от программ
CREATE TABLE IF NOT EXISTS found_pages (
  url     TEXT PRIMARY KEY,
  engine  TEXT NOT NULL,             -- bing, brave
  seen    INTEGER NOT NULL           -- час, когда страница последний раз была в выдаче
);

-- Сколько отчетов пришло за час с одного адреса: хэш(IP + час + соль), не больше 5
CREATE TABLE IF NOT EXISTS seen (
  k       TEXT PRIMARY KEY,
  hour    INTEGER NOT NULL,
  n       INTEGER NOT NULL DEFAULT 1
);

-- Счетчик копий программы: хэш ID устройства, маска для владельца, дни, версия, время работы и признаки подмены
CREATE TABLE IF NOT EXISTS devices (
  id            TEXT PRIMARY KEY,      -- SHA-256 от ID устройства с солью сервера
  mask          TEXT NOT NULL,         -- первые и последние 4 знака ID
  first_day     INTEGER NOT NULL,
  last_day      INTEGER NOT NULL,
  days          INTEGER NOT NULL DEFAULT 1,
  version       TEXT,
  source        TEXT NOT NULL DEFAULT '?',  -- board, windows (UUID скрыт), random
  token         TEXT NOT NULL DEFAULT '',
  token_changes INTEGER NOT NULL DEFAULT 0, -- другая установка Windows или копия
  replaced      INTEGER NOT NULL DEFAULT 0, -- ID сменился
  moved         INTEGER NOT NULL DEFAULT 0, -- папку данных перенесли
  total_sec     INTEGER NOT NULL DEFAULT 0,
  last_sec      INTEGER NOT NULL DEFAULT 0,
  runs          INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS devices_last ON devices (last_day);

-- Точный счет по дням: сколько разных ID отметилось
CREATE TABLE IF NOT EXISTS device_days (
  day INTEGER PRIMARY KEY,
  n   INTEGER NOT NULL
);

-- Приблизительная оценка: разные адреса за сутки. k: хэш адреса с солью суток, между днями не сравнивается
CREATE TABLE IF NOT EXISTS reach (
  day INTEGER NOT NULL,
  k   TEXT NOT NULL,
  PRIMARY KEY (day, k)
);
