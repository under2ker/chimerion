-- Сводка владельца: на связи сейчас и подключены к DNS.
-- Применить к рабочей базе ДО выкладки нового worker.js, иначе отметки (INSERT в devices) будут падать:
--   npx wrangler d1 execute chimerion-crowd --remote --file migrate-007.sql
ALTER TABLE devices ADD COLUMN seen_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE devices ADD COLUMN dns INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS devices_seen ON devices (seen_at);
