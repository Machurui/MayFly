CREATE TABLE devices (
  id       INT         PRIMARY KEY,
  name     VARCHAR(80) NOT NULL,
  location VARCHAR(80)
);
CREATE TABLE sensor_readings (
  id            INT           PRIMARY KEY,
  device_id     INT,
  metric        VARCHAR(40),
  reading_value DECIMAL(12,4),
  reading_at    VARCHAR(30)
);
INSERT INTO devices (id, name, location) VALUES
  (1, 'Router-A1',      'Server Room'),
  (2, 'Server-B2',      'Data Centre'),
  (3, 'Camera-C3',      'Lobby'),
  (4, 'EnvSensor-D4',   'Warehouse'),
  (5, 'Thermostat-E5',  'Office Floor');
INSERT INTO sensor_readings (id, device_id, metric, reading_value, reading_at) VALUES
  (1,  1, 'cpu_load',    12.5000, '2026-01-01T00:00:00Z'),
  (2,  1, 'cpu_load',    18.7500, '2026-01-01T01:00:00Z'),
  (3,  1, 'temperature', 38.2000, '2026-01-01T00:00:00Z'),
  (4,  1, 'temperature', 39.1000, '2026-01-01T01:00:00Z'),
  (5,  1, 'voltage',    220.0000, '2026-01-01T00:00:00Z'),
  (6,  1, 'voltage',    219.8000, '2026-01-01T01:00:00Z'),
  (7,  2, 'cpu_load',    55.0000, '2026-01-01T00:00:00Z'),
  (8,  2, 'cpu_load',    62.3000, '2026-01-01T01:00:00Z'),
  (9,  2, 'temperature', 45.0000, '2026-01-01T00:00:00Z'),
  (10, 2, 'temperature', 46.5000, '2026-01-01T01:00:00Z'),
  (11, 2, 'voltage',    220.5000, '2026-01-01T00:00:00Z'),
  (12, 2, 'voltage',    220.2000, '2026-01-01T01:00:00Z'),
  (13, 3, 'cpu_load',     5.0000, '2026-01-01T00:00:00Z'),
  (14, 3, 'cpu_load',     6.2500, '2026-01-01T01:00:00Z'),
  (15, 3, 'temperature', 28.0000, '2026-01-01T00:00:00Z'),
  (16, 3, 'temperature', 28.5000, '2026-01-01T01:00:00Z'),
  (17, 3, 'voltage',    219.5000, '2026-01-01T00:00:00Z'),
  (18, 3, 'voltage',    219.7000, '2026-01-01T01:00:00Z'),
  (19, 4, 'temperature', 22.1000, '2026-01-01T00:00:00Z'),
  (20, 4, 'temperature', 22.3000, '2026-01-01T01:00:00Z'),
  (21, 4, 'humidity',    58.0000, '2026-01-01T00:00:00Z'),
  (22, 4, 'humidity',    59.5000, '2026-01-01T01:00:00Z'),
  (23, 4, 'pressure',  1013.2500, '2026-01-01T00:00:00Z'),
  (24, 4, 'pressure',  1012.7500, '2026-01-01T01:00:00Z'),
  (25, 5, 'temperature', 21.0000, '2026-01-01T00:00:00Z'),
  (26, 5, 'temperature', 21.5000, '2026-01-01T01:00:00Z'),
  (27, 5, 'humidity',    45.0000, '2026-01-01T00:00:00Z'),
  (28, 5, 'humidity',    46.0000, '2026-01-01T01:00:00Z'),
  (29, 5, 'pressure',  1013.0000, '2026-01-01T00:00:00Z'),
  (30, 5, 'pressure',  1013.5000, '2026-01-01T01:00:00Z');
