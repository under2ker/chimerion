-- Smart DNS, которые нашел и проверил сервер CHIMERION (server_search.py на VPS): адрес отвечает и подменяет
-- адреса ChatGPT или Claude. /v1/discover отдает их программам вместе с серверами, которые работают у многих.
-- Применить к рабочей базе ДО выкладки нового worker.js:
--   npx wrangler d1 execute chimerion-crowd --remote --file migrate-008.sql
CREATE TABLE IF NOT EXISTS verified (
  server  TEXT PRIMARY KEY,          -- IP или хост DoH
  name    TEXT NOT NULL,             -- сайт, где адрес нашелся
  url     TEXT,                      -- полный адрес DoH
  ms      INTEGER,                   -- время ответа с сервера, мс
  seen    INTEGER NOT NULL           -- час последней удачной проверки
);
CREATE INDEX IF NOT EXISTS verified_seen ON verified (seen);
