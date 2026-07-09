# MayFly SQL Engines — Implementation Plan (Sub-project 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add MySQL, MariaDB, and SQL Server as provisionable engines, reapplying all SP6 hardening per engine, behind a clean engine abstraction.

**Architecture:** Introduce `IEngineProvider` (Provisioner: image/env/setup/readiness/hardening/credentials per engine) and `IEngineClient` (Api: ADO.NET connection factory + connection-string + size SQL + soft-enforce SQL per engine). Refactor the current Postgres-specific code into `PostgresEngineProvider`/`PostgresEngineClient` with NO behavior change (the existing 40 tests are the regression guard), then add MySQL, MariaDB, SQL Server providers/clients. All SP6 topology (internal network + socat sidecar, read-only rootfs, init-volume/writer, reconcile + orphan sweep, quota volume, labels) is reused unchanged; per-engine deviations live in the provider/client.

**Tech Stack:** .NET 8, Docker.DotNet.Enhanced, Npgsql (existing), MySqlConnector, Microsoft.Data.SqlClient, EF Core 8, xUnit/FluentAssertions/Moq/Testcontainers, Vue 3.

## Global Constraints

- **Runtime:** .NET 8 (`net8.0`). Only `MayFly.Provisioner` references `Docker.DotNet.Enhanced`; `MayFly.Api` references NO Docker package. Docker client `new DockerClientBuilder().Build()`; Testcontainers `new PostgreSqlBuilder("postgres:16-alpine").Build()` (and the MySql/MsSql Testcontainers builders where used).
- **Commit messages:** plain conventional-commit — NO `Co-Authored-By` and NO attribution/"Generated with"/"🤖" trailer.
- **Engines (validated verbatim):** `postgres` | `mysql` | `mariadb` | `mssql`. Images: `postgres:16-alpine`, `mysql:8.4`, `mariadb:11.4`, `mcr.microsoft.com/mssql/server:2022-latest`. Ports: 5432/3306/3306/1433.
- **Roles:** admin user is engine-specific (`mayflyadmin`/`root`/`sa`); app user is `appuser` (scoped, non-privileged — no FILE/SUPER for MySQL family; non-sysadmin, no xp_cmdshell for mssql). DB name `appdb`.
- **Initial data:** `blank` for all engines; `northwind` ONLY for `postgres` (validator + frontend gate it).
- **Hardening reused from SP6 per engine:** internal `mayfly-users` network + socat sidecar (no DB published port), read-only rootfs + tmpfs (EXCEPT mssql — `ReadonlyRootfs=false`), cap-drop ALL + minimal caps, no-new-privileges, mem/cpu/pids limits (mssql memory floor ~2GB), init-volume via writer (engines with `initdb.d`; mssql uses docker-exec), reconcile + `SweepOrphansAsync`, quota volume, `mayfly.instance`/`mayfly.role` labels.
- **Docker integration tests** join `[Collection("docker-sequential")]`. mssql tests need the Docker Desktop VM at ≥3–4 GB RAM.
- **Behavior-preserving refactor:** Phase 1 must keep the existing 40-test suite green.

---

## File Structure

```
MayFly.Provisioner/
  Engines/IEngineProvider.cs        # NEW: interface + EngineSetup/Credentials records
  Engines/PostgresEngineProvider.cs # NEW: extracted from DockerProvisioner (pg-specific)
  Engines/MySqlEngineProvider.cs    # NEW
  Engines/MariaDbEngineProvider.cs  # NEW (subclasses/reuses MySql)
  Engines/SqlServerEngineProvider.cs# NEW (docker-exec setup, mem floor, rootfs relaxed)
  Docker/DockerProvisioner.cs       # MODIFY: engine-agnostic, resolves IEngineProvider by EngineId
  Program.cs                        # MODIFY: register providers keyed by EngineId
MayFly.Api/
  Engines/IEngineClient.cs          # NEW: interface
  Engines/PostgresEngineClient.cs   # NEW: extracted (Npgsql, pg SQL)
  Engines/MySqlEngineClient.cs      # NEW (serves mysql + mariadb)
  Engines/SqlServerEngineClient.cs  # NEW
  Engines/EngineClientRegistry.cs   # NEW: resolve IEngineClient by engine id
  Services/QueryExecutor.cs         # MODIFY: generic ADO.NET via IEngineClient
  Lifecycle/QuotaEnforcer.cs        # MODIFY: SoftEnforce SQL from IEngineClient, admin=inst.AdminUser
  Lifecycle/LifecycleService.cs     # MODIFY: size SQL from IEngineClient
  Domain/Instance.cs                # MODIFY: add AdminUser + migration
  Dtos/InstanceDto.cs               # MODIFY: connection string via IEngineClient
  Validation/ApiSpecValidator.cs    # MODIFY: engines postgres|mysql|mariadb|mssql
  Program.cs                        # MODIFY: register engine clients
MayFly.Web/
  src/lib/snippets.ts               # MODIFY: engine-aware buildSnippets
  src/lib/engineLabels.ts           # NEW: engine -> label/glyph map
  src/components/EnginePicker.vue    # MODIFY: enable mysql/mariadb/mssql
  src/components/InitialDataPicker.vue# MODIFY: Northwind gated by engine
  src/views/InstanceView.vue        # MODIFY: engine label from map (fixes SP1 hardcode)
scripts/e2e-fullstack.sh            # MODIFY: per-engine e2e loop
```

---

## Phase 1 — Engine abstraction (behavior-preserving)

### Task 1: Define `IEngineProvider` + `IEngineClient` + records

**Files:** Create `MayFly.Provisioner/Engines/IEngineProvider.cs`, `MayFly.Api/Engines/IEngineClient.cs`. No behavior yet.

**Interfaces:**
- Produces (Provisioner):
```csharp
namespace MayFly.Provisioner.Engines;

public record EngineCredentials(string AdminUser, string AdminPassword, string AppUser, string AppPassword, string Db);
// Setup is EITHER init-script files (placed in the engine's initdb.d) OR a post-ready exec.
public record EngineSetup(IReadOnlyList<(string FileName, string Sql)> InitScripts, IReadOnlyList<string>? PostReadyExec);

public interface IEngineProvider
{
    string EngineId { get; }        // "postgres" | "mysql" | "mariadb" | "mssql"
    string Image { get; }
    int Port { get; }               // 5432 | 3306 | 3306 | 1433
    bool UsesInitVolume { get; }    // true = initdb.d via init-volume/writer; false = post-ready docker-exec (mssql)
    EngineCredentials GenerateCredentials();               // engine-compliant passwords
    IList<string> BuildEnv(EngineCredentials c);           // POSTGRES_*/MYSQL_*/MARIADB_*/MSSQL_*
    EngineSetup BuildSetup(EngineCredentials c, string initialData);
    IList<string> ReadinessExec(EngineCredentials c);      // docker-exec argv returning 0 when TCP-ready
    void ApplyHardening(Docker.DotNet.Models.HostConfig hc); // engine mem/rootfs/tmpfs/caps
}
```
- Produces (Api):
```csharp
namespace MayFly.Api.Engines;
using System.Data.Common;

public interface IEngineClient
{
    string EngineId { get; }
    DbConnection CreateConnection(string adoConnectionString);
    string BuildAdoConnectionString(string host, int port, string db, string user, string password);
    string BuildDisplayConnectionString(string host, int port, string db, string user, string password);
    string SizeQuerySql(string db);                    // returns bytes used
    string SoftEnforceReadOnlySql(string appUser, string db);
}
```

- [ ] **Step 1: Create the two interface files** (content above). No tests (interfaces only).
- [ ] **Step 2: Build** `dotnet build MayFly.sln` → 0 errors.
- [ ] **Step 3: Commit** `git commit -m "feat: engine abstraction interfaces (IEngineProvider, IEngineClient)"`

---

### Task 2: Extract `PostgresEngineProvider`; make `DockerProvisioner` engine-agnostic

**Files:** Create `MayFly.Provisioner/Engines/PostgresEngineProvider.cs`; Modify `Docker/DockerProvisioner.cs`, `Program.cs`.

**Interfaces:**
- Consumes: `IEngineProvider`, `EngineCredentials`, `EngineSetup`.
- Produces: `PostgresEngineProvider : IEngineProvider` (EngineId `postgres`, Image `postgres:16-alpine`, Port 5432, UsesInitVolume true). `DockerProvisioner` resolves the provider by `CreateInstanceRequest.Engine` from an injected `IReadOnlyDictionary<string, IEngineProvider>` (or `IEnumerable<IEngineProvider>` keyed by EngineId).

- [ ] **Step 1: Regression baseline** — run the full suite once and record it green:
  Run: `dotnet test MayFly.Tests` → expected all pass (the 40-test baseline). This is the behavior-preserving guard.
- [ ] **Step 2: Move pg-specifics into `PostgresEngineProvider`** — extract from `DockerProvisioner`, verbatim, into the provider methods:
  - `Image` = `"postgres:16-alpine"`, `Port` = 5432, `UsesInitVolume` = true.
  - `GenerateCredentials()` — the existing CSPRNG hex password generation, admin user `"mayflyadmin"`, app user `"appuser"`, db `"appdb"`.
  - `BuildEnv(c)` — `["POSTGRES_USER=" + c.AdminUser, "POSTGRES_PASSWORD=" + c.AdminPassword, "POSTGRES_DB=" + c.Db]`.
  - `BuildSetup(c, initialData)` — the existing `00-roles.sql` (CREATE ROLE appuser NOSUPERUSER … + grants + extensions) and, when `initialData=="northwind"`, `01-seed.sql` (embedded northwind + GRANTs). `PostReadyExec` = null.
  - `ReadinessExec(c)` — `["pg_isready","-h","127.0.0.1","-U",c.AdminUser,"-q"]`.
  - `ApplyHardening(hc)` — set `ReadonlyRootfs=true`, the 3 tmpfs, Memory/NanoCPUs/PidsLimit, CapDrop/CapAdd, SecurityOpt (the existing pg values).
- [ ] **Step 3: Make `DockerProvisioner` delegate** — replace the hardcoded pg constants/env/setup/readiness/hardening in `CreateAsync` with calls to the resolved `IEngineProvider` for `req.Engine`. Keep the init-volume/writer path when `provider.UsesInitVolume`, else run `provider`'s `PostReadyExec` via docker-exec after readiness (not exercised until mssql). `Program.cs`: register `AddSingleton<IEngineProvider, PostgresEngineProvider>()` and inject the set into `DockerProvisioner` (keyed by EngineId).
- [ ] **Step 4: Regression** — run the full suite: `dotnet test MayFly.Tests` → **all 40 still green** (no behavior change). If any pg test fails, the extraction drifted — fix until green.
- [ ] **Step 5: Commit** `git commit -m "refactor(provisioner): extract PostgresEngineProvider; DockerProvisioner engine-agnostic"`

---

### Task 3: Extract `PostgresEngineClient`; Api engine-aware; persist `AdminUser`

**Files:** Create `MayFly.Api/Engines/PostgresEngineClient.cs`, `Engines/EngineClientRegistry.cs`; Modify `Services/QueryExecutor.cs`, `Lifecycle/QuotaEnforcer.cs`, `Lifecycle/LifecycleService.cs`, `Dtos/InstanceDto.cs`, `Domain/Instance.cs` (+migration), `Program.cs`.

**Interfaces:**
- Produces: `PostgresEngineClient : IEngineClient` (Npgsql `CreateConnection`; `postgresql://` display + `Host=…` ADO string; `SELECT pg_database_size(current_database())` size; `ALTER ROLE {appUser} SET default_transaction_read_only=on` soft-enforce). `EngineClientRegistry.For(engineId) : IEngineClient`. `Instance.AdminUser` (string).

- [ ] **Step 1: Regression baseline** — `dotnet test MayFly.Tests` green.
- [ ] **Step 2: Add `Instance.AdminUser`** + EF migration:
  `Domain/Instance.cs`: `public string AdminUser { get; set; } = "";`
  Run `dotnet ef migrations add AdminUser --project MayFly.Api`. `InstanceService.CreateAsync` stores `AdminUser = prov.AdminUser` (the `CreateInstanceResult`/`ProvisionResult` already carry `AdminUser` from SP6 Task 4 — confirm; if only AdminPassword is carried, add `AdminUser` to the contract + client mirror).
- [ ] **Step 3: Extract `PostgresEngineClient`** — move the Npgsql connection build + pg size SQL + pg soft-enforce SQL + the `postgresql://user:pass@host:port/db` display builder out of `QueryExecutor`/`QuotaEnforcer`/`LifecycleService`/`InstanceDto` into `PostgresEngineClient`. Create `EngineClientRegistry` (resolve by engine id, register postgres). 
- [ ] **Step 4: Rewire the consumers** to resolve `IEngineClient` by `inst.Engine`:
  - `QueryExecutor.ExecuteAsync`: `var client = registry.For(inst.Engine); await using var conn = client.CreateConnection(client.BuildAdoConnectionString(host, port, inst.DbName, inst.DbUser, secrets.Unprotect(inst.DbPasswordEnc)));` then the SAME generic ADO.NET read loop (cap 500, timeout, error capture, re-throw cancellation) using `DbCommand`/`DbDataReader`.
  - `QuotaEnforcer`: connect as admin using `inst.AdminUser` + `secrets.Unprotect(inst.AdminPasswordEnc)`; run `registry.For(inst.Engine).SoftEnforceReadOnlySql(inst.DbUser, inst.DbName)`.
  - `LifecycleService` size monitor: run `registry.For(inst.Engine).SizeQuerySql(inst.DbName)`.
  - `InstanceDto.From`: build the display connection string via `registry.For(i.Engine).BuildDisplayConnectionString(...)`.
  `Program.cs`: register `PostgresEngineClient` + `EngineClientRegistry`.
- [ ] **Step 5: Regression** — `dotnet test MayFly.Tests` → **all green** (behavior-preserving; the connection strings + size + soft-enforce for postgres must be byte-identical to before).
- [ ] **Step 6: Commit** `git commit -m "refactor(api): extract PostgresEngineClient; engine-aware query/enforce/dto; persist AdminUser"`

---

## Phase 2 — MySQL

### Task 4: `MySqlEngineProvider` (provisioning)

**Files:** Create `MayFly.Provisioner/Engines/MySqlEngineProvider.cs`; Modify `Program.cs`; Test `MayFly.Tests/Provisioner/MySqlEngineTests.cs`.

**Interfaces:** Produces `MySqlEngineProvider : IEngineProvider` (EngineId `mysql`, Image `mysql:8.4`, Port 3306, UsesInitVolume true).

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class MySqlEngineTests
{
    [Fact]
    public async Task Mysql_appuser_scoped_no_file_and_reachable()
    {
        // Provision a mysql instance via DockerProvisioner.CreateAsync(engine "mysql", 3, 256, "blank").
        // Connect as appuser via the sidecar published port (MySqlConnector):
        //  - SELECT CURRENT_USER() -> appuser@...
        //  - SHOW GRANTS shows no FILE / no SUPER (scoped to appdb)
        //  - CREATE TABLE t(x int) + INSERT works (legit DDL/DML on appdb)
        //  - LOAD DATA INFILE / SELECT ... INTO OUTFILE is REJECTED (no FILE privilege)
        // Egress probe from the DB container fails (reuse the SP6 EgressTests probe).
        // Destroy cleans up DB + sidecar.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter MySqlEngineTests` (provider not registered).
- [ ] **Step 3: Implement `MySqlEngineProvider`**
```csharp
public sealed class MySqlEngineProvider : IEngineProvider
{
    public string EngineId => "mysql";
    public string Image => "mysql:8.4";
    public int Port => 3306;
    public bool UsesInitVolume => true;
    public EngineCredentials GenerateCredentials() => /* hex passwords, admin "root", app "appuser", db "appdb" */;
    public IList<string> BuildEnv(EngineCredentials c) => new List<string> {
        $"MYSQL_ROOT_PASSWORD={c.AdminPassword}", $"MYSQL_DATABASE={c.Db}" };
    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        var roles = $@"
CREATE USER '{c.AppUser}'@'%' IDENTIFIED BY '{Escape(c.AppPassword)}';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, DROP, INDEX, ALTER, REFERENCES ON `{c.Db}`.* TO '{c.AppUser}'@'%';
FLUSH PRIVILEGES;";
        // blank only in SP2 (no northwind for mysql)
        return new EngineSetup(new[] { ("00-roles.sql", roles) }, PostReadyExec: null);
    }
    public IList<string> ReadinessExec(EngineCredentials c) =>
        new[] { "mysqladmin", "ping", "-h", "127.0.0.1", "-uroot", $"-p{c.AdminPassword}" };
    public void ApplyHardening(HostConfig hc) { /* ReadonlyRootfs=true; tmpfs /var/run/mysqld,/tmp,/run; mem 512MB; caps as pg */ }
}
```
Register in `Program.cs`: `AddSingleton<IEngineProvider, MySqlEngineProvider>()`.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter MySqlEngineTests`.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): MySQL engine provider (hardened, non-FILE appuser)"`

---

### Task 5: `MySqlEngineClient` (query + soft-enforce)

**Files:** Create `MayFly.Api/Engines/MySqlEngineClient.cs`; Modify `Program.cs`, `EngineClientRegistry`; Test `MayFly.Tests/Api/MySqlEngineClientTests.cs`.

**Interfaces:** Produces `MySqlEngineClient : IEngineClient` (EngineId `mysql`; `MySqlConnector`).

- [ ] **Step 1: Write the failing integration test** — provision a mysql instance; via `QueryExecutor` run `SELECT 1` (success, columns/rows) and a bad query (Success=false, Error non-empty); then `QuotaEnforcer.EnforceAsync(inst, over-quota)` → connect as appuser and assert an INSERT is REJECTED (write revoked).
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter MySqlEngineClientTests`.
- [ ] **Step 3: Implement `MySqlEngineClient`**
```csharp
public sealed class MySqlEngineClient : IEngineClient
{
    public virtual string EngineId => "mysql";
    public DbConnection CreateConnection(string cs) => new MySqlConnector.MySqlConnection(cs);
    public string BuildAdoConnectionString(string h, int p, string db, string u, string pw) =>
        new MySqlConnector.MySqlConnectionStringBuilder { Server=h, Port=(uint)p, Database=db, UserID=u, Password=pw, ConnectionTimeout=5 }.ToString();
    public string BuildDisplayConnectionString(string h, int p, string db, string u, string pw) =>
        $"mysql://{u}:{pw}@{h}:{p}/{db}";
    public string SizeQuerySql(string db) =>
        $"SELECT COALESCE(SUM(data_length+index_length),0) FROM information_schema.tables WHERE table_schema='{db}'";
    public string SoftEnforceReadOnlySql(string appUser, string db) =>
        $"REVOKE INSERT, UPDATE, DELETE, CREATE, DROP, ALTER, INDEX ON `{db}`.* FROM '{appUser}'@'%'; FLUSH PRIVILEGES;";
}
```
Add `dotnet add MayFly.Api package MySqlConnector`. Register in `Program.cs` + `EngineClientRegistry` (mysql).
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter MySqlEngineClientTests`.
- [ ] **Step 5: Commit** `git commit -m "feat(api): MySQL engine client (MySqlConnector, size, soft-enforce)"`

---

## Phase 3 — MariaDB

### Task 6: MariaDB provider + client reuse

**Files:** Create `MayFly.Provisioner/Engines/MariaDbEngineProvider.cs`; Modify `Program.cs`, `EngineClientRegistry`; Test `MayFly.Tests/Provisioner/MariaDbEngineTests.cs`.

**Interfaces:** Produces `MariaDbEngineProvider : IEngineProvider` (EngineId `mariadb`, Image `mariadb:11.4`, Port 3306). `MySqlEngineClient` serves `mariadb` too (registry maps both).

- [ ] **Step 1: Write the failing integration test** — provision a `mariadb` instance; connect as appuser via `MySqlConnector`, assert appuser scoped (no FILE), `SELECT 1` works, egress blocked; destroy cleans up.
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter MariaDbEngineTests`.
- [ ] **Step 3: Implement `MariaDbEngineProvider`** — reuse MySQL logic (subclass or share a base): `EngineId "mariadb"`, `Image "mariadb:11.4"`; `BuildEnv` uses `MARIADB_ROOT_PASSWORD`/`MARIADB_DATABASE`; `BuildSetup` identical role SQL (MariaDB accepts the same `CREATE USER`/`GRANT`); `ReadinessExec` = `["mariadb-admin","ping","-h","127.0.0.1","-uroot","-p"+c.AdminPassword]`; `ApplyHardening` same as MySQL. Register `AddSingleton<IEngineProvider, MariaDbEngineProvider>()`. In `EngineClientRegistry`, map `mariadb` → the `MySqlEngineClient` instance (same driver/SQL). `ApiSpecValidator` already accepts mariadb (Task 9). 
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter MariaDbEngineTests`.
- [ ] **Step 5: Commit** `git commit -m "feat: MariaDB engine (reuses MySQL client)"`

---

## Phase 4 — SQL Server

### Task 7: `SqlServerEngineProvider` (docker-exec setup, mem floor, rootfs relaxed)

**Files:** Create `MayFly.Provisioner/Engines/SqlServerEngineProvider.cs`; Modify `Program.cs`, `DockerProvisioner.cs` (post-ready exec path), Test `MayFly.Tests/Provisioner/SqlServerEngineTests.cs`.

**Interfaces:** Produces `SqlServerEngineProvider : IEngineProvider` (EngineId `mssql`, Image `mcr.microsoft.com/mssql/server:2022-latest`, Port 1433, `UsesInitVolume=false`).

- [ ] **Step 1: Write the failing integration test** (heavy — mssql ~2GB, slow start)
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class SqlServerEngineTests
{
    [Fact(Timeout = 180000)]
    public async Task Mssql_appuser_not_sysadmin_no_xp_cmdshell_and_reachable()
    {
        // Provision an mssql instance (blank). Wait (slow start).
        // Connect as appuser via the sidecar port (Microsoft.Data.SqlClient, TrustServerCertificate=True):
        //  - SELECT IS_SRVROLEMEMBER('sysadmin') -> 0 (not sysadmin)
        //  - CREATE TABLE t(x int) + INSERT works on appdb
        //  - EXEC xp_cmdshell 'whoami' -> throws (permission denied / disabled)
        // Egress probe from the DB container fails. Destroy cleans up DB + sidecar.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter SqlServerEngineTests`.
- [ ] **Step 3: Implement `SqlServerEngineProvider`** + the DockerProvisioner post-ready exec path
```csharp
public sealed class SqlServerEngineProvider : IEngineProvider
{
    public string EngineId => "mssql";
    public string Image => "mcr.microsoft.com/mssql/server:2022-latest";
    public int Port => 1433;
    public bool UsesInitVolume => false;   // no initdb.d -> docker-exec setup
    public EngineCredentials GenerateCredentials()
    {
        // SQL Server SA password policy: >=8 chars, 3 of 4 categories. hex + strong suffix.
        string strong() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + "Aa1!";
        return new EngineCredentials("sa", strong(), "appuser", strong(), "appdb");
    }
    public IList<string> BuildEnv(EngineCredentials c) =>
        new List<string> { "ACCEPT_EULA=Y", $"MSSQL_SA_PASSWORD={c.AdminPassword}" };
    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        var sql =
$@"IF DB_ID('{c.Db}') IS NULL CREATE DATABASE [{c.Db}];
CREATE LOGIN [{c.AppUser}] WITH PASSWORD='{c.AppPassword.Replace("'","''")}', CHECK_POLICY=OFF;
USE [{c.Db}];
CREATE USER [{c.AppUser}] FOR LOGIN [{c.AppUser}];
ALTER ROLE db_datareader ADD MEMBER [{c.AppUser}];
ALTER ROLE db_datawriter ADD MEMBER [{c.AppUser}];
ALTER ROLE db_ddladmin  ADD MEMBER [{c.AppUser}];";
        // PostReadyExec: run the above via sqlcmd inside the container (admin = sa)
        return new EngineSetup(Array.Empty<(string,string)>(),
            PostReadyExec: new[] { "/opt/mssql-tools18/bin/sqlcmd","-C","-S","127.0.0.1","-U","sa","-P",c.AdminPassword,"-Q", sql });
    }
    public IList<string> ReadinessExec(EngineCredentials c) =>
        new[] { "/opt/mssql-tools18/bin/sqlcmd","-C","-S","127.0.0.1","-U","sa","-P",c.AdminPassword,"-Q","SELECT 1" };
    public void ApplyHardening(HostConfig hc)
    {
        hc.ReadonlyRootfs = false;                 // mssql runtime write paths not predictable
        hc.Memory = 2L * 1024 * 1024 * 1024;       // ~2GB floor (mssql min)
        hc.NanoCPUs = 1_000_000_000;               // 1 CPU
        hc.PidsLimit = 500;
        // keep CapDrop=ALL + minimal, no-new-privileges (mssql image runs as non-root 'mssql')
    }
}
```
In `DockerProvisioner`: when `!provider.UsesInitVolume`, after readiness, run `provider.BuildSetup(...).PostReadyExec` via `docker exec` (create exec, start, poll exit code == 0). The `BuildSetup` for init-volume engines still uses the init-volume/writer path. Register `AddSingleton<IEngineProvider, SqlServerEngineProvider>()`.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter SqlServerEngineTests` (needs Docker VM ≥3–4GB; generous timeout).
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): SQL Server engine provider (docker-exec setup, mem floor, non-sysadmin appuser)"`

---

### Task 8: `SqlServerEngineClient` (query + soft-enforce)

**Files:** Create `MayFly.Api/Engines/SqlServerEngineClient.cs`; Modify `Program.cs`, `EngineClientRegistry`; Test `MayFly.Tests/Api/SqlServerEngineClientTests.cs`.

**Interfaces:** Produces `SqlServerEngineClient : IEngineClient` (EngineId `mssql`; `Microsoft.Data.SqlClient`).

- [ ] **Step 1: Write the failing integration test** — provision mssql; via `QueryExecutor` run `SELECT 1` (success) + a bad query (Success=false); then `QuotaEnforcer.EnforceAsync(over-quota)` → an appuser INSERT is REJECTED (db read-only).
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter SqlServerEngineClientTests`.
- [ ] **Step 3: Implement `SqlServerEngineClient`**
```csharp
public sealed class SqlServerEngineClient : IEngineClient
{
    public string EngineId => "mssql";
    public DbConnection CreateConnection(string cs) => new Microsoft.Data.SqlClient.SqlConnection(cs);
    public string BuildAdoConnectionString(string h, int p, string db, string u, string pw) =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder {
            DataSource=$"{h},{p}", InitialCatalog=db, UserID=u, Password=pw,
            TrustServerCertificate=true, ConnectTimeout=5 }.ToString();
    public string BuildDisplayConnectionString(string h, int p, string db, string u, string pw) =>
        $"Server={h},{p};Database={db};User Id={u};Password={pw};TrustServerCertificate=True";
    public string SizeQuerySql(string db) =>
        $"SELECT CAST(COALESCE(SUM(size),0) AS BIGINT)*8192 FROM sys.master_files WHERE database_id=DB_ID('{db}')";
    public string SoftEnforceReadOnlySql(string appUser, string db) =>
        $"ALTER DATABASE [{db}] SET READ_ONLY WITH ROLLBACK IMMEDIATE;";
}
```
Add `dotnet add MayFly.Api package Microsoft.Data.SqlClient`. Register + registry map `mssql`.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter SqlServerEngineClientTests`.
- [ ] **Step 5: Commit** `git commit -m "feat(api): SQL Server engine client (Microsoft.Data.SqlClient, size, read-only soft-enforce)"`

---

## Phase 5 — Frontend + validator + e2e

### Task 9: Api validator + frontend engine enablement + Northwind gating + label map

**Files:** Modify `MayFly.Api/Validation/ApiSpecValidator.cs`; Create `MayFly.Web/src/lib/engineLabels.ts`; Modify `EnginePicker.vue`, `InitialDataPicker.vue`, `InstanceView.vue`; Test `MayFly.Web/src/components/EnginePicker.test.ts`, `MayFly.Tests/Api/...` for the validator.

- [ ] **Step 1: Validator test (RED)** — `ApiSpecValidator.Validate` accepts `mysql`/`mariadb`/`mssql` (+ postgres), rejects others; `northwind` only allowed with `postgres` (a `{engine:"mysql", initialData:"northwind"}` → invalid).
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter ApiSpecValidator`.
- [ ] **Step 3: Implement validator** — engines set `{postgres,mysql,mariadb,mssql}`; initialData rule: `blank` always; `northwind` only when engine==postgres. GREEN.
- [ ] **Step 4: Frontend** — `engineLabels.ts` exports `{postgres:"PostgreSQL", mysql:"MySQL", mariadb:"MariaDB", mssql:"SQL Server"}` (+ glyph/color from the design data). `EnginePicker.vue`: enable mysql/mariadb/mssql (mongo stays disabled). `InitialDataPicker.vue`: accept an `engine` prop; Northwind option `enabled` only when `engine==='postgres'`. `InstanceView.vue`: replace the hardcoded "PostgreSQL" with `engineLabels[inst.engine]` (fixes SP1 minor). `npx vue-tsc --noEmit` + `npm run build` green.
- [ ] **Step 5: EnginePicker test** — mount, assert mysql/mariadb/mssql are selectable and mongo disabled; InitialDataPicker: Northwind disabled when engine≠postgres. `npx vitest run`.
- [ ] **Step 6: Commit** `git commit -m "feat: enable SQL engines in API validator + wizard (Northwind gated, engine label map)"`

---

### Task 10: Engine-aware connection snippets

**Files:** Modify `MayFly.Web/src/lib/snippets.ts`; Test `MayFly.Web/src/lib/snippets.test.ts`.

- [ ] **Step 1: Tests (RED)** — `buildSnippets(inst)` for a mysql inst asserts bash uses `mysql`, python `pymysql`/`mysql.connector`, node `mysql2`, go `go-sql-driver/mysql`, dotnet `MySqlConnector`; for an mssql inst asserts bash `sqlcmd`, python `pyodbc`, node `mssql`, go `denisenkom/go-mssqldb`, dotnet `Microsoft.Data.SqlClient`; each contains the real host/port/db/user. Postgres snippets unchanged.
- [ ] **Step 2: RED** `npx vitest run src/lib/snippets.test.ts`.
- [ ] **Step 3: Implement** — `buildSnippets` dispatches on `inst.engine`: parse the engine's connection string (mysql `Server=…`/`mysql://`; mssql `Server=h,p;…`), then emit the 5-language snippets per engine (postgres unchanged). GREEN + `npm run build`.
- [ ] **Step 4: Commit** `git commit -m "feat(web): engine-aware connection snippets (mysql/mariadb/mssql)"`

---

### Task 11: Full-stack e2e per engine + docs

**Files:** Modify `scripts/e2e-fullstack.sh`, `SECURITY.md` (engine list).

- [ ] **Step 1: Clean docker state**, then run the full backend suite `dotnet test MayFly.Tests` (all green) + `cd MayFly.Web && npx vitest run` (green).
- [ ] **Step 2: Extend `scripts/e2e-fullstack.sh`** — loop `for engine in postgres mysql mariadb mssql`: create via `POST /api/instances {engine, ttlHours:3, storageMb:256, initialData:"blank"}` (mssql storage may need 512+) → wait ready → connect via the sidecar published port using the engine's client (or query via `POST /instances/{token}/query {sql:"SELECT 1"}`) → assert success → egress probe from the DB fails → destroy → confirm cleanup. (mssql: allow a longer timeout; ensure the Docker VM has ≥3–4GB.)
- [ ] **Step 3: Run the e2e** `bash scripts/e2e-fullstack.sh` → each engine passes create→connect→query→destroy through Caddy; no leftovers.
- [ ] **Step 4: Update `SECURITY.md`** — engine list now postgres/mysql/mariadb/mssql; note the mssql deviations (2GB floor, read-only rootfs relaxed, docker-exec setup).
- [ ] **Step 5: Commit** `git commit -m "test: per-engine full-stack e2e + SECURITY.md engine list"`

---

## Self-Review (completed)

**Spec coverage:** §2 abstraction → Tasks 1–3 (interfaces + Postgres extraction both sides + AdminUser). §3 MySQL → Tasks 4–5; MariaDB → Task 6; SQL Server → Tasks 7–8. §4 mssql deviations (password/exec/mem/rootfs) → Task 7. §5 frontend (enable/gate/snippets/label) → Tasks 9–10. §6 phasing = the 5 phases. §7 tests → per-task Docker integration + Task 11 e2e; the 40-test regression guard is Tasks 2/3 Step-1 baselines + Step-4/5 reruns. §2.2 AdminUser persistence → Task 3.

**Placeholder scan:** Engine-specific code (env, role SQL, readiness, size, soft-enforce, connection strings, snippets) is concrete. The behavior-preserving refactor tasks (2,3) describe the extraction of NAMED existing code into the provider/client with the 40-test rerun as the gate — the "code" there is the interface impl + the move, not new logic. Integration tests that drive real Docker containers are specified by their exact driver calls + assertions (the implementer writes the literal test), as in SP6.

**Type consistency:** `IEngineProvider`/`IEngineClient` signatures are used identically across all providers/clients; `EngineCredentials`/`EngineSetup` records are shared; `Instance.AdminUser` consumed by `QuotaEnforcer`; `EngineClientRegistry.For(engineId)` used by QueryExecutor/QuotaEnforcer/LifecycleService/InstanceDto; `UsesInitVolume` drives the DockerProvisioner init-volume-vs-exec branch consistently (postgres/mysql/mariadb true, mssql false).

**Note (mssql resource):** Task 7/8/11 require the Docker Desktop VM at ≥3–4GB; if unavailable, those tasks report the environment limit rather than weakening the control.
