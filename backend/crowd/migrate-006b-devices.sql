-- Таблицы счетчика копий в том виде, в каком они были до migrate-007 (аудит CR-19). В рабочей базе они уже есть:
-- их создали вручную, а migrate-003 создавал старую таблицу installs, которой код больше не пользуется. Без этого
-- файла база, поднятая по миграциям по порядку, ломалась на migrate-007 (ALTER TABLE devices). На рабочую базу
-- запускать не нужно; если запустить, ничего не изменится (IF NOT EXISTS).
-- Новую базу проще поднимать целиком по schema.sql.
CREATE TABLE IF NOT EXISTS devices (
  id            TEXT PRIMARY KEY,
  mask          TEXT NOT NULL,
  first_day     INTEGER NOT NULL,
  last_day      INTEGER NOT NULL,
  days          INTEGER NOT NULL DEFAULT 1,
  version       TEXT,
  source        TEXT NOT NULL DEFAULT '?',
  token         TEXT NOT NULL DEFAULT '',
  token_changes INTEGER NOT NULL DEFAULT 0,
  replaced      INTEGER NOT NULL DEFAULT 0,
  moved         INTEGER NOT NULL DEFAULT 0,
  total_sec     INTEGER NOT NULL DEFAULT 0,
  last_sec      INTEGER NOT NULL DEFAULT 0,
  runs          INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS devices_last ON devices (last_day);

CREATE TABLE IF NOT EXISTS device_days (
  day INTEGER PRIMARY KEY,
  n   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS reach (
  day INTEGER NOT NULL,
  k   TEXT NOT NULL,
  PRIMARY KEY (day, k)
);
