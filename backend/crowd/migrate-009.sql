-- Кто сообщал о сервере (для /v1/discover): хэш подсети отправителя с солью, сам адрес не хранится и из хэша
-- не восстанавливается. Сервер от пользователей попадает в «обнаружение» только при отчетах из 3 разных подсетей
-- за сутки. Записи старше двух суток удаляет ежедневная чистка.
-- Применить к рабочей базе ДО выкладки нового worker.js:
--   npx wrangler d1 execute chimerion-crowd --remote --file migrate-009.sql
CREATE TABLE IF NOT EXISTS reporters (
  server  TEXT    NOT NULL,          -- IP сервера или хост DoH
  hour    INTEGER NOT NULL,          -- час отчета (UTC)
  k       TEXT    NOT NULL,          -- SHA-256 от подсети отправителя (/24 или /48) с солью
  PRIMARY KEY (server, hour, k)
);
CREATE INDEX IF NOT EXISTS reporters_hour ON reporters (hour);
