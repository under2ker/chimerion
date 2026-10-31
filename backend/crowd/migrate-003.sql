-- Счетчик копий программы: только хэш ID устройства, дни первой и последней отметки и версия
CREATE TABLE IF NOT EXISTS installs (
  id        TEXT PRIMARY KEY,        -- SHA-256 от ID устройства с солью сервера
  first_day INTEGER NOT NULL,
  last_day  INTEGER NOT NULL,
  version   TEXT
);
CREATE INDEX IF NOT EXISTS installs_last ON installs (last_day);
