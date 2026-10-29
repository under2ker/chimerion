-- Страницы, которые сервер нашел в поисковиках (ночная задача): программы берут их как источники.
-- Поисковики видят один запрос в сутки от сервера, а не сотни от программ
CREATE TABLE IF NOT EXISTS found_pages (
  url     TEXT PRIMARY KEY,
  engine  TEXT NOT NULL,             -- bing, brave
  seen    INTEGER NOT NULL           -- час, когда страница последний раз была в выдаче
);
