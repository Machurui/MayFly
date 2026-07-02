// Mock data + helpers shared across screens (Claude Design reference — not compiled).
// Exposes everything on `window` for cross-file scope.

const ENGINES = [
  { id: "postgres", label: "PostgreSQL", version: "16.2", versions: ["16.2","15.6","14.11"], port: 5432, driver: "psql", glyph: "Pg", color: "#60a5fa", sample: "postgresql://user_a7f3:••••••••@eph.mayfly.sh:5432/db_zk8m" },
  { id: "mysql", label: "MySQL", version: "8.3", versions: ["8.3","8.0","5.7"], port: 3306, driver: "mysql", glyph: "My", color: "#fbbf24", sample: "mysql://user_a7f3:••••••••@eph.mayfly.sh:3306/db_zk8m" },
  { id: "mariadb", label: "MariaDB", version: "11.3", versions: ["11.3","10.11","10.6"], port: 3306, driver: "mysql", glyph: "Mb", color: "#a78bfa", sample: "mysql://user_a7f3:••••••••@eph.mayfly.sh:3306/db_zk8m" },
  { id: "mongo", label: "MongoDB", version: "7.0", versions: ["7.0","6.0"], port: 27017, driver: "mongodb", glyph: "Mg", color: "#4ade80", sample: "mongodb://user_a7f3:••••••••@eph.mayfly.sh:27017/db_zk8m" },
  { id: "mssql", label: "SQL Server", version: "2022", versions: ["2022","2019"], port: 1433, driver: "mssql", glyph: "Sq", color: "#f87171", sample: "sqlserver://user_a7f3:••••••••@eph.mayfly.sh:1433/db_zk8m" },
];

const DURATIONS = [
  { id: "3h", label: "3 h", ms: 3 * 3600_000, desc: "Quick test" },
  { id: "6h", label: "6 h", ms: 6 * 3600_000, desc: "Half day" },
  { id: "12h", label: "12 h", ms: 12 * 3600_000, desc: "Workshop" },
];

const SIZES = [
  { id: "xs", label: "256 MB", disk: "256 MB", bytes: 256,  desc: "small test" },
  { id: "s",  label: "512 MB", disk: "512 MB", bytes: 512,  desc: "typical app" },
  { id: "m",  label: "1 GB",   disk: "1 GB",   bytes: 1024, desc: "decent dataset" },
  { id: "l",  label: "2 GB",   disk: "2 GB",   bytes: 2048, desc: "heavy import" },
];

const SEEDS = [
  { id: "blank", label: "Blank", desc: "Empty database, no tables." },
  { id: "shop", label: "E-commerce", desc: "products, orders, customers (50k rows)" },
  { id: "blog", label: "Blog", desc: "posts, authors, comments, tags" },
  { id: "iot", label: "IoT timeseries", desc: "10M sensor readings" },
  { id: "northwind", label: "Northwind", desc: "Classic sample dataset" },
  { id: "import", label: "Import dump", desc: "Upload .sql / .dump file" },
];
// NOTE (slice-1 mapping): engine postgres enabled (others disabled); durations 3h/6h/12h -> ttlHours;
// sizes bytes 256/512/1024/2048 -> storageMb; seeds blank + northwind enabled (shop/blog/iot/import disabled).

// Helpers (fmtDuration, fmtRelative, fmtBytes, sparkPath, makeSeries) — see the console/instance screens.
// Instances list, SAMPLE_SCHEMA, SAMPLE_RESULT, SAMPLE_LOG were mock data for the prototype;
// the real Vue app uses the live API (InstanceDto, QueryResultDto, DashboardSummary).
