-- 0.1.35: адрес DoH для обнаружения серверов
ALTER TABLE servers ADD COLUMN url TEXT;
