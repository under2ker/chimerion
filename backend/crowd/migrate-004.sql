-- Обращения из программы («Сообщить о проблеме»): текст, очищенный отчет, необязательный контакт и ответ владельца.
-- key: хэш секретного токена обращения (токен знает только программа, по нему она читает ответ), code: код для связи с ботом.
CREATE TABLE IF NOT EXISTS tickets (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  key          TEXT NOT NULL UNIQUE,
  code         TEXT NOT NULL,
  created      INTEGER NOT NULL,          -- секунды Unix
  updated      INTEGER NOT NULL,
  version      TEXT,
  text         TEXT NOT NULL,
  report       TEXT,                      -- очищенный отчет диагностики, через 30 дней стирается
  contact_kind TEXT,                      -- email, tg (@имя), tgid (числовой ID в Telegram или в боте)
  contact      TEXT,
  status       TEXT NOT NULL DEFAULT 'new',  -- new, work, fixed, closed
  fix_version  TEXT,
  reply        TEXT,
  owner_msg    INTEGER,                   -- сообщение в чате владельца: ответ на него уходит пользователю
  user_chat    INTEGER                    -- чат пользователя с ботом, если он связал обращение через ссылку
);
CREATE INDEX IF NOT EXISTS tickets_owner_msg ON tickets (owner_msg);
CREATE INDEX IF NOT EXISTS tickets_status ON tickets (status, updated);
