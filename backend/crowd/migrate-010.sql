-- Кто сообщал, что популярный сервис не открывается (аудит CR-04): хэш адреса отправителя с солью (IPv4 целиком,
-- IPv6 по блоку /64), сам адрес не хранится. «Новая блокировка» показывается, только если не открылось хотя бы у
-- двух разных отправителей: один человек мог прислать 5 отчетов в час и изобразить ее сам. Живут двое суток.
-- Применить к рабочей базе ДО выкладки нового worker.js:
--   npx wrangler d1 execute chimerion-crowd --remote --file migrate-010.sql
CREATE TABLE IF NOT EXISTS site_reporters (
  asn     INTEGER NOT NULL,
  site    TEXT    NOT NULL,
  bypass  INTEGER NOT NULL,
  hour    INTEGER NOT NULL,
  k       TEXT    NOT NULL,
  PRIMARY KEY (asn, site, bypass, hour, k)
);
CREATE INDEX IF NOT EXISTS site_reporters_hour ON site_reporters (hour);
