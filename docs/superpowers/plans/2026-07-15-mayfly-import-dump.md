# MayFly Import Dump — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user upload a text dump (`.sql` for SQL engines, `.js` for Mongo) and have their database restored from it, on all five engines.

**Architecture:** A new Provisioner `ExecDumpAsync` channel (mirrors `ExecMongoshAsync`) runs the engine's native client (`psql`/`mysql`/`sqlcmd`/`mongosh`) against the uploaded dump **inside the user's disposable container as admin**. The API exposes `POST /instances/{token}/import` (multipart, size-capped) that decrypts the admin credential and calls the channel via `IProvisionerClient`; it never parses the dump. The wizard orchestrates create(blank)→import.

**Tech Stack:** .NET 8, Docker.DotNet.Enhanced, native DB clients (in each engine image), xUnit/FluentAssertions/Testcontainers, `Npgsql`/`MySqlConnector`/`Microsoft.Data.SqlClient`/`MongoDB.Driver` (tests only), Vue 3.

## Global Constraints

- **Runtime:** .NET 8. Only `MayFly.Provisioner` references `Docker.DotNet.Enhanced`; `MayFly.Api` references no Docker package and no `MongoDB.Driver`. DB drivers for connecting in tests are already in `MayFly.Tests`.
- **Commit messages:** plain conventional-commit — NO `Co-Authored-By`, NO attribution/"Generated with"/"🤖" trailer. Commit with `git -c user.name='fanfan' -c user.email='fazemachurui@outlook.fr' commit -m "..."`.
- **Engines:** `postgres|mysql|mariadb|mssql|mongo`. Dump file: `.sql` for the four SQL engines, `.js` for mongo (text dumps only — binary `pg_dump -Fc`/BSON out of scope). Admin user is engine-specific (`mayflyadmin`/`root`/`sa`/`mayflyadmin`); DB `appdb`.
- **Role:** the dump runs as the DB ADMIN inside the container (fidelity). Untrusted execution is confined to the user's disposable, egress-blocked, read-only-rootfs (mssql relaxed), cpu/pids-limited, TTL-bounded container — NEVER on the API.
- **Size cap:** uploads capped at **16 MiB** (configurable) — under the container `/tmp` tmpfs (64 MiB). Over-cap → HTTP 413.
- **Guardrails:** container-side `timeout` + outer CTS; output capped via `CappedStream`; `X-Provisioner-Key` on the exec-dump call; per-IP rate limit on `/import`. Restored data counts against the volume quota (SP6 soft-enforce applies).
- **`success` semantics:** `success = exitCode == 0` (the client process completed). Per-statement dump errors appear in the returned `output`/`error` text for the user to read (the SQL clients run in continue-on-error mode; `success` means "the import ran", not "zero errors").
- **Docker integration tests** join `[Collection("docker-sequential")]`. mongo/mysql/mariadb arm64-native (fast); mssql emulated (slow, generous timeouts). Clean `docker rm -f $(docker ps -aq --filter 'name=mayfly-')` before full-suite runs.

---

## File Structure

```
MayFly.Provisioner/
  Docker/DockerProvisioner.cs       # MODIFY: ExecDumpAsync (per-engine client + /tmp delivery), reuse ExecCaptureAsync
  Docker/IDockerProvisioner.cs      # MODIFY: add ExecDumpAsync
  Endpoints/ProvisionerEndpoints.cs # MODIFY: MapPost /instances/{containerId}/exec-dump
  Contracts/InstanceSpec.cs         # MODIFY: ExecDumpRequest/Result records
MayFly.Api/
  Import/IDumpImporter.cs           # NEW
  Import/DumpImporter.cs            # NEW: decrypt admin creds + call ExecDumpAsync
  Provisioning/IProvisionerClient.cs+ProvisionerClient.cs # MODIFY: add ExecDumpAsync
  Provisioning/ProvisionerDtos.cs   # MODIFY: ExecDumpRequest/Result records
  Dtos/ImportResultDto.cs           # NEW: { success, output, error, truncated, ms }
  Controllers/InstancesController.cs # MODIFY: POST {token}/import (multipart, cap, GetByTokenAsync, rate-limit)
  Program.cs                        # MODIFY: register DumpImporter + "import" rate-limit policy + multipart size limit
MayFly.Web/
  src/api/instances.ts              # MODIFY: importDump(token, file)
  src/api/types.ts                  # MODIFY: ImportResultDto
  src/components/InitialDataPicker.vue # MODIFY: enable dump + file input
  src/views/NewView.vue             # MODIFY: create(blank)->importDump orchestration + result display
scripts/e2e-fullstack.sh            # MODIFY: import case
SECURITY.md                         # MODIFY: import/exec-dump security model
```

---

## Phase 1 — exec-dump channel (Provisioner)

### Task 1: `ExecDumpAsync` + endpoint

**Files:** Modify `MayFly.Provisioner/Docker/DockerProvisioner.cs`, `Docker/IDockerProvisioner.cs`, `Endpoints/ProvisionerEndpoints.cs`, `Contracts/InstanceSpec.cs`; Test `MayFly.Tests/Provisioner/ExecDumpTests.cs`.

**Interfaces:**
- Produces: `Task<ExecDumpResult> ExecDumpAsync(string containerId, ExecDumpRequest req, CancellationToken ct)` where
  ```csharp
  public record ExecDumpRequest(string Engine, string DumpContent, string AdminUser, string AdminPassword, string Db, int TimeoutSeconds, int MaxOutputBytes);
  public record ExecDumpResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
  ```
- Endpoint `POST /instances/{containerId}/exec-dump` (the global `X-Provisioner-Key` middleware guards it).

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class ExecDumpTests
{
    [Theory(Timeout = 240000)]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    [InlineData("mongo")]
    public async Task Dump_restores_data_as_admin(string engine)
    {
        // Provision engine (blank). Build a small dump for that engine:
        //   SQL engines: "CREATE TABLE imp (id INT PRIMARY KEY, name VARCHAR(50)); INSERT INTO imp (id,name) VALUES (1,'x'),(2,'y');"
        //   mongo (.js): "db.getSiblingDB('appdb').getCollection('imp').insertMany([{_id:1,name:'x'},{_id:2,name:'y'}]);"
        // Call DockerProvisioner.ExecDumpAsync(containerId, new ExecDumpRequest(engine, dump, adminUser, adminPassword, "appdb", 60, 256*1024)).
        //   -> ExitCode 0. Then connect (SQL: as appuser or admin via the driver; mongo: appuser via MongoDB.Driver) and assert the restored data:
        //   SQL: SELECT COUNT(*) FROM imp == 2 ; mongo: db.getCollection('imp').CountDocuments == 2.
        // finally destroy.
    }

    [Fact(Timeout = 180000)]
    public async Task Postgres_dump_can_run_admin_only_statements()
    {
        // Provision postgres. Dump = "CREATE EXTENSION IF NOT EXISTS pg_trgm;\nCREATE TABLE ext_t(id int);\nINSERT INTO ext_t VALUES (1);"
        // ExecDumpAsync -> ExitCode 0 (admin can CREATE EXTENSION — proves admin role). SELECT COUNT(*) FROM ext_t == 1. destroy.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter ExecDumpTests`.
- [ ] **Step 3: Implement.**
  - Records in `Contracts/InstanceSpec.cs` (per Interfaces).
  - `ExecDumpAsync` (mirror `ExecMongoshAsync` ~L478; reuse `ExecCaptureAsync` + `CappedStream` + the outer CTS; reuse the tar helpers `WriteTarEntry`/`TarWriter` for delivery):
    1. **Deliver the dump** to `/tmp/dump.<ext>` (`ext` = `sql` for postgres/mysql/mariadb/mssql, `js` for mongo): build an in-memory tar containing one entry `dump.<ext>` with `req.DumpContent`, then `await _docker.Containers.ExtractArchiveToContainerAsync(containerId, new CopyToContainerParameters { Path = "/tmp" }, tarStream, ct)`.
    2. **Build the client command** (`sh -c "timeout {secs} <client...>"`), admin password OFF the argv via env where the client supports it:
       - postgres: env `PGPASSWORD=<pwd>`; cmd `timeout {secs} psql -h 127.0.0.1 -U {AdminUser} -d {Db} -f /tmp/dump.sql`
       - mysql/mariadb: env `MYSQL_PWD=<pwd>`; cmd `timeout {secs} mysql -h 127.0.0.1 -u {AdminUser} {Db} < /tmp/dump.sql`
       - mssql: env `SQLCMDPASSWORD=<pwd>`; cmd `timeout {secs} /opt/mssql-tools18/bin/sqlcmd -C -S 127.0.0.1 -U {AdminUser} -d {Db} -i /tmp/dump.sql`
       - mongo: cmd `timeout {secs} mongosh --quiet --host 127.0.0.1 -u {AdminUser} -p "$MONGO_PWD" --authenticationDatabase admin {Db} --file /tmp/dump.js` (env `MONGO_PWD=<pwd>`, referenced from a double-quoted `sh -c` expansion — no argv exposure, same pattern as ExecMongoshAsync)
       Clamp `TimeoutSeconds` to `[1,120]`. Validate `req.AdminUser`/`req.Db` against `^[A-Za-z0-9_]+$` before interpolation (same defense-in-depth as ExecMongoshAsync). The dump CONTENT is never interpolated into the shell — it lives only in the `/tmp` file.
    3. `var (exit, outStr, errStr, trunc) = await ExecCaptureAsync(containerId, new List<string>{"sh","-c",sh}, env, Math.Clamp(req.MaxOutputBytes,1,1024*1024), outerCts.Token);` return `new ExecDumpResult(outStr, errStr, exit, trunc, ms)`.
  - `IDockerProvisioner.ExecDumpAsync` signature.
  - `ProvisionerEndpoints.cs`: `app.MapPost("/instances/{containerId}/exec-dump", async (string containerId, ExecDumpRequest req, IDockerProvisioner p, CancellationToken ct) => Results.Ok(await p.ExecDumpAsync(containerId, req, ct)));`
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter ExecDumpTests` (5 engines + admin-proof); then full suite once (clean `mayfly-*` first).
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): exec-dump channel (native client restore as admin, all engines)"`

---

## Phase 2 — API import endpoint

### Task 2: `POST /instances/{token}/import` + `DumpImporter`

**Files:** Create `MayFly.Api/Import/IDumpImporter.cs`, `Import/DumpImporter.cs`, `Dtos/ImportResultDto.cs`; Modify `Provisioning/IProvisionerClient.cs`, `ProvisionerClient.cs`, `ProvisionerDtos.cs`, `Controllers/InstancesController.cs`, `Program.cs`, `MayFly.Web/src/api/types.ts`; Test `MayFly.Tests/Api/ImportEndpointTests.cs`.

**Interfaces:**
- Produces: `IProvisionerClient.ExecDumpAsync(string containerId, ExecDumpRequest req, CancellationToken ct)` (Api mirror records in `ProvisionerDtos.cs`). `IDumpImporter.ImportAsync(Instance inst, string dumpContent, CancellationToken ct) : Task<ImportResultDto>`. `ImportResultDto(bool Success, string Output, string? Error, bool Truncated, int Ms)`.

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class ImportEndpointTests
{
    [Fact(Timeout = 120000)]
    public async Task Import_restores_dump_via_DumpImporter()
    {
        // Provision a postgres instance via a real DockerProvisioner (mirror MySqlEngineClientTests).
        // Build an Instance (Engine="postgres", ContainerId, AdminUser="mayflyadmin", AdminPasswordEnc=secrets.Protect(...), DbName="appdb", DbUser, DbPasswordEnc).
        // Construct DumpImporter with a Mock<IProvisionerClient> whose ExecDumpAsync DELEGATES to the real provisioner (same delegating-mock pattern as MongoQueryTests).
        //   ImportAsync(inst, "CREATE TABLE imp(id int); INSERT INTO imp VALUES (1),(2);") -> Success==true.
        //   Then connect as appuser and assert SELECT COUNT(*) FROM imp == 2.
        //   ImportAsync(inst, "this is not valid sql;;;") -> Success==false, Error/Output non-empty. finally destroy.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter ImportEndpointTests`.
- [ ] **Step 3: Implement.**
  - `ProvisionerDtos.cs`: add `ExecDumpRequest`/`ExecDumpResult` (identical shapes to the Provisioner Contracts side).
  - `IProvisionerClient`+`ProvisionerClient`: add `ExecDumpAsync` — `POST {baseUrl}/instances/{containerId}/exec-dump` (`PostAsJsonAsync` + `EnsureSuccessStatusCode` + `ReadFromJsonAsync<ExecDumpResult>`; the `X-Provisioner-Key` header is on the injected HttpClient, like the existing methods).
  - `DumpImporter.ImportAsync`: decrypt admin pwd (`secrets.Unprotect(inst.AdminPasswordEnc)`), call `prov.ExecDumpAsync(inst.ContainerId, new ExecDumpRequest(inst.Engine, dumpContent, inst.AdminUser, adminPwd, inst.DbName, 60, 256*1024), ct)`, map to `ImportResultDto`: `Success = r.ExitCode == 0`, `Output = r.Output`, `Error = r.ExitCode == 0 ? null : (string.IsNullOrEmpty(r.Error) ? r.Output : r.Error)`, `Truncated`, `Ms`. Wrap the provisioner call in try/catch returning a graceful failure DTO (re-throw `OperationCanceledException`), mirroring `MongoOps.RunConsoleAsync`.
  - `ImportResultDto.cs` (per Interfaces). `types.ts`: mirror `ImportResultDto`.
  - `InstancesController`: add
    ```csharp
    [HttpPost("{token}/import")]
    [EnableRateLimiting("import")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IActionResult> Import(string token, IFormFile file, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        if (inst is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("no file");
        if (file.Length > 16 * 1024 * 1024) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync(ct);
        var result = await dumpImporter.ImportAsync(inst, content, ct);
        return Ok(result);
    }
    ```
    Inject `IDumpImporter dumpImporter`. (Ownership = possession of the capability token, exactly like the `Query` endpoint's `GetByTokenAsync`.)
  - `Program.cs`: `AddScoped<IDumpImporter, DumpImporter>()`; add an `"import"` rate-limit policy (mirror the existing `"create"` policy — a strict per-IP fixed window, e.g. `PermitLimit = 6, Window = 1 min`, partitioned on `RemoteIpAddress`); set `services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 16 * 1024 * 1024)` so the 413 fires for oversize multipart.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter ImportEndpointTests`; full suite once. From `MayFly.Web/`: `npx vue-tsc --noEmit`.
- [ ] **Step 5: Commit** `git commit -m "feat(api): POST /import endpoint + DumpImporter (multipart, size-capped, admin restore)"`

---

## Phase 3 — Frontend

### Task 3: Enable dump + file input + wizard orchestration

**Files:** Modify `MayFly.Web/src/components/InitialDataPicker.vue`, `src/views/NewView.vue`, `src/api/instances.ts`; Test `MayFly.Web/src/components/InitialDataPicker.test.ts`, `src/views/NewView.test.ts`.

- [ ] **Step 1: Tests (RED).** `InitialDataPicker.test.ts`: `dump` is now selectable (clicking sets the model to `'dump'`); when `dump` is selected the component exposes/emits a file (a `<input type="file">` is rendered; selecting a file emits it or updates a `file` model). `NewView.test.ts`: when `initialData==='dump'` with a chosen file, submitting calls `createInstance` with `initialData:'blank'` then `importDump(token, file)` and renders the import result; the create button is disabled when `dump` is selected but no file is chosen. Run: `cd MayFly.Web && npx vitest run src/components/InitialDataPicker.test.ts src/views/NewView.test.ts`.
- [ ] **Step 2: RED.**
- [ ] **Step 3: Implement.**
  - `src/api/instances.ts`: `export const importDump = (token: string, file: File) => { const fd = new FormData(); fd.append('file', file); return api.post<ImportResultDto>(\`/api/instances/${token}/import\`, fd); }` (ensure the shared `api.post` sends `FormData` as multipart — do NOT set a JSON content-type when the body is `FormData`). Import `ImportResultDto` from `./types`.
  - `InitialDataPicker.vue`: `isEnabled('dump')` returns `true`. When `dump` is the selected model, render an `<input type="file" accept=".sql,.js">`; on change, emit the selected `File` to the parent (e.g. a second `v-model`/emit `update:file`) and enforce the 16 MiB client-side check (reject with a message if larger).
  - `NewView.vue`: hold a `dumpFile` ref bound to the picker's file emit. On create, if `initialData === 'dump'`: require `dumpFile`; call `createInstance({ ...params, initialData: 'blank' })`; on success, `await importDump(newToken, dumpFile)`; show progress ("restoring…") and then the `ImportResultDto` (success or the `output`/`error` text in a monospace block); then navigate to the instance. Disable the create button when `dump` selected and no file. Non-dump paths unchanged.
- [ ] **Step 4: GREEN** `npx vitest run`; `npx vue-tsc --noEmit`; `npm run build`.
- [ ] **Step 5: Commit** `git commit -m "feat(web): Import dump — file picker + create->import wizard flow"`

---

## Phase 4 — e2e + docs

### Task 4: Full-stack import e2e + SECURITY.md

**Files:** Modify `scripts/e2e-fullstack.sh`, `SECURITY.md`.

- [ ] **Step 1: Pre-checks.** Clean docker; `dotnet test MayFly.Tests` (green) + from `MayFly.Web/` `npx vitest run` (green). Record counts.
- [ ] **Step 2: Extend `scripts/e2e-fullstack.sh`** — add an import case for **postgres** and **mongo**: `POST /api/instances {engine, ttlHours:3, storageMb:256, initialData:"blank"}` → 201 + token; then `curl -F "file=@<tmp dump>" http://localhost/api/instances/{token}/import` with a small dump written to a temp file (postgres: `CREATE TABLE imp(id int); INSERT INTO imp VALUES (1),(2),(3);`; mongo `.js`: `db.getSiblingDB('appdb').getCollection('imp').insertMany([{_id:1},{_id:2},{_id:3}]);`) → assert `success:true`; then query the restored data via `POST /query` (postgres `SELECT COUNT(*) FROM imp` → 3; mongo `print(db.getCollection('imp').countDocuments())` → 3) → destroy → 204. Reuse the existing session-cookie + query-assertion machinery.
- [ ] **Step 3: Run** `docker rm -f $(docker ps -aq --filter 'name=mayfly-') 2>/dev/null; bash scripts/e2e-fullstack.sh` → the import case (postgres + mongo) passes create→import→query→destroy; existing cases still pass; no leftovers. Fix any real integration bug found.
- [ ] **Step 4: Update `SECURITY.md`** — a subsection on the **import security model**: uploaded dumps are untrusted user content run as the DB admin ONLY inside the user's disposable, egress-blocked, hardened, TTL-bounded container (never on the API — the API doesn't parse the dump); delivered to `/tmp` and run via the engine's native client through the `X-Provisioner-Key`-guarded exec-dump channel; bounded by the 16 MiB size cap, container-side timeout, output cap, per-IP rate limit; restored data counts against the volume quota.
- [ ] **Step 5: Commit** `git commit -m "test: full-stack import-dump e2e + SECURITY.md import model"`

---

## Self-Review (completed)

**Spec coverage:** §2 architecture (post-create endpoint, exec-dump in container as admin, wizard create→import) → Tasks 1-3. §3 exec-dump channel (records, per-engine native client, `/tmp` delivery, guardrails) → Task 1. §4 API endpoint (multipart, 16 MiB cap → 413, GetByTokenAsync ownership, `IDumpImporter`, rate-limit) → Task 2. §5 frontend (enable dump, file input, orchestration) → Task 3. §6 phasing = the 4 phases; per-engine exec-dump test + admin-proof → Task 1; endpoint/size/bad-dump → Task 2; e2e → Task 4.

**Placeholder scan:** Engine-specific commands (psql/mysql/sqlcmd/mongosh with the exact flags + off-argv password env), the `/tmp` tar-delivery, the endpoint code, the `DumpImporter` mapping, and the rate-limit/size-limit config are concrete. Dump CONTENT in tests is small literals the implementer writes (data, not logic). The `success = exitCode==0` semantics (errors surfaced in `output`) are stated in Global Constraints.

**Type consistency:** `ExecDumpRequest`/`ExecDumpResult` are defined once on each side (Provisioner `Contracts` + Api `ProvisionerDtos`) with identical shapes; `IDumpImporter.ImportAsync` → `ImportResultDto(Success, Output, Error, Truncated, Ms)` is consumed by the controller and mirrored in `types.ts`; `importDump(token, file)` on the frontend posts multipart to the same route the controller serves. `ExecDumpAsync` reuses `ExecCaptureAsync`/`CappedStream`/the tar helpers from the existing code (named, not redefined).

**Note (security):** the dump is untrusted and runs as ADMIN — the confinement (container-only, never on the API, `/tmp`-delivered, X-Provisioner-Key, identifier validation on User/Db, dump content never shell-interpolated, size/timeout/output/rate caps) is the load-bearing property; Task 4's SECURITY.md subsection documents it and the final whole-branch review should scrutinize it.
