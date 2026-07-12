# MayFly SP3 — Initial-Data Templates — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the wizard's four seed templates (Northwind, E-commerce, Blog, IoT) real and available on all five engines, via one portable ANSI-SQL seed + one Mongo JS variant per template.

**Architecture:** A `SeedCatalog` (Provisioner) loads embedded seed resources (`Seeding/<id>.sql` portable + `Seeding/<id>.mongo.js`). Each engine's `BuildSetup` consults the catalog by `initialData` and delivers the seed via its native channel (Postgres/MySQL/MariaDB init-scripts; SQL Server appended to its `PostReadyExec` sqlcmd; Mongo `01-seed.js`). The seed runs as admin at first init; both validators accept the four templates on all engines. Import dump stays out of scope (its own sub-project).

**Tech Stack:** .NET 8, Docker.DotNet.Enhanced, embedded resources, xUnit/FluentAssertions/Testcontainers, `Npgsql`/`MySqlConnector`/`Microsoft.Data.SqlClient`/`MongoDB.Driver` (tests only), Vue 3.

## Global Constraints

- **Runtime:** .NET 8. Only `MayFly.Provisioner` references `Docker.DotNet.Enhanced`; `MayFly.Api` references no Docker package and no `MongoDB.Driver`. DB drivers for connecting in tests live in `MayFly.Tests` (all already present: Npgsql, MySqlConnector, Microsoft.Data.SqlClient, MongoDB.Driver).
- **Commit messages:** plain conventional-commit — NO `Co-Authored-By`, NO attribution/"Generated with"/"🤖" trailer. Commit with `git -c user.name='fanfan' -c user.email='fazemachurui@outlook.fr' commit -m "..."`.
- **Templates (verbatim ids):** `northwind`, `ecommerce`, `blog`, `iot`. Available on all engines `postgres|mysql|mariadb|mssql|mongo`. `blank` remains the always-available default. `dump` stays disabled/rejected (out of scope).
- **Portable SQL constraints (one `.sql` must run on postgres+mysql+mariadb+mssql):** explicit integer PKs in INSERTs (NO SERIAL/AUTO_INCREMENT/IDENTITY); column types limited to `INT`, `VARCHAR(n)`, `DECIMAL(p,s)`; dates/timestamps as `VARCHAR(30)` ISO-8601 (no TIMESTAMP/DATETIME/DATETIME2); booleans as `INT` 0/1 (no BOOLEAN); no `TEXT`; `lower_snake_case` unquoted identifiers avoiding reserved words (`orders` not `order`, `blog_comments`/`comments` is fine, avoid `user`/`table`); standard `CREATE TABLE` + `INSERT INTO t (cols) VALUES (...)`; string literals escape `'` as `''`; no engine-specific functions.
- **Mongo variant:** `Seeding/<id>.mongo.js` = `db.getCollection("<coll>").insertMany([ ... ])` for the same logical dataset (documents), run as an init `.js` as admin.
- **Seed runs as admin at first init** (SQL init-scripts / `01-seed.js` / `sqlcmd -U sa`). The appuser reads seeded objects via its db-level grants (Postgres additionally needs a trailing per-object GRANT — see Task 1).
- **Both validators** (`MayFly.Api/Validation/ApiSpecValidator.cs` AND `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`) gate `initialData`; each template id is added to BOTH in lockstep with its seed files.
- **Docker integration tests** join `[Collection("docker-sequential")]`. mongo/mysql/mariadb arm64-native (fast); mssql emulated (slow — generous timeouts). Clean `docker rm -f $(docker ps -aq --filter 'name=mayfly-')` before full-suite runs.

---

## File Structure

```
MayFly.Provisioner/
  Seeding/SeedCatalog.cs        # NEW: static loader for <id>.sql / <id>.mongo.js embedded resources
  Seeding/northwind.sql         # MODIFY: rewrite pg-specific -> portable ANSI
  Seeding/northwind.mongo.js    # NEW
  Seeding/ecommerce.sql + .mongo.js   # NEW (Task 3)
  Seeding/blog.sql + .mongo.js        # NEW (Task 4)
  Seeding/iot.sql  + .mongo.js        # NEW (Task 5)
  MayFly.Provisioner.csproj     # MODIFY: <EmbeddedResource> for each new seed file
  Engines/PostgresEngineProvider.cs   # MODIFY: BuildSetup consults catalog (+ trailing GRANT)
  Engines/MySqlEngineProvider.cs      # MODIFY: BuildSetup consults catalog (init-script)
  Engines/SqlServerEngineProvider.cs  # MODIFY: BuildSetup appends seed to PostReadyExec sqlcmd
  Engines/MongoEngineProvider.cs      # MODIFY: BuildSetup adds 01-seed.js from catalog
  Validation/InstanceSpecValidator.cs # MODIFY: InitialData allow-set
MayFly.Api/
  Validation/ApiSpecValidator.cs      # MODIFY: Init allow-set; drop northwind-only-postgres rule
MayFly.Web/
  src/components/InitialDataPicker.vue # MODIFY: enable 4 templates all engines (Task 6)
scripts/e2e-fullstack.sh               # MODIFY: seeded-template e2e (Task 6)
```

---

## Phase 1 — Catalog + mechanism + Northwind portable

### Task 1: `SeedCatalog` + portable Northwind on the SQL engines + validators

**Files:** Create `MayFly.Provisioner/Seeding/SeedCatalog.cs`; Modify `Seeding/northwind.sql`, `MayFly.Provisioner.csproj`, `Engines/PostgresEngineProvider.cs`, `Engines/MySqlEngineProvider.cs`, `Engines/SqlServerEngineProvider.cs`, `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`, `MayFly.Api/Validation/ApiSpecValidator.cs`; Test `MayFly.Tests/Provisioner/SeedTemplateTests.cs`.

**Interfaces:**
- Produces: `SeedCatalog` (static):
  ```csharp
  public static class SeedCatalog
  {
      public static readonly IReadOnlySet<string> Templates = new HashSet<string> { "northwind" }; // grows per task
      public static bool IsTemplate(string initialData) => Templates.Contains(initialData);
      public static string GetSql(string templateId);      // loads embedded Seeding/<id>.sql; throws if absent
      public static string GetMongoJs(string templateId);  // loads embedded Seeding/<id>.mongo.js
  }
  ```
  (Load via `Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith($"{templateId}.sql"))` — the same pattern as the current `ReadEmbeddedNorthwind`.)

- [ ] **Step 1: Write the failing integration test** `SeedTemplateTests` (northwind on the 4 SQL engines)
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class SeedTemplateTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    public async Task Northwind_seeds_products_readable_by_appuser(string engine)
    {
        // Provision engine with initialData "northwind" (mssql: storageMb 1024, generous timeout).
        // Connect as appuser via the sidecar published port using the engine's driver
        //   (Npgsql / MySqlConnector / Microsoft.Data.SqlClient — mirror the SP2 engine-client tests).
        //  - "SELECT COUNT(*) FROM products" > 0 (seeded, readable by appuser).
        //  - an appuser INSERT into a seeded table succeeds (appuser has write on the seeded objects).
        // finally destroy.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter SeedTemplateTests` (mysql/mariadb/mssql reject northwind today).
- [ ] **Step 3: Implement.**
  - `SeedCatalog.cs` per the interface above (Templates = `{ "northwind" }` for now).
  - `Seeding/northwind.sql`: **rewrite** to the portable subset — tables `customers(id INT PRIMARY KEY, name VARCHAR(100), city VARCHAR(80))`, `products(id INT PRIMARY KEY, name VARCHAR(100), price DECIMAL(10,2))`, `orders(id INT PRIMARY KEY, customer_id INT, order_date VARCHAR(30))`, `order_details(order_id INT, product_id INT, quantity INT, PRIMARY KEY(order_id, product_id))` — plus a deterministic compact dataset (e.g. ~10 customers, ~15 products, ~12 orders, ~30 order_details) using ONLY the portable constraints from Global Constraints. `products` MUST have rows (the existing `InitScriptTests` asserts `SELECT COUNT(*) FROM products > 0`).
  - `MayFly.Provisioner.csproj`: keep the existing `<EmbeddedResource Include="Seeding/northwind.sql" />` (already declared).
  - `PostgresEngineProvider.BuildSetup`: replace `if (initialData == "northwind")` with `if (SeedCatalog.IsTemplate(initialData))`; the seed body = `SeedCatalog.GetSql(initialData)` + the existing trailing `GRANT ALL ON ALL TABLES/SEQUENCES IN SCHEMA public TO {c.AppUser}` lines. Keep `ReadEmbeddedNorthwind` removed/replaced by `SeedCatalog.GetSql`.
  - `MySqlEngineProvider.BuildSetup`: after the roles script, `if (SeedCatalog.IsTemplate(initialData)) scripts.Add(("01-seed.sql", SeedCatalog.GetSql(initialData)));` (no grant — appuser has `ON appdb.*`). (MariaDB inherits.)
  - `SqlServerEngineProvider.BuildSetup`: when `SeedCatalog.IsTemplate(initialData)`, append to the `-Q` sql string, after the role batches: `"\nGO\nUSE [" + db + "];\nGO\n" + SeedCatalog.GetSql(initialData)`. (The connection stays in `[appdb]` context; the seed's `;`-separated CREATE/INSERT run as one batch. Compact seeds fit the exec argv.)
  - **Validators (both):** add `ecommerce`, `blog`, `iot` are NOT added yet — set the allow-set to `{ "blank", "northwind" }` (unchanged set) BUT **remove the `northwind && engine != postgres` cross-rule** in `ApiSpecValidator` (lines 18-19) so northwind is now valid on all engines. `InstanceSpecValidator` already has no such cross-rule — leave its set `{ "blank", "northwind" }`.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter SeedTemplateTests` (4 SQL engines seed northwind); then `dotnet test MayFly.Tests --filter InitScriptTests` (pg northwind still green); then full suite once.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): SeedCatalog + portable Northwind seed on all SQL engines"`

---

### Task 2: Northwind on Mongo

**Files:** Create `MayFly.Provisioner/Seeding/northwind.mongo.js`; Modify `MayFly.Provisioner.csproj`, `Engines/MongoEngineProvider.cs`; Test `MayFly.Tests/Provisioner/SeedTemplateMongoTests.cs`.

**Interfaces:** Consumes `SeedCatalog.GetMongoJs("northwind")`.

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class SeedTemplateMongoTests
{
    [Fact(Timeout = 120000)]
    public async Task Northwind_seeds_mongo_readable_by_appuser()
    {
        // Provision engine "mongo" with initialData "northwind".
        // Connect as appuser via MongoDB.Driver (mongodb://appuser:pwd@localhost:port/appdb?authSource=appdb):
        //  - db.getCollection("products").CountDocuments > 0 (seeded, readable by appuser).
        // finally destroy.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter SeedTemplateMongoTests` (mongo rejects northwind today, and no mongo seed exists).
- [ ] **Step 3: Implement.**
  - `Seeding/northwind.mongo.js`: `db.getCollection("products").insertMany([ { _id: 1, name: "...", price: 10.0 }, ... ]);` plus `customers`, `orders`, `order_details` collections mirroring the SQL dataset (documents). `products` MUST have rows.
  - `MayFly.Provisioner.csproj`: `<EmbeddedResource Include="Seeding/northwind.mongo.js" />`.
  - `MongoEngineProvider.BuildSetup`: after `00-roles.js`, `if (SeedCatalog.IsTemplate(initialData)) scripts.Add(("01-seed.js", SeedCatalog.GetMongoJs(initialData)));`.
  - Validators already accept `northwind` (Task 1) — no validator change.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter SeedTemplateMongoTests`; full suite once.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): Northwind seed on MongoDB (init .js from catalog)"`

---

## Phase 2-4 — The three new templates

### Task 3: E-commerce template

**Files:** Create `MayFly.Provisioner/Seeding/ecommerce.sql`, `Seeding/ecommerce.mongo.js`; Modify `MayFly.Provisioner.csproj`, `Seeding/SeedCatalog.cs`, `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`, `MayFly.Api/Validation/ApiSpecValidator.cs`; Test extend `SeedTemplateTests` + `SeedTemplateMongoTests`.

**Interfaces:** Consumes the Task-1 catalog/wiring — no provider change needed (the providers already consult `SeedCatalog.IsTemplate`). Adding a template = seed files + add id to `SeedCatalog.Templates` + both validators.

- [ ] **Step 1: Write the failing test** — add `[InlineData]` cases (or a new `[Theory]`) so `ecommerce` is provisioned on all 5 engines and a known seeded row/doc count is asserted (e.g. `SELECT COUNT(*) FROM products` == the exact number you author, and the mongo `products` collection count matches). Assert appuser can read it.
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter "SeedTemplateTests|SeedTemplateMongoTests"` (ecommerce rejected / no seed).
- [ ] **Step 3: Implement.**
  - `Seeding/ecommerce.sql` (portable, per Global Constraints): tables `products(id, name, price, category VARCHAR(60))`, `customers(id, name, email VARCHAR(120))`, `orders(id, customer_id, order_date VARCHAR(30), total DECIMAL(10,2))`, `order_items(order_id, product_id, quantity, unit_price DECIMAL(10,2), PRIMARY KEY(order_id, product_id))` + a compact deterministic dataset.
  - `Seeding/ecommerce.mongo.js`: the same dataset as documents (orders may embed their items).
  - `MayFly.Provisioner.csproj`: `<EmbeddedResource>` for both files.
  - `SeedCatalog.Templates`: add `"ecommerce"`.
  - Both validators: add `"ecommerce"` to the `initialData` allow-set.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter "SeedTemplateTests|SeedTemplateMongoTests"`; full suite once.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): E-commerce seed template (portable SQL + Mongo)"`

---

### Task 4: Blog template

**Files:** Create `MayFly.Provisioner/Seeding/blog.sql`, `Seeding/blog.mongo.js`; Modify `MayFly.Provisioner.csproj`, `Seeding/SeedCatalog.cs`, both validators; Test extend `SeedTemplateTests`/`SeedTemplateMongoTests`.

- [ ] **Step 1: Write the failing test** — provision `blog` on all 5 engines; assert a known seeded count (e.g. `SELECT COUNT(*) FROM posts` == authored number; mongo `posts` count matches) readable by appuser.
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter "SeedTemplateTests|SeedTemplateMongoTests"`.
- [ ] **Step 3: Implement.**
  - `Seeding/blog.sql` (portable): tables `authors(id, name, email VARCHAR(120))`, `posts(id, author_id, title VARCHAR(200), body VARCHAR(4000), published_at VARCHAR(30))`, `blog_comments(id, post_id, author_name VARCHAR(100), body VARCHAR(2000), created_at VARCHAR(30))`, `tags(id, name VARCHAR(50))`, `post_tags(post_id, tag_id, PRIMARY KEY(post_id, tag_id))` + a compact dataset. (Avoid reserved word `comments` collisions by using `blog_comments`.)
  - `Seeding/blog.mongo.js`: posts as documents, comments embedded in each post, tags as arrays.
  - `.csproj` EmbeddedResources; `SeedCatalog.Templates` += `"blog"`; both validators += `"blog"`.
- [ ] **Step 4: GREEN** + full suite once.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): Blog seed template (portable SQL + Mongo)"`

---

### Task 5: IoT template

**Files:** Create `MayFly.Provisioner/Seeding/iot.sql`, `Seeding/iot.mongo.js`; Modify `MayFly.Provisioner.csproj`, `Seeding/SeedCatalog.cs`, both validators; Test extend `SeedTemplateTests`/`SeedTemplateMongoTests`.

- [ ] **Step 1: Write the failing test** — provision `iot` on all 5 engines; assert a known seeded count (e.g. `SELECT COUNT(*) FROM sensor_readings` == authored number; mongo `sensor_readings` count matches) readable by appuser.
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter "SeedTemplateTests|SeedTemplateMongoTests"`.
- [ ] **Step 3: Implement.**
  - `Seeding/iot.sql` (portable, time-series with ISO timestamps as VARCHAR): tables `devices(id INT PRIMARY KEY, name VARCHAR(80), location VARCHAR(80))`, `sensor_readings(id INT PRIMARY KEY, device_id INT, metric VARCHAR(40), value DECIMAL(12,4), reading_at VARCHAR(30))` + a compact deterministic dataset (e.g. 5 devices × N readings each, ISO-8601 timestamps as VARCHAR(30)).
  - `Seeding/iot.mongo.js`: devices + sensor_readings collections (readings may embed device ref).
  - `.csproj` EmbeddedResources; `SeedCatalog.Templates` += `"iot"`; both validators += `"iot"`.
- [ ] **Step 4: GREEN** + full suite once.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): IoT seed template (portable SQL + Mongo)"`

---

## Phase 5 — Frontend + e2e

### Task 6: Enable templates in the wizard + e2e

**Files:** Modify `MayFly.Web/src/components/InitialDataPicker.vue`, `scripts/e2e-fullstack.sh`; Test `MayFly.Web/src/components/InitialDataPicker.test.ts`.

- [ ] **Step 1: Frontend test (RED).** In `InitialDataPicker.test.ts`: for EACH engine in `postgres|mysql|mariadb|mssql|mongo`, mounting the picker with that `engine` prop makes `northwind`, `ecommerce`, `blog`, `iot` selectable (clicking sets the model to that id); `blank` always selectable; `dump` NOT selectable (disabled). `npx vitest run src/components/InitialDataPicker.test.ts` → RED.
- [ ] **Step 2: Implement.** `InitialDataPicker.vue` `isEnabled(seed)`: return `true` for `blank` and for `northwind`/`ecommerce`/`blog`/`iot` on ALL engines; return `false` for `dump` (unchanged — "soon"). Remove the `seed.id === 'northwind' && props.engine === 'postgres'` gate. Keep the defensive reset `watch` (it now never fires for the 4 templates; harmless).
- [ ] **Step 3: GREEN** `npx vitest run`; `npx vue-tsc --noEmit`; `npm run build`.
- [ ] **Step 4: e2e.** Extend `scripts/e2e-fullstack.sh`: for a representative subset (e.g. postgres + mongo — one SQL, one NoSQL), create an instance with a seed template (`{engine, ttlHours:3, storageMb:256, initialData:"blog"}`; mssql storage 1024 if included) → query the seeded data via `POST /instances/{token}/query` (SQL `SELECT COUNT(*) FROM posts` → rows>0; mongo `print(db.getCollection('posts').countDocuments())` → output>0) → assert seeded → destroy. Reuse the existing per-engine `test_engine` machinery + the mongo `output` assertion branch.
- [ ] **Step 5: Run** `docker rm -f $(docker ps -aq --filter 'name=mayfly-') 2>/dev/null; bash scripts/e2e-fullstack.sh` → seeded-template create→query→destroy passes; no leftovers. Fix any real integration bug found.
- [ ] **Step 6: Commit** `git commit -m "feat(web): enable seed templates on all engines + seeded-template e2e"`

---

## Self-Review (completed)

**Spec coverage:** §2 catalog/mechanism → Task 1 (`SeedCatalog` + wiring). §3 portable authoring + Northwind port + Mongo variant → Tasks 1 (northwind.sql portable) + 2 (northwind.mongo.js) + the Global Constraints portable rules applied in every seed task. §4 provider wiring (pg GRANT / mysql init / mssql PostReadyExec / mongo 01-seed.js) → Tasks 1-2; validators relaxed → Task 1 (drop northwind-postgres rule) + each template task (add its id to both). §5 frontend → Task 6. §6 phasing = the 5 phases; per-engine seed tests → `SeedTemplateTests` (4 SQL) + `SeedTemplateMongoTests` (mongo), extended per template; e2e → Task 6.

**Placeholder scan:** The mechanism code (SeedCatalog, BuildSetup wiring, validators, mssql PostReadyExec append) is concrete. The seed DATA is specified as schema + portability constraints + a deterministic compact dataset the implementer authors, with the test asserting the authored row/doc count — this is data-authoring, not logic; a plan cannot and should not hand-write hundreds of INSERT rows, so it fixes the schema + constraints + the test contract (count matches what was authored + readable by appuser). The portable-SQL rules are exhaustive (types, PKs, dates, booleans, identifiers) in Global Constraints.

**Type consistency:** `SeedCatalog.Templates` / `IsTemplate` / `GetSql` / `GetMongoJs` are defined in Task 1 and consumed unchanged by every provider and every later template task (which only append an id to `Templates` + the validators). The `initialData` id strings (`northwind`/`ecommerce`/`blog`/`iot`) are used identically across the catalog, both validators, the seed resource filenames (`<id>.sql`/`<id>.mongo.js`), and the frontend. The provider wiring added in Tasks 1-2 is generic (`SeedCatalog.IsTemplate`), so Tasks 3-5 need no provider changes — confirmed by the file lists.

**Note (mssql seed delivery):** Task 1 appends the seed to the SQL Server `PostReadyExec` sqlcmd `-Q` string (after `GO / USE [appdb] / GO`). This relies on the seeds being COMPACT (they fit the docker-exec argv). If a future seed grows large, switch to a mounted seed file + `sqlcmd -i` — but the compact deterministic datasets specified here fit comfortably.
