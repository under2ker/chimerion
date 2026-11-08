-- Открываются ли популярные сервисы в сети провайдера: по часу, сервису и тому, был ли включен обход.
-- Только счетчики, без адресов. По ним сервер замечает новые блокировки (worker.js, incidents)
CREATE TABLE IF NOT EXISTS site_stats (
  hour    INTEGER NOT NULL,          -- часы с начала эпохи (UTC)
  site    TEXT    NOT NULL,          -- сервис из списка SITES: youtube.com, discord.com...
  asn     INTEGER NOT NULL,          -- сеть провайдера (cf.asn)
  bypass  INTEGER NOT NULL,          -- 1: у пользователя работал обход (zapret)
  ok      INTEGER NOT NULL DEFAULT 0,
  fail    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (hour, site, asn, bypass)
);
CREATE INDEX IF NOT EXISTS site_stats_asn ON site_stats (asn, hour);
