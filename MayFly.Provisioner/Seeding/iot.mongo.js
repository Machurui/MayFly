// IoT seed for MongoDB — runs as admin in /docker-entrypoint-initdb.d/.
// Target appdb explicitly (default init db is not guaranteed).
var appdb = db.getSiblingDB("appdb");

appdb.getCollection("devices").insertMany([
  { _id: 1, name: "Router-A1",     location: "Server Room"  },
  { _id: 2, name: "Server-B2",     location: "Data Centre"  },
  { _id: 3, name: "Camera-C3",     location: "Lobby"        },
  { _id: 4, name: "EnvSensor-D4",  location: "Warehouse"    },
  { _id: 5, name: "Thermostat-E5", location: "Office Floor" }
]);

appdb.getCollection("sensor_readings").insertMany([
  { _id:  1, device_id: 1, metric: "cpu_load",    reading_value: 12.5,    reading_at: "2026-01-01T00:00:00Z" },
  { _id:  2, device_id: 1, metric: "cpu_load",    reading_value: 18.75,   reading_at: "2026-01-01T01:00:00Z" },
  { _id:  3, device_id: 1, metric: "temperature", reading_value: 38.2,    reading_at: "2026-01-01T00:00:00Z" },
  { _id:  4, device_id: 1, metric: "temperature", reading_value: 39.1,    reading_at: "2026-01-01T01:00:00Z" },
  { _id:  5, device_id: 1, metric: "voltage",     reading_value: 220.0,   reading_at: "2026-01-01T00:00:00Z" },
  { _id:  6, device_id: 1, metric: "voltage",     reading_value: 219.8,   reading_at: "2026-01-01T01:00:00Z" },
  { _id:  7, device_id: 2, metric: "cpu_load",    reading_value: 55.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id:  8, device_id: 2, metric: "cpu_load",    reading_value: 62.3,    reading_at: "2026-01-01T01:00:00Z" },
  { _id:  9, device_id: 2, metric: "temperature", reading_value: 45.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 10, device_id: 2, metric: "temperature", reading_value: 46.5,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 11, device_id: 2, metric: "voltage",     reading_value: 220.5,   reading_at: "2026-01-01T00:00:00Z" },
  { _id: 12, device_id: 2, metric: "voltage",     reading_value: 220.2,   reading_at: "2026-01-01T01:00:00Z" },
  { _id: 13, device_id: 3, metric: "cpu_load",    reading_value: 5.0,     reading_at: "2026-01-01T00:00:00Z" },
  { _id: 14, device_id: 3, metric: "cpu_load",    reading_value: 6.25,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 15, device_id: 3, metric: "temperature", reading_value: 28.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 16, device_id: 3, metric: "temperature", reading_value: 28.5,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 17, device_id: 3, metric: "voltage",     reading_value: 219.5,   reading_at: "2026-01-01T00:00:00Z" },
  { _id: 18, device_id: 3, metric: "voltage",     reading_value: 219.7,   reading_at: "2026-01-01T01:00:00Z" },
  { _id: 19, device_id: 4, metric: "temperature", reading_value: 22.1,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 20, device_id: 4, metric: "temperature", reading_value: 22.3,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 21, device_id: 4, metric: "humidity",    reading_value: 58.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 22, device_id: 4, metric: "humidity",    reading_value: 59.5,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 23, device_id: 4, metric: "pressure",    reading_value: 1013.25, reading_at: "2026-01-01T00:00:00Z" },
  { _id: 24, device_id: 4, metric: "pressure",    reading_value: 1012.75, reading_at: "2026-01-01T01:00:00Z" },
  { _id: 25, device_id: 5, metric: "temperature", reading_value: 21.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 26, device_id: 5, metric: "temperature", reading_value: 21.5,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 27, device_id: 5, metric: "humidity",    reading_value: 45.0,    reading_at: "2026-01-01T00:00:00Z" },
  { _id: 28, device_id: 5, metric: "humidity",    reading_value: 46.0,    reading_at: "2026-01-01T01:00:00Z" },
  { _id: 29, device_id: 5, metric: "pressure",    reading_value: 1013.0,  reading_at: "2026-01-01T00:00:00Z" },
  { _id: 30, device_id: 5, metric: "pressure",    reading_value: 1013.5,  reading_at: "2026-01-01T01:00:00Z" }
]);
