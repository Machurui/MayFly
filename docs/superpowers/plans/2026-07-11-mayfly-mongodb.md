# MayFly MongoDB — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add MongoDB as a fifth engine with a full mongosh console, executed via `docker exec mongosh --eval` inside the user's hardened ephemeral container — never on the API.

**Architecture:** MongoDB does NOT use the SQL `IEngineClient` abstraction. Provisioning reuses `IEngineProvider` (`MongoEngineProvider`). Everything else — console queries, size measurement, quota soft-enforce — routes through a NEW Provisioner endpoint (`POST /instances/{id}/exec-mongosh`) that the Provisioner runs as `docker exec mongosh` in the user's container. The API never speaks the Mongo wire protocol (no `MongoDB.Driver` in `MayFly.Api`). A thin API-side `MongoOps` service wraps the exec calls with the specific mongosh commands (console as appuser; dbStats + updateUser as admin).

**Tech Stack:** .NET 8, Docker.DotNet.Enhanced, `mongo:7.0` (arm64-native — no emulation), MongoDB.Driver (TEST project only), xUnit/FluentAssertions/Testcontainers, Vue 3 + CodeMirror.

## Global Constraints

- **Runtime:** .NET 8 (`net8.0`). Only `MayFly.Provisioner` references `Docker.DotNet.Enhanced`; `MayFly.Api` references NO Docker package and **NO `MongoDB.Driver`** (the API never speaks Mongo). Docker client `new DockerClientBuilder().Build()`.
- **Commit messages:** plain conventional-commit — NO `Co-Authored-By`, NO attribution/"Generated with"/"🤖" trailer. Commit with `git -c user.name='fanfan' -c user.email='fazemachurui@outlook.fr' commit -m "..."`.
- **Engine id (canonical):** `mongo` everywhere (backend + frontend). Reconcile the frontend `EnginePicker` entry from `mongodb` → `mongo`. Image `mongo:7.0`, port `27017`, DB `appdb`, admin user `mayflyadmin`, app user `appuser`.
- **Roles:** appuser = `readWrite` on `appdb` ONLY (no root/clusterAdmin/other-db). Admin = `mayflyadmin` (root, via `MONGO_INITDB_ROOT_*`). SCRAM auth; hex passwords (no complexity policy).
- **Hardening reused from SP6 per engine:** internal `mayfly-users` network + socat sidecar (no published DB port), read-only rootfs + tmpfs, cap-drop ALL + minimal caps, no-new-privileges, mem/cpu/pids limits, init-volume via writer, reconcile + `SweepOrphansAsync`, quota volume, `mayfly.instance`/`mayfly.role` labels, non-root `mongodb` image user.
- **Untrusted-JS confinement:** the console runs arbitrary user mongosh JS, but ONLY via `docker exec` inside the user's own disposable, egress-blocked, resource-limited container as the non-privileged appuser. NEVER eval on the API. Exec is bounded by a `timeout` wrapper + an output cap.
- **Initial data:** `blank` only for mongo (no seed).
- **Docker integration tests** join `[Collection("docker-sequential")]`. `mongo:7.0` is arm64-native → fast (no emulation). Clean `docker rm -f $(docker ps -aq --filter 'name=mayfly-')` before full-suite runs.
- **Two validators:** the engine allow-list lives in BOTH `MayFly.Api/Validation/ApiSpecValidator.cs` and `MayFly.Provisioner/Validation/InstanceSpecValidator.cs` — add `mongo` to BOTH.

---

## File Structure

```
MayFly.Provisioner/
  Engines/MongoEngineProvider.cs          # NEW: IEngineProvider for mongo
  Docker/DockerProvisioner.cs             # MODIFY: extract ExecCaptureAsync helper; add ExecMongoshAsync (timeout+cap)
  Docker/IDockerProvisioner.cs            # MODIFY: add ExecMongoshAsync signature
  Endpoints/ProvisionerEndpoints.cs       # MODIFY: MapPost /instances/{containerId}/exec-mongosh
  Contracts/*.cs                          # MODIFY: ExecMongoshRequest/Result records
  Validation/InstanceSpecValidator.cs     # MODIFY: add "mongo"
  Program.cs                              # MODIFY: register MongoEngineProvider
MayFly.Api/
  Mongo/IMongoOps.cs                      # NEW: RunConsole/GetSizeBytes/SoftEnforceReadOnly
  Mongo/MongoOps.cs                       # NEW: wraps IProvisionerClient.ExecMongoshAsync
  Provisioning/IProvisionerClient.cs      # MODIFY: add ExecMongoshAsync
  Provisioning/ProvisionerClient.cs       # MODIFY: implement it (POST + X-Provisioner-Key)
  Provisioning/ProvisionerDtos.cs         # MODIFY: ExecMongoshRequest/Result records
  Dtos/CreateInstanceDto.cs               # MODIFY: QueryRequestDto.Sql -> Query
  Dtos/QueryResultDto.cs                  # MODIFY: add optional Output + Truncated
  Controllers/InstancesController.cs      # MODIFY: query branch (mongo -> MongoOps); DisplayCs mongo URI
  Lifecycle/LifecycleService.cs           # MODIFY: size-monitor mongo branch
  Lifecycle/QuotaEnforcer.cs              # MODIFY: soft-enforce mongo branch
  Validation/ApiSpecValidator.cs          # MODIFY: add "mongo"
  Program.cs                              # MODIFY: register MongoOps
MayFly.Web/
  src/components/EnginePicker.vue          # MODIFY: mongodb->mongo, enabled:true
  src/lib/engineLabels.ts                  # MODIFY: mongo -> MongoDB
  src/lib/snippets.ts                      # MODIFY: mongo parse + mongoSnippets
  src/api/instances.ts + api/types.ts      # MODIFY: query field rename; QueryResultDto.output
  src/views/ConsoleView.vue                # MODIFY: engine branch (JS editor + JSON output for mongo)
scripts/e2e-fullstack.sh                   # MODIFY: mongo case
SECURITY.md                                # MODIFY: mongo engine + exec-channel security
```

---

## Phase 1 — Provisioning

### Task 1: `MongoEngineProvider` + hardening + validators

**Files:** Create `MayFly.Provisioner/Engines/MongoEngineProvider.cs`; Modify `MayFly.Provisioner/Program.cs`, `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`, `MayFly.Api/Validation/ApiSpecValidator.cs`, `MayFly.Tests/MayFly.Tests.csproj`; Test `MayFly.Tests/Provisioner/MongoEngineTests.cs`.

**Interfaces:** Produces `MongoEngineProvider : IEngineProvider` (EngineId `mongo`, Image `mongo:7.0`, Port 27017, UsesInitVolume true, DataDirectory `/data/db`).

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class MongoEngineTests
{
    [Fact(Timeout = 120000)]
    public async Task Mongo_appuser_scoped_readWrite_only_and_reachable()
    {
        // Provision a mongo instance via DockerProvisioner.CreateAsync(engine "mongo", 3, 256, "blank").
        // Construct the provisioner with a provider set including MongoEngineProvider (+ the others).
        // Connect as appuser via the sidecar published port using MongoDB.Driver
        //   ("mongodb://appuser:<pwd>@localhost:<publicPort>/appdb?authSource=appdb"):
        //  - insert + find a doc in appdb succeeds (legit readWrite)
        //  - db.adminCommand({listDatabases:1}) / access to another db (e.g. "other") is REJECTED (not privileged)
        // Egress probe from the DB container fails (reuse the EgressTests probe: label mayfly.role=db,
        //   /dev/tcp or nc — mongo:7 is Debian, has bash+/dev/tcp).
        // finally: prov.DestroyAsync(...) cleans up.
    }
}
```
- [ ] **Step 2: Add `MongoDB.Driver` to the TEST project** — `dotnet add MayFly.Tests package MongoDB.Driver --version 2.28.0`. RED: `dotnet test MayFly.Tests --filter MongoEngineTests`.
- [ ] **Step 3: Implement `MongoEngineProvider`** (mirror `PostgresEngineProvider`/`MySqlEngineProvider` structure — the hex password helper, the init-script literal escape helper, the `ApplyHardening` shape)
```csharp
public sealed class MongoEngineProvider : IEngineProvider
{
    public string EngineId => "mongo";
    public string Image => "mongo:7.0";
    public int Port => 27017;
    public bool UsesInitVolume => true;
    public string DataDirectory => "/data/db";
    public EngineCredentials GenerateCredentials() => /* hex admin+app pwds, admin "mayflyadmin", app "appuser", db "appdb" */;
    public IList<string> BuildEnv(EngineCredentials c) => new List<string> {
        $"MONGO_INITDB_ROOT_USERNAME={c.AdminUser}",
        $"MONGO_INITDB_ROOT_PASSWORD={c.AdminPassword}",
        $"MONGO_INITDB_DATABASE={c.Db}" };
    public EngineSetup BuildSetup(EngineCredentials c, string initialData)
    {
        // mongo runs /docker-entrypoint-initdb.d/*.js at first init, against the admin.
        var js = $@"db.getSiblingDB('{c.Db}').createUser({{
  user: '{c.AppUser}', pwd: '{JsEscape(c.AppPassword)}',
  roles: [{{ role: 'readWrite', db: '{c.Db}' }}]
}});";
        return new EngineSetup(new[] { ("00-roles.js", js) }, PostReadyExec: null);
    }
    public IList<string> ReadinessExec(EngineCredentials c) => new List<string> {
        "mongosh", "--quiet", "--host", "127.0.0.1",
        "-u", c.AdminUser, "-p", c.AdminPassword, "--authenticationDatabase", "admin",
        "--eval", "db.adminCommand('ping')" };
    public void ApplyHardening(HostConfig hc)
    {
        hc.ReadonlyRootfs = true;
        hc.Tmpfs = new Dictionary<string,string> { // iterate if mongo won't boot RO
            ["/tmp"] = "rw,noexec,nosuid,size=64m",
            ["/data/configdb"] = "rw,nosuid,size=64m" };
        hc.Memory = 1024L * 1024 * 1024;   // 1 GiB (WiredTiger); lower to 512Mi only if it still boots
        hc.NanoCPUs = 1_000_000_000;
        hc.PidsLimit = 200;
        hc.CapDrop = new List<string> { "ALL" };
        hc.CapAdd  = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" };
        hc.SecurityOpt = new List<string> { "no-new-privileges" };
    }
}
```
Register in `MayFly.Provisioner/Program.cs`: `AddSingleton<IEngineProvider, MongoEngineProvider>()`. Add `"mongo"` to the `Engines` set in BOTH `MayFly.Provisioner/Validation/InstanceSpecValidator.cs` and `MayFly.Api/Validation/ApiSpecValidator.cs` (the API validator's `northwind`-only-postgres rule already excludes mongo from northwind).
- [ ] **Step 4: Iterate to GREEN** `dotnet test MayFly.Tests --filter MongoEngineTests`. If mongo won't boot under read-only rootfs, add the tmpfs path it logs (do NOT disable rootfs silently — report DONE_WITH_CONCERNS with the log if truly impossible). Then full suite once: `dotnet test MayFly.Tests`.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): MongoDB engine provider (hardened, readWrite-scoped appuser)"`

---

## Phase 2 — Exec channel

### Task 2: `ExecMongoshAsync` on the Provisioner (endpoint + timeout + output cap)

**Files:** Modify `MayFly.Provisioner/Docker/DockerProvisioner.cs`, `Docker/IDockerProvisioner.cs`, `Endpoints/ProvisionerEndpoints.cs`, `Contracts/*.cs` (the provisioner-side request/result records); Test `MayFly.Tests/Provisioner/ExecMongoshTests.cs`.

**Interfaces:**
- Produces (Provisioner): `Task<ExecMongoshResult> ExecMongoshAsync(string containerId, ExecMongoshRequest req, CancellationToken ct)` where
  ```csharp
  public record ExecMongoshRequest(string Command, string User, string Password, string AuthDb, int TimeoutSeconds, int MaxOutputBytes);
  public record ExecMongoshResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
  ```
- Endpoint: `POST /instances/{containerId}/exec-mongosh` (body = `ExecMongoshRequest`) → `ExecMongoshResult`. (The X-Provisioner-Key middleware from SP6 already guards all Provisioner routes.)

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class ExecMongoshTests
{
    [Fact(Timeout = 120000)]
    public async Task Exec_runs_mongosh_captures_output_and_enforces_timeout()
    {
        // Provision a mongo instance. Then via DockerProvisioner.ExecMongoshAsync(containerId, req):
        //  - as appuser: command "db.getCollection('t').insertOne({x:1}); print(db.getCollection('t').countDocuments())"
        //      -> ExitCode 0, Output contains "1".
        //  - a syntax-error / bad command -> ExitCode != 0, Error non-empty.
        //  - a runaway "while(true){}" with TimeoutSeconds=3 -> returns within ~5s with a non-zero exit
        //      (timeout wrapper kills it), NOT hanging.
        //  - a command printing a huge string with MaxOutputBytes small -> Truncated == true, Output length capped.
        // finally destroy.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter ExecMongoshTests`.
- [ ] **Step 3: Implement.** Extract the existing exec pattern (`DockerProvisioner.cs` ~L200-219: `ExecCreateContainerExecAsync` + `StartContainerExecAsync` + `CopyOutputToAsync(Stream.Null, stdoutBuf, stderrBuf)` + `InspectContainerExecAsync`) into a reusable private `Task<(int exit, string stdout, string stderr, bool truncated)> ExecCaptureAsync(string containerId, IList<string> cmd, int maxBytes, CancellationToken ct)` (cap the buffers at `maxBytes`, set `truncated`). Then:
```csharp
public async Task<ExecMongoshResult> ExecMongoshAsync(string containerId, ExecMongoshRequest req, CancellationToken ct)
{
    // Kill runaway JS inside the container with coreutils `timeout` (mongo:7 is Debian → has it).
    // Pass the user's JS (MONGO_CMD) and the password (MONGO_PWD) via the exec's ENV, referenced from a
    // double-quoted sh -c expansion:
    //   sh -c 'timeout <secs> mongosh --quiet --host 127.0.0.1 -u <user> -p "$MONGO_PWD" \
    //          --authenticationDatabase <authdb> appdb --eval "$MONGO_CMD"'
    // with Env = ["MONGO_PWD=<pwd>", "MONGO_CMD=<command>"].
    // Why this is safe: the JS is injected via env expansion into a double-quoted context, so it is NEVER
    // parsed by the shell as command text -> no shell-injection surface despite `sh -c`. The password stays
    // out of the docker-exec Cmd/inspect (it's in Env). It DOES land on the in-container mongosh argv after
    // expansion, but the untrusted appuser has no shell/ps access in the container (only this mongosh --eval
    // channel), so it is unobservable to them -- same acceptable residual as the SQL `-P`/`mysqladmin -p` pattern.
    var sh = $"timeout {req.TimeoutSeconds} mongosh --quiet --host 127.0.0.1 -u {req.User} " +
             $"-p \"$MONGO_PWD\" --authenticationDatabase {req.AuthDb} appdb --eval \"$MONGO_CMD\"";
    var cmd = new List<string> { "sh", "-c", sh };
    var env = new List<string> { $"MONGO_PWD={req.Password}", $"MONGO_CMD={req.Command}" };
    var sw = Stopwatch.StartNew();
    var (exit, outStr, errStr, truncated) = await ExecCaptureAsync(containerId, cmd, env, req.MaxOutputBytes, ct);
    return new ExecMongoshResult(outStr, errStr, exit, truncated, (int)sw.ElapsedMilliseconds);
}
```
(Extend `ExecCaptureAsync` to accept an `env` list — `ContainerExecCreateParameters.Env`. Passing `MONGO_CMD` via env means the JS is NOT on the argv/shell command line, so there is no shell-injection surface even though we use `sh -c`; only `$MONGO_PWD`/`$MONGO_CMD` expansions are used.) Add the signature to `IDockerProvisioner`. Add the endpoint in `ProvisionerEndpoints.cs`:
```csharp
app.MapPost("/instances/{containerId}/exec-mongosh",
    async (string containerId, ExecMongoshRequest req, IDockerProvisioner p, CancellationToken ct)
        => Results.Ok(await p.ExecMongoshAsync(containerId, req, ct)));
```
Also add an OUTER safety timeout: link `ct` with a `CancellationTokenSource(TimeSpan.FromSeconds(req.TimeoutSeconds + 5))` so the API call can't hang even if the container-side `timeout` misbehaves.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter ExecMongoshTests`.
- [ ] **Step 5: Commit** `git commit -m "feat(provisioner): exec-mongosh endpoint (docker-exec mongosh, timeout + output cap)"`

---

### Task 3: API query routing (MongoOps.RunConsole + controller branch + DTO generalization)

**Files:** Create `MayFly.Api/Mongo/IMongoOps.cs`, `MayFly.Api/Mongo/MongoOps.cs`; Modify `MayFly.Api/Provisioning/IProvisionerClient.cs`, `ProvisionerClient.cs`, `ProvisionerDtos.cs`, `MayFly.Api/Dtos/CreateInstanceDto.cs`, `Dtos/QueryResultDto.cs`, `Controllers/InstancesController.cs`, `Program.cs`, `MayFly.Web/src/api/instances.ts`, `MayFly.Web/src/api/types.ts`; Test `MayFly.Tests/Api/MongoQueryTests.cs`.

**Interfaces:**
- Produces: `IProvisionerClient.ExecMongoshAsync(string containerId, ExecMongoshRequest req, CancellationToken ct)` (Api mirror of the provisioner records — add matching `ExecMongoshRequest`/`ExecMongoshResult` to `ProvisionerDtos.cs`). `IMongoOps.RunConsoleAsync(Instance inst, string command, CancellationToken ct) : Task<QueryResultDto>`. `QueryRequestDto(string Query)`. `QueryResultDto(bool Success, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, int RowCount, int ElapsedMs, string? Error, string? Output = null, bool Truncated = false)`.

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class MongoQueryTests
{
    [Fact(Timeout = 120000)]
    public async Task Mongo_console_runs_via_provisioner_exec()
    {
        // Provision a mongo instance; build an Instance (Engine="mongo", ContainerId, DbUser="appuser",
        //   DbPasswordEnc, AdminUser="mayflyadmin", AdminPasswordEnc). Construct MongoOps with a real
        //   IProvisionerClient pointed at the provisioner (or a ProvisionerClient over the test host).
        //   [Mirror how ProvisionerClient is constructed in existing Api integration tests.]
        //  - RunConsoleAsync(inst, "db.getCollection('t').insertOne({x:1}); db.getCollection('t').find().toArray()")
        //      -> Success==true, Output non-empty containing the inserted doc.
        //  - RunConsoleAsync(inst, "this is not valid js;;;") -> Success==false, Error non-empty.
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter MongoQueryTests`.
- [ ] **Step 3: Implement.**
  - `ProvisionerDtos.cs`: add `public record ExecMongoshRequest(string Command, string User, string Password, string AuthDb, int TimeoutSeconds, int MaxOutputBytes);` and `public record ExecMongoshResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);`.
  - `IProvisionerClient` + `ProvisionerClient`: add `ExecMongoshAsync` — `POST {baseUrl}/instances/{containerId}/exec-mongosh` with the `X-Provisioner-Key` header (mirror the existing `ProvisionerClient` calls), `EnsureSuccessStatusCode`, read `ExecMongoshResult`.
  - `MongoOps.RunConsoleAsync`: decrypt appuser pwd (`secrets.Unprotect(inst.DbPasswordEnc)`), call `ExecMongoshAsync(inst.ContainerId, new ExecMongoshRequest(command, inst.DbUser, pwd, authDb: inst.DbName, TimeoutSeconds: 10, MaxOutputBytes: 256*1024))`, map to `QueryResultDto`: `Success = exit==0`, `Output = result.Output`, `Error = exit==0 ? null : (result.Error != "" ? result.Error : result.Output)`, `Truncated = result.Truncated`, empty Columns/Rows.
  - `QueryResultDto.cs`: append `string? Output = null, bool Truncated = false` (defaults keep the 3 existing SQL call sites in `QueryExecutor.cs` compiling unchanged).
  - `QueryRequestDto`: rename `Sql` → `Query` (`public record QueryRequestDto(string Query);`).
  - `InstancesController.Query`: branch — `var result = inst.Engine == "mongo" ? await mongoOps.RunConsoleAsync(inst, body.Query, ct) : await queryExec.ExecuteAsync(inst, body.Query, ct);`. Inject `IMongoOps`. Keep the best-effort QueryLog write (log `body.Query` into the existing `QueryLog.Sql` column — column name unchanged).
  - `InstancesController` `DisplayCs(Instance i)` helper (from SP2): branch — `i.Engine == "mongo" ? $"mongodb://{i.DbUser}:{pwd}@{PublicHost}:{i.PublicPort}/{i.DbName}" : registry.For(i.Engine).BuildDisplayConnectionString(...)` (mongo has no `IEngineClient`).
  - `Program.cs`: `AddSingleton<IMongoOps, MongoOps>()`.
  - Frontend: `MayFly.Web/src/api/instances.ts` `runQuery` — send `{ query }` instead of `{ sql }`. `src/api/types.ts` `QueryResultDto` — add `output?: string; truncated?: boolean`.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter MongoQueryTests`; then `dotnet test MayFly.Tests` (confirm SQL query tests still pass after the `Sql`→`Query` rename). From `MayFly.Web/`: `npx vue-tsc --noEmit`.
- [ ] **Step 5: Commit** `git commit -m "feat(api): route mongo console queries through provisioner exec (MongoOps); neutral query field + output DTO"`

---

## Phase 3 — Size + soft-enforce

### Task 4: Mongo size-monitor + soft-enforce branches

**Files:** Modify `MayFly.Api/Mongo/IMongoOps.cs`, `Mongo/MongoOps.cs`, `Lifecycle/LifecycleService.cs`, `Lifecycle/QuotaEnforcer.cs` (+ DI as needed); Test `MayFly.Tests/Api/MongoQuotaTests.cs`.

**Interfaces:** Produces `IMongoOps.GetSizeBytesAsync(Instance inst, CancellationToken ct) : Task<long>` and `IMongoOps.SoftEnforceReadOnlyAsync(Instance inst, CancellationToken ct) : Task`.

- [ ] **Step 1: Write the failing integration test**
```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class MongoQuotaTests
{
    [Fact(Timeout = 120000)]
    public async Task Mongo_soft_enforce_flips_appuser_read_only()
    {
        // Provision mongo. Insert a doc as appuser (via MongoOps.RunConsoleAsync). GetSizeBytesAsync > 0.
        // Call SoftEnforceReadOnlyAsync(inst). Then RunConsoleAsync(inst, "db.getCollection('t').insertOne({y:2})")
        //   -> Success==false (write rejected: appuser is now read-only on appdb).
        //   [Each RunConsoleAsync spawns a fresh mongosh, so the new role is in effect.]
    }
}
```
- [ ] **Step 2: RED** `dotnet test MayFly.Tests --filter MongoQuotaTests`.
- [ ] **Step 3: Implement.**
  - `MongoOps.GetSizeBytesAsync`: admin exec, command `var s=db.getSiblingDB('${inst.DbName}').stats(); print(s.storageSize + s.indexSize)`, using `inst.AdminUser`/`secrets.Unprotect(inst.AdminPasswordEnc)`, `AuthDb: "admin"`. Parse the trailing integer from `Output` (trim; `long.Parse`); on parse failure return 0 and log (best-effort, like the SQL size path).
  - `MongoOps.SoftEnforceReadOnlyAsync`: admin exec, command `db.getSiblingDB('${inst.DbName}').updateUser('${inst.DbUser}', {roles:[{role:'read', db:'${inst.DbName}'}]})`.
  - `LifecycleService` size-monitor loop: `if (inst.Engine == "mongo") sizeBytes = await mongoOps.GetSizeBytesAsync(inst, ct); else <existing SQL SizeQuerySql path>`. Inject `IMongoOps`.
  - `QuotaEnforcer`: `if (inst.Engine == "mongo") await mongoOps.SoftEnforceReadOnlyAsync(inst, ct); else <existing SQL admin ALTER path>`. Inject `IMongoOps`. Keep best-effort try/catch + logging.
- [ ] **Step 4: GREEN** `dotnet test MayFly.Tests --filter MongoQuotaTests`; then `dotnet test MayFly.Tests`.
- [ ] **Step 5: Commit** `git commit -m "feat(api): mongo size (dbStats) + soft-enforce (readWrite->read) via provisioner exec"`

---

## Phase 4 — Frontend

### Task 5: Enable mongo in wizard + snippets

**Files:** Modify `MayFly.Web/src/components/EnginePicker.vue`, `src/lib/engineLabels.ts`, `src/lib/snippets.ts`; Test `MayFly.Web/src/lib/snippets.test.ts`, `src/components/EnginePicker.test.ts`.

- [ ] **Step 1: Tests (RED).** `snippets.test.ts`: for a `mongo` inst (connectionString `mongodb://appuser:pw@localhost:27017/appdb`): bash contains `mongosh`, python `pymongo`, node `mongodb`, go `go.mongodb.org/mongo-driver`, dotnet `MongoDB.Driver`; each contains host/port/db/user AND the password `pw`. `EnginePicker.test.ts`: the MongoDB card is selectable and clicking it sets the model to `'mongo'` (NOT `'mongodb'`). Run: `cd MayFly.Web && npx vitest run src/lib/snippets.test.ts src/components/EnginePicker.test.ts`.
- [ ] **Step 2: RED.**
- [ ] **Step 3: Implement.** `EnginePicker.vue`: change the MongoDB entry `id: 'mongodb'` → `id: 'mongo'` and `enabled: false` → `enabled: true`. `engineLabels.ts`: add `mongo: 'MongoDB'`. `snippets.ts`: in `parse`, mongo uses the URL path (like mysql — `mongodb://` parses as a URL); add a `mongoSnippets(p)` returning the 5-language snippets (bash `mongosh "<url>"`; python `from pymongo import MongoClient; MongoClient("<url>")`; node `import { MongoClient } from 'mongodb'; new MongoClient("<url>")`; go `go.mongodb.org/mongo-driver/mongo` + `mongo.Connect(...options.Client().ApplyURI("<url>"))`; dotnet `using MongoDB.Driver; new MongoClient("<url>")`), each embedding host/port/db/user/pass; dispatch `if (inst.engine === 'mongo') return mongoSnippets(p)`.
- [ ] **Step 4: GREEN** `npx vitest run`; `npx vue-tsc --noEmit`; `npm run build`.
- [ ] **Step 5: Commit** `git commit -m "feat(web): enable MongoDB in wizard (id mongo) + connection snippets"`

---

### Task 6: Mongo console mode (JS editor + JSON output)

**Files:** Modify `MayFly.Web/src/views/ConsoleView.vue`; Test `MayFly.Web/src/views/ConsoleView.test.ts` (extend or create).

- [ ] **Step 1: Add the JS language package** — `cd MayFly.Web && npm i @codemirror/lang-javascript`.
- [ ] **Step 2: Tests (RED).** In `ConsoleView.test.ts`: mounting the console for a `mongo` instance renders the JavaScript editor (default doc contains `db.` — e.g. `db.getCollection("items").find()`) and renders the mongo output pane (a `<pre>`/monospace block bound to `result.output`) rather than the tabular `QueryResults`; for a `postgres` instance it still renders the SQL editor + `QueryResults`. (Stub `runQuery` to return a `QueryResultDto` with `output` set for mongo and `columns`/`rows` for sql.)
- [ ] **Step 3: RED** `npx vitest run src/views/ConsoleView.test.ts`.
- [ ] **Step 4: Implement.** `ConsoleView.vue`: read the instance engine (the view already loads the instance by token — expose `inst.engine`). Branch the CodeMirror language + default doc: `inst.engine === 'mongo'` → `javascript()` from `@codemirror/lang-javascript` with doc `db.getCollection("items").find()`; else `sql({ dialect: PostgreSQL })` with `SELECT 1;`. Branch the result render: mongo → a `<pre class="mongo-output">{{ result.output }}</pre>` (show `result.error` when `!result.success`; show a "truncated" note when `result.truncated`); else → `<QueryResults :result="result" />`. Send the editor text via `runQuery` unchanged (it now posts `{ query }`).
- [ ] **Step 5: GREEN** `npx vitest run`; `npx vue-tsc --noEmit`; `npm run build`.
- [ ] **Step 6: Commit** `git commit -m "feat(web): MongoDB console mode (JS editor + JSON output)"`

---

## Phase 5 — e2e + docs

### Task 7: Full-stack e2e mongo case + SECURITY.md

**Files:** Modify `scripts/e2e-fullstack.sh`, `SECURITY.md`.

- [ ] **Step 1: Pre-checks.** Clean docker; `dotnet test MayFly.Tests` (all green) + from `MayFly.Web/` `npx vitest run` (all green). Record counts.
- [ ] **Step 2: Extend `scripts/e2e-fullstack.sh`.** Add a `mongo` case to the per-engine flow (from SP2's `test_engine` function): `POST /api/instances {engine:"mongo", ttlHours:3, storageMb:256, initialData:"blank"}` → 201 + token + `mongodb://…` connectionString → `POST /api/instances/{token}/query {query:"db.getCollection('t').insertOne({x:1}); print(db.getCollection('t').countDocuments())"}` → assert `success:true` and the `output` contains `1` (mongo returns `output`, not `rows`) → egress probe (label `mayfly.role=db`, best-effort) → `DELETE` → 204. The `test_engine` helper (or a mongo-specific variant) must handle the `output`-vs-`rows` assertion for mongo.
- [ ] **Step 3: Run** `bash scripts/e2e-fullstack.sh` → mongo (and all prior engines) pass create→query→destroy through Caddy; no leftovers. Fix any real integration bug found (note it).
- [ ] **Step 4: Update `SECURITY.md`.** Add MongoDB to the engine list, and a subsection on the **exec-channel security model**: console runs arbitrary user mongosh JS but ONLY via `docker exec` inside the user's own disposable, egress-blocked, resource-limited container as the non-privileged appuser (`readWrite` on appdb only); never eval on the API; bounded by a container-side `timeout` + output cap; size/soft-enforce run as admin via the same channel; the exec endpoint requires `X-Provisioner-Key`.
- [ ] **Step 5: Commit** `git commit -m "test: full-stack e2e mongo case + SECURITY.md exec-channel model"`

---

## Self-Review (completed)

**Spec coverage:** §2 architecture (Mongo separate from IEngineClient; all ops via provisioner exec) → Tasks 1-4. §3 provisioning → Task 1. §4 exec channel (endpoint + routing + neutral field + output + timeout/cap + security) → Tasks 2-3. §5 size + soft-enforce → Task 4. §6 frontend (enable+id, console JS/JSON, snippets, both validators) → Tasks 1 (validators) + 5 + 6. §7 phasing = the 5 phases; testing → per-task Docker integration + Task 7 e2e.

**Placeholder scan:** Engine-specific content is concrete (provider env/role-js/readiness/hardening; the exec `sh -c timeout mongosh` with env-passed pwd+command; MongoOps commands; snippet drivers; console branch). Integration tests are specified by the exact mongosh commands + assertions the implementer writes (the RED/GREEN cycle), as in the SQL engine tasks. `GenerateCredentials`/`JsEscape` reuse the existing PostgresEngineProvider helpers (named, not re-specified).

**Type consistency:** `ExecMongoshRequest`/`ExecMongoshResult` records are defined once each on both sides (Provisioner `Contracts` + Api `ProvisionerDtos`) with identical shapes; `IMongoOps` (`RunConsoleAsync`/`GetSizeBytesAsync`/`SoftEnforceReadOnlyAsync`) is defined in Task 3/4 and consumed by the controller/lifecycle/enforcer; `QueryResultDto` gains `Output`/`Truncated` as trailing defaulted params (SQL call sites unchanged); `QueryRequestDto.Query` rename is applied atomically in Task 3 (backend DTO + controller + frontend `runQuery` body). Engine id `mongo` is used consistently in providers, validators (both), the controller branch, EnginePicker, engineLabels, snippets, and the e2e.

**Note (untrusted JS):** The console executes arbitrary user mongosh JS — but only inside the user's own hardened, egress-blocked, resource-limited, disposable container as the non-privileged appuser, wrapped in a container-side `timeout` with an output cap. This is the central security property; Task 7's SECURITY.md subsection documents it.
