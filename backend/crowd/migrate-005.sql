-- Служебные значения Worker: сейчас чат владельца (его запоминает команда /owner <ключ администратора> в боте)
CREATE TABLE IF NOT EXISTS meta (
  k TEXT PRIMARY KEY,
  v TEXT NOT NULL
);
