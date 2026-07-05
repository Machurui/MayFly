# MayFly Security Hardening — Implementation Plan (Sub-project 6)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden MayFly for real public exposure: block user-container egress, drop the DB user's superuser, read-only rootfs, real disk-quota enforcement, an idempotent + self-reconciling lifecycle, and defense-in-depth (Provisioner auth, rate-limit).

**Architecture:** Backend-only changes across `MayFly.Provisioner` (network topology + socat sidecar, non-superuser role, read-only rootfs, quota, reconcile, endpoint auth, logging) and `MayFly.Api` (`Destroying` state + guarded destroy, admin credential, reconcile + soft-enforce in the lifecycle, rate-limit policies), plus compose networks and `SECURITY.md`. Validated via the Docker API against Docker Desktop's Linux VM; the XFS hard-cap is validated on the VPS at deploy.

**Tech Stack:** .NET 8, Docker.DotNet.Enhanced 4.2.0, Npgsql 8.0.6, EF Core 8, xUnit + FluentAssertions + Moq + Testcontainers, `alpine/socat`.

## Global Constraints

- **Runtime:** .NET 8 (`net8.0`). Provisioner uses `Docker.DotNet.Enhanced`; Api references NO Docker package (privilege boundary preserved).
- **Docker client** is built with `new DockerClientBuilder().Build()`. Testcontainers Postgres: `new PostgreSqlBuilder("postgres:16-alpine").Build()` (parameterless ctor is obsolete/CS0618).
- **Commit messages:** plain conventional-commit — NO `Co-Authored-By` and NO attribution/"Generated with"/"🤖" trailer.
- **DB image:** `postgres:16-alpine`. **Sidecar image:** `alpine/socat` (pin a tag, e.g. `alpine/socat:1.8.0.0`).
- **Networks:** user DBs on `mayfly-users` (`internal: true`, no egress); sidecars on `mayfly-users` + `mayfly-ingress` (bridge); the Api joins `mayfly-users`; the Provisioner is NOT on `mayfly-users`.
- **Roles:** container superuser is `mayflyadmin` (internal only, never exposed); the user role is `appuser` (`LOGIN NOSUPERUSER`, scoped grants). Connection strings + `QueryExecutor` use `appuser`; seeding + soft-enforce use `mayflyadmin`.
- **Container labels:** DB container `mayfly.role=db`, sidecar `mayfly.role=sidecar`, both `mayfly.instance=<id>`; volumes `mayfly.instance=<id>`.
- **Active states (count toward 3/IP quota):** `Provisioning`, `Running` (NOT `Destroying`/`Destroyed`/`Failed`/`Expired`).
- **Docker integration tests** join the existing xUnit `[Collection("docker-sequential")]`.
- **Storage quota values:** 256/512/1024/2048 MB. **TTL:** 3/6/12 h.

---

## File Structure

```
MayFly.Provisioner/
  Docker/DockerProvisioner.cs      # networks, sidecar, rootfs, role init, ILogger, sidecar-aware destroy
  Docker/IVolumeProvisioner.cs
  Docker/XfsVolumeProvisioner.cs   # size-opt-only hard cap
  Docker/RoleInitializer.cs        # NEW: creates appuser (NOSUPERUSER) + preinstalls extensions as mayflyadmin
  Contracts/InstanceSpec.cs        # CreateInstanceResult gains AdminPassword
  Seeding/PostgresSeeder.cs        # seed as mayflyadmin
  Endpoints/ProvisionerEndpoints.cs+ Program.cs  # X-Provisioner-Key auth
MayFly.Api/
  Domain/Instance.cs               # AdminPasswordEnc + InstanceState.Destroying
  Data/Migrations/*                # add AdminPasswordEnc column
  Services/InstanceService.cs      # store admin cred, guarded idempotent destroy
  Services/IProvisionerClient impl # X-Provisioner-Key header, AdminPassword in result
  Lifecycle/LifecycleService.cs    # reconcile-on-startup + soft-enforce quota
  Lifecycle/QuotaEnforcer.cs       # NEW: ALTER ROLE appuser read-only via mayflyadmin
  Program.cs                       # rate-limit policies (create/query)
docker-compose.yml                 # mayfly-users/mayfly-ingress, api on mayfly-users, provisioner key
SECURITY.md                        # NEW
MayFly.Tests/…                     # per-task tests (docker-sequential)
```

---

## Phase 1 — Robustness (foundation)

### Task 1: ILogger in the Provisioner (no more silent cleanup)

**Files:**
- Modify: `MayFly.Provisioner/Docker/DockerProvisioner.cs`, `MayFly.Provisioner/Program.cs`
- Modify (tests): `MayFly.Tests/Provisioner/DockerProvisionerTests.cs`, `SeederTests.cs` constructions

**Interfaces:**
- Produces: `DockerProvisioner(IDockerClient, IPortAllocator, IVolumeProvisioner, ILogger<DockerProvisioner>)`.

- [ ] **Step 1: Update the failing test constructions**

In every `new DockerProvisioner(docker, ports, volumes)` in the tests, add a logger arg:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
// ...
new DockerProvisioner(docker, new PortAllocator(Array.Empty<int>()),
    new PlainVolumeProvisioner(docker), NullLogger<DockerProvisioner>.Instance);
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet build MayFly.sln`
Expected: FAIL — `DockerProvisioner` has no 4-arg constructor.

- [ ] **Step 3: Add the logger and log swallowed failures**

In `DockerProvisioner.cs`, change the primary constructor to accept `ILogger<DockerProvisioner> log` and replace every empty `catch { }` around cleanup with a logged catch:
```csharp
public sealed class DockerProvisioner(
    IDockerClient docker, IPortAllocator ports, IVolumeProvisioner volumes,
    ILogger<DockerProvisioner> log) : IDockerProvisioner
{
    // in CreateAsync catch cleanup and DestroyAsync:
    try { await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct); }
    catch (Exception ex) { log.LogWarning(ex, "cleanup: remove container {Id} failed", containerId); }
    // ...same pattern for volume destroy, port release logging where relevant
}
```
Add `using Microsoft.Extensions.Logging;`. In `Program.cs`, DI already provides `ILogger<>` via `AddLogging` (WebApplication default) — `DockerProvisioner` resolves it automatically (it's registered `AddSingleton<IDockerProvisioner, DockerProvisioner>()`).

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet build MayFly.sln && dotnet test MayFly.Tests --filter "DockerProvisionerLifecycleTests|VolumeProvisionerTests"`
Expected: build clean; the Docker lifecycle tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore(provisioner): inject ILogger and log swallowed cleanup failures"
```

---

### Task 2: `Destroying` state + guarded idempotent destroy

**Files:**
- Modify: `MayFly.Api/Domain/Instance.cs` (enum), `MayFly.Api/Services/InstanceService.cs`
- Test: `MayFly.Tests/Api/InstanceServiceTests.cs` (append)

**Interfaces:**
- Produces: `InstanceState.Destroying`; `InstanceService.DestroyAsync` is idempotent (concurrent/repeat calls transition once). Active states unchanged: {Provisioning, Running}.

- [ ] **Step 1: Write the failing test** (append to `InstanceServiceQuotaTests` or a new class)

```csharp
[Fact]
public async Task DestroyAsync_is_idempotent_single_transition()
{
    var (sut, ctx) = NewSut();  // existing helper: real Testcontainers metadata DB + mocked IProvisionerClient
    var created = (await sut.CreateAsync("postgres", 3, 256, "blank", "7.7.7.7", "s", default)).Instance!;
    var token = created.CapabilityToken;

    var first = await sut.DestroyAsync(token, default);
    var second = await sut.DestroyAsync(token, default);   // repeat

    first.Should().BeTrue();
    second.Should().BeFalse();   // already destroyed → no-op
    (await ctx.Instances.SingleAsync(i => i.CapabilityToken == token)).State
        .Should().Be(InstanceState.Destroyed);
    // provisioner destroy called at most once for this instance
    // (verify via the Mock<IProvisionerClient> in NewSut)
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter DestroyAsync_is_idempotent_single_transition`
Expected: FAIL (`InstanceState.Destroying` not defined / behavior not guarded).

- [ ] **Step 3: Implement**

`Instance.cs`: add `Destroying` to the enum:
```csharp
public enum InstanceState { Provisioning, Running, Destroying, Expired, Destroyed, Failed }
```
`InstanceService.DestroyAsync`: replace the current body with a guarded transition using an atomic `ExecuteUpdate` (EF Core 8):
```csharp
public async Task<bool> DestroyAsync(string token, CancellationToken ct)
{
    var inst = await db.Instances.SingleOrDefaultAsync(i => i.CapabilityToken == token, ct);
    if (inst is null) return false;

    // Atomically claim the destroy: only one caller flips an active row to Destroying.
    var claimed = await db.Instances
        .Where(i => i.Id == inst.Id &&
                    (i.State == InstanceState.Running || i.State == InstanceState.Provisioning))
        .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, InstanceState.Destroying), ct);
    if (claimed == 0) return false;   // already being/been destroyed

    await provisioner.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct);
    await db.Instances.Where(i => i.Id == inst.Id)
        .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, InstanceState.Destroyed), ct);
    return true;
}
```
(`ExecuteUpdate` bypasses the change tracker — reload or clear if the entity is reused; here we return immediately.) Add `using Microsoft.EntityFrameworkCore;`. No DB migration needed (enum stored as string).

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter "InstanceServiceQuotaTests|DestroyAsync_is_idempotent_single_transition"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): idempotent guarded destroy with Destroying state"
```

---

### Task 3: Reconcile-on-startup

**Files:**
- Modify: `MayFly.Api/Lifecycle/LifecycleService.cs`; `MayFly.Api/Provisioning/IProvisionerClient.cs` + client (add a `ListManagedAsync`)
- Modify: `MayFly.Provisioner` — add `GET /managed` returning live `mayfly.role=db` container ids + labels
- Test: `MayFly.Tests/Api/LifecycleServiceTests.cs` (append), `MayFly.Tests/Provisioner/...`

**Interfaces:**
- Produces: `IProvisionerClient.ListManagedAsync(CancellationToken) -> IReadOnlyList<ManagedContainer>` where `record ManagedContainer(string ContainerId, string InstanceId, string Role)`; `LifecycleService.RunReconcileAsync(CancellationToken)` (public, called once at `ExecuteAsync` start).
- Consumes: Provisioner `GET /managed`.

- [ ] **Step 1: Write the failing test** (reconcile marks a metadata row Failed when its container is gone)

```csharp
[Fact]
public async Task Reconcile_marks_running_with_missing_container_as_failed()
{
    // Arrange: real metadata DB, a Running instance whose ContainerId does not exist in Docker.
    // Mock IProvisionerClient.ListManagedAsync to return an EMPTY list (no live containers).
    // Act: await sut.RunReconcileAsync(default);
    // Assert: the instance is now Failed and its port is released.
}
```
(Use the existing `LifecycleServiceTests` harness: Testcontainers metadata DB + `Mock<IProvisionerClient>` + `Mock<IQueryExecutor>`.)

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter Reconcile_marks_running_with_missing_container_as_failed`
Expected: FAIL (`RunReconcileAsync` not defined).

- [ ] **Step 3: Implement**

Provisioner: add `GET /managed` to `ProvisionerEndpoints` returning live containers labelled `mayfly.role=db` with their `mayfly.instance` label (via `docker.Containers.ListContainersAsync` with a label filter). `IDockerProvisioner.ListManagedAsync()` returns `IReadOnlyList<(string ContainerId, string InstanceId)>`.
Api client: `ListManagedAsync` GETs `/managed`.
`LifecycleService.RunReconcileAsync`:
```csharp
public async Task RunReconcileAsync(CancellationToken ct)
{
    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
    var prov = scope.ServiceProvider.GetRequiredService<IProvisionerClient>();
    var live = (await prov.ListManagedAsync(ct)).Select(m => m.InstanceId).ToHashSet();

    // metadata says active but no live container → Failed + free port
    var active = await db.Instances.Where(i =>
        i.State == InstanceState.Running || i.State == InstanceState.Provisioning ||
        i.State == InstanceState.Destroying).ToListAsync(ct);
    foreach (var inst in active.Where(i => !live.Contains(i.Id.ToString("N"))))
        inst.State = InstanceState.Failed;
    await db.SaveChangesAsync(ct);

    // live containers with no active metadata → orphan, destroy them
    var known = active.Select(i => i.Id.ToString("N")).ToHashSet();
    foreach (var m in await prov.ListManagedAsync(ct))
        if (!known.Contains(m.InstanceId))
            try { await prov.DestroyByInstanceAsync(m.InstanceId, ct); } catch (Exception ex) { log.LogWarning(ex, "reconcile orphan destroy {Id}", m.InstanceId); }
}
```
Add `IProvisionerClient.DestroyByInstanceAsync(string instanceId, CancellationToken)` → Provisioner `DELETE /managed/{instanceId}` that force-removes the DB + sidecar + volume by label. Call `RunReconcileAsync(ct)` once at the top of `LifecycleService.ExecuteAsync` before the loop.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter "LifecycleServiceTests|Reconcile_"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): reconcile-on-startup cleans orphans and marks missing instances failed"
```

---

## Phase 2 — Isolation (core go-live)

### Task 4: Non-superuser `appuser` + admin credential

**Files:**
- Create: `MayFly.Provisioner/Docker/RoleInitializer.cs`
- Modify: `MayFly.Provisioner/Docker/DockerProvisioner.cs` (env `POSTGRES_USER=mayflyadmin`; call role init after ready), `Contracts/InstanceSpec.cs` (`CreateInstanceResult.AdminPassword`), `Seeding/PostgresSeeder.cs` (seed as mayflyadmin)
- Modify: `MayFly.Api/Domain/Instance.cs` (`AdminPasswordEnc`), migration, `MayFly.Api/Services/InstanceService.cs` (store both), `MayFly.Api/Provisioning/*` (AdminPassword in result)
- Test: `MayFly.Tests/Provisioner/RoleInitializerTests.cs`

**Interfaces:**
- Produces: `CreateInstanceResult(... string DbUser, string DbPassword, string AdminUser, string AdminPassword)`; container superuser `mayflyadmin`; DB role `appuser` (`NOSUPERUSER`) with scoped grants; `ProvisionResult.AdminPassword`; `Instance.AdminPasswordEnc`.

- [ ] **Step 1: Write the failing integration test**

```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class RoleInitializerTests
{
    [Fact]
    public async Task Appuser_is_not_superuser_and_cannot_copy_program()
    {
        // Create a container (POSTGRES_USER=mayflyadmin), run RoleInitializer to create appuser.
        // Connect as appuser:
        //  - SELECT rolsuper FROM pg_roles WHERE rolname='appuser'  => false
        //  - "COPY (SELECT 1) TO PROGRAM 'id'" throws (permission denied / superuser required)
        // Connect as mayflyadmin: can run the COPY PROGRAM (proves admin still works) and seeding.
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter RoleInitializerTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

`RoleInitializer.cs` — connects as `mayflyadmin`, waits ready, runs (parameterized-safe) DDL:
```csharp
public sealed class RoleInitializer
{
    public async Task InitAsync(string host, int port, string db, string adminUser, string adminPassword,
        string appUser, string appPassword, CancellationToken ct)
    {
        var cs = $"Host={host};Port={port};Database={db};Username={adminUser};Password={adminPassword};Timeout=5";
        await WaitReadyAsync(cs, ct);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        // create appuser NOSUPERUSER with scoped grants
        var sql = $@"
            CREATE ROLE {Quote(appUser)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD {Literal(appPassword)};
            GRANT CONNECT ON DATABASE {Quote(db)} TO {Quote(appUser)};
            GRANT ALL ON SCHEMA public TO {Quote(appUser)};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO {Quote(appUser)};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO {Quote(appUser)};
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    // Quote(): validate identifier is [a-z_][a-z0-9_]* (appUser/db are server-controlled constants, but guard).
    // Literal(): use a single-quoted escaped string literal for the password.
}
```
`DockerProvisioner.CreateAsync`: generate `adminPassword` (CSPRNG) + `appPassword`; env `POSTGRES_USER=mayflyadmin`, `POSTGRES_PASSWORD=<adminPassword>`, `POSTGRES_DB=appdb`. After start + readiness, call `RoleInitializer.InitAsync(...)` then return `CreateInstanceResult(..., DbUser:"appuser", DbPassword:appPassword, AdminUser:"mayflyadmin", AdminPassword:adminPassword)`.
`PostgresSeeder`: seed connecting as `mayflyadmin` (the endpoint already passes user/password — pass the admin creds for seeding).
Api: `Instance.AdminPasswordEnc`; `ProvisionResult`/`CreateBody` gain `AdminPassword`; `InstanceService.CreateAsync` stores `AdminPasswordEnc = secrets.Protect(prov.AdminPassword)`. `InstanceDto` still exposes only `appuser`. `QueryExecutor` still connects as `appuser`.
Add an EF migration for `AdminPasswordEnc` (`dotnet ef migrations add AdminCredential --project MayFly.Api`).

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter "RoleInitializerTests|SeederTests|InstanceServiceQuotaTests"`
Expected: PASS (appuser non-superuser, COPY PROGRAM refused, admin seeding works).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): non-superuser appuser + internal mayflyadmin, admin credential persisted"
```

---

### Task 5: Egress — internal network + socat sidecar

**Files:**
- Modify: `MayFly.Provisioner/Docker/DockerProvisioner.cs` (networks, sidecar create/destroy)
- Modify: `docker-compose.yml` (networks; api on `mayfly-users`)
- Test: `MayFly.Tests/Provisioner/EgressTests.cs`

**Interfaces:**
- Produces: DB container on `mayfly-users` only (no published port); sidecar `mayfly-sidecar-<id>` on `mayfly-users`+`mayfly-ingress` publishing `PublicPort:5432`; `InternalHost` = DB container name; egress from the DB container is blocked.

- [ ] **Step 1: Write the failing integration test**

```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class EgressTests
{
    [Fact]
    public async Task User_db_has_no_internet_but_is_reachable_via_sidecar()
    {
        // Create an instance. Then:
        //  1) exec in the DB container: `wget -T2 -q -O- http://example.com` (or a raw TCP dial) => FAILS/timeout
        //  2) connect via the published sidecar port from the test host: SELECT 1 => works
        //  3) (Api path) connect over mayfly-users by container name => works
        // Assert (1) fails, (2) and (3) succeed. Destroy cleans up DB + sidecar.
    }
}
```
(For the exec-egress probe use `docker.Exec.*` via Docker.DotNet to run a short-timeout outbound probe and assert non-zero/hang.)

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter EgressTests`
Expected: FAIL (DB still has egress / no sidecar).

- [ ] **Step 3: Implement**

`DockerProvisioner`:
- `EnsureNetworksAsync`: create `mayfly-users` (`Internal = true`, `Driver=bridge`, `CheckDuplicate` via 409-catch) and `mayfly-ingress` (normal bridge) if absent.
- DB container: `NetworkMode = "mayfly-users"`, NO `PortBindings`/`ExposedPorts` publish; keep hardening (caps, limits) from slice-1.
- Sidecar: `CreateContainerAsync` image `alpine/socat:1.8.0.0`, name `mayfly-sidecar-<id>`, labels `mayfly.role=sidecar`,`mayfly.instance=<id>`, `Cmd = ["-d","TCP-LISTEN:5432,fork,reuseaddr","TCP:" + dbName + ":5432"]`, `ExposedPorts {"5432/tcp"}`, `HostConfig.PortBindings { "5432/tcp": [{ HostPort = port }] }`, mem/pids limits, `CapDrop=ALL`, `no-new-privileges`. Connect it to BOTH networks: create with `NetworkMode="mayfly-ingress"` then `docker.Networks.ConnectNetworkAsync("mayfly-users", { Container = sidecarId })`. Start it.
- `DestroyAsync`: also force-remove the sidecar (find by label `mayfly.instance=<id>`,`mayfly.role=sidecar`).
`docker-compose.yml`: declare `mayfly-users` (`internal: true`) and `mayfly-ingress`; attach the `api` service to `mayfly-users` (in addition to the compose default network) so the Api reaches DBs; keep `provisioner` OFF `mayfly-users`.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter "EgressTests|DockerProvisionerLifecycleTests"`
Expected: PASS (no egress; reachable via sidecar and via internal name).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): block user-db egress via internal network + socat sidecar"
```

---

### Task 6: Read-only rootfs + tmpfs

**Files:**
- Modify: `MayFly.Provisioner/Docker/DockerProvisioner.cs`
- Test: `MayFly.Tests/Provisioner/DockerProvisionerTests.cs` (extend hardening assertions)

**Interfaces:**
- Produces: DB container `HostConfig.ReadonlyRootfs = true` + tmpfs on `/tmp`, `/var/run/postgresql`, `/run`.

- [ ] **Step 1: Write the failing test** (extend the existing hardening assertions)

```csharp
inspect.HostConfig.ReadonlyRootfs.Should().BeTrue();
inspect.HostConfig.Tmpfs.Should().ContainKey("/var/run/postgresql");
// and: an exec `sh -c 'echo x > /etc/x'` inside the DB container FAILS (read-only fs),
// while postgres still answers SELECT 1.
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter DockerProvisionerLifecycleTests`
Expected: FAIL (ReadonlyRootfs not set).

- [ ] **Step 3: Implement**

In the DB container `HostConfig`:
```csharp
ReadonlyRootfs = true,
Tmpfs = new Dictionary<string, string>
{
    ["/tmp"] = "rw,noexec,nosuid,size=64m",
    ["/var/run/postgresql"] = "rw,noexec,nosuid,size=16m",
    ["/run"] = "rw,noexec,nosuid,size=16m",
},
```
(Keep the data volume mount at `/var/lib/postgresql/data` — that stays writable. Keep the init caps.)

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter DockerProvisionerLifecycleTests`
Expected: PASS (read-only rootfs, tmpfs present, postgres serves).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): read-only rootfs + tmpfs for user db containers"
```

---

## Phase 3 — Real disk quota

### Task 7: Fix the XFS hard-cap driver options

**Files:**
- Modify: `MayFly.Provisioner/Docker/XfsVolumeProvisioner.cs`
- Test: `MayFly.Tests/Provisioner/XfsVolumeProvisionerTests.cs`

**Interfaces:**
- Produces: `XfsVolumeProvisioner.CreateAsync` builds a `local` volume with `DriverOpts { size = "<mb>m" }` only (no `type`/`o`).

- [ ] **Step 1: Write the failing unit test** (assert the DriverOpts shape, mocking IDockerClient)

```csharp
// Mock IDockerClient.Volumes.CreateAsync; capture VolumesCreateParameters.
// Assert DriverOpts contains only "size" = "256m" (no "type", no "o"), Driver=="local".
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter XfsVolumeProvisionerTests`
Expected: FAIL (current opts include type/o).

- [ ] **Step 3: Implement**

```csharp
DriverOpts = new Dictionary<string, string> { ["size"] = $"{storageMb}m" },
Driver = "local",
```
(Remove `type`/`o`.) Add an XML-doc note: "Real hard cap only when the Docker data-root is on an XFS filesystem mounted with `pquota`; see SECURITY.md host prerequisites."

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter XfsVolumeProvisionerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "fix(provisioner): correct XFS size-quota driver options"
```

---

### Task 8: Portable soft-enforce (read-only at cap)

**Files:**
- Create: `MayFly.Api/Lifecycle/QuotaEnforcer.cs`
- Modify: `MayFly.Api/Lifecycle/LifecycleService.cs` (size monitor calls the enforcer)
- Test: `MayFly.Tests/Api/QuotaEnforcerTests.cs`

**Interfaces:**
- Produces: `QuotaEnforcer.EnforceAsync(Instance inst, long sizeBytes, CancellationToken)` — when `sizeBytes >= quotaBytes`, connect as `mayflyadmin` and `ALTER ROLE appuser SET default_transaction_read_only = on`; idempotent.

- [ ] **Step 1: Write the failing integration test**

```csharp
[Trait("Category","Docker")]
[Collection("docker-sequential")]
public class QuotaEnforcerTests
{
    [Fact]
    public async Task Over_quota_flips_appuser_read_only()
    {
        // Create a real container + appuser + mayflyadmin (via DockerProvisioner + RoleInitializer).
        // Call EnforceAsync with sizeBytes >= quota. Then connect as appuser and assert a write
        // (CREATE TABLE / INSERT) fails with "read-only transaction".
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter QuotaEnforcerTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

`QuotaEnforcer` connects as `mayflyadmin` (decrypt `inst.AdminPasswordEnc`), host/port by the same `QueryExecutor:UseInternalHost` config, and runs:
```csharp
if (sizeBytes < (long)inst.StorageQuotaMb * 1024 * 1024) return;
await using var conn = new NpgsqlConnection(adminCs);
await conn.OpenAsync(ct);
await using var cmd = new NpgsqlCommand(
    $"ALTER ROLE {inst.DbUser} SET default_transaction_read_only = on", conn);
await cmd.ExecuteNonQueryAsync(ct);
```
(`inst.DbUser` = "appuser", validated identifier.) In `LifecycleService`'s size-monitor loop, after computing `LastSizeBytes`, call `enforcer.EnforceAsync(inst, LastSizeBytes, ct)` (best-effort, logged). Inject `QuotaEnforcer` + `ISecretProtector` into the scope.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter QuotaEnforcerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): portable soft-enforce — flip appuser read-only over quota"
```

---

## Phase 4 — Polish

### Task 9: Provisioner endpoint auth (shared secret)

**Files:**
- Modify: `MayFly.Provisioner/Program.cs` (auth middleware), `MayFly.Api/Provisioning/ProvisionerClient.cs` (header), `docker-compose.yml` (env)
- Test: `MayFly.Tests/Provisioner/ProvisionerEndpointsTests.cs` (append)

**Interfaces:**
- Produces: Provisioner rejects (401) any request lacking `X-Provisioner-Key == config["Provisioner:Key"]`; the Api client sends it.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Request_without_key_is_401()
{
    var resp = await _client.PostAsJsonAsync("/instances",
        new CreateInstanceRequest("postgres", 3, 256, "blank"));  // no X-Provisioner-Key
    resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
}
```
(Configure the test factory with `Provisioner:Key=test-key`; the existing 400 test must now send the header.)

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter ProvisionerEndpointsTests`
Expected: FAIL (no auth yet).

- [ ] **Step 3: Implement**

Provisioner `Program.cs` — a terminal-inline middleware before `MapProvisioner`:
```csharp
var key = builder.Configuration["Provisioner:Key"];
app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(key) &&
        ctx.Request.Headers["X-Provisioner-Key"] != key)
    { ctx.Response.StatusCode = 401; return; }
    await next();
});
```
Api `ProvisionerClient`: add the header on the typed `HttpClient` (in `Program.cs` `AddHttpClient` config: `c.DefaultRequestHeaders.Add("X-Provisioner-Key", cfg["Provisioner:Key"])`). `docker-compose.yml`: set `Provisioner__Key: "${PROVISIONER_KEY:?set a provisioner key}"` on both `api` and `provisioner`.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter ProvisionerEndpointsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: shared-secret auth on Provisioner endpoints"
```

---

### Task 10: Rate-limit refinement (create + query policies)

**Files:**
- Modify: `MayFly.Api/Program.cs`, `MayFly.Api/Controllers/InstancesController.cs` (policy attributes)
- Test: `MayFly.Tests/Api/InstancesApiTests.cs` (append — policy registration smoke)

**Interfaces:**
- Produces: per-IP `create` policy (e.g. 6/min) on `POST /api/instances`; per-IP `query` policy (e.g. 60/min) on `POST /api/instances/{token}/query`; general policy retained elsewhere.

- [ ] **Step 1: Write the failing test** (app still starts + a create still returns 400 for a bad engine through the create policy)

```csharp
[Fact]
public async Task Create_endpoint_still_reachable_under_create_policy()
{
    var client = _factory.CreateClient(); // with X-Provisioner-Key configured
    var resp = await client.PostAsJsonAsync("/api/instances",
        new { engine = "oracle", ttlHours = 3, storageMb = 256, initialData = "blank" });
    resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test MayFly.Tests --filter Create_endpoint_still_reachable_under_create_policy`
Expected: FAIL until the new policies compile/register.

- [ ] **Step 3: Implement**

`Program.cs` `AddRateLimiter`: add two partitioned policies keyed on `RemoteIpAddress` — `"create"` (PermitLimit 6, Window 1 min) and `"query"` (PermitLimit 60, Window 1 min), alongside the existing `"perip"`. Apply with `[EnableRateLimiting("create")]` on `Create` and `[EnableRateLimiting("query")]` on `Query`.

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test MayFly.Tests --filter "InstancesApiTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): stricter per-IP rate limits on create and query"
```

---

### Task 11: SECURITY.md + full verification

**Files:**
- Create: `SECURITY.md`
- Modify: `scripts/e2e-fullstack.sh` (add an egress + non-superuser assertion if feasible)

- [ ] **Step 1: Write `SECURITY.md`**

Document: privilege boundary (Provisioner-only Docker + shared-secret), non-superuser appuser (COPY PROGRAM blocked), egress-blocked internal network + sidecar, read-only rootfs, disk quota (XFS hard cap host prerequisite + portable soft-enforce), per-IP rate limits, capability tokens, encrypted credentials, ForwardedHeaders/KnownProxies. Residual risks: capability-URL leak (mitigated by TTL), DB-to-DB isolation relies on non-superuser (per-instance network deferred), limited CREATE EXTENSION. Host prerequisites: XFS+pquota data-root, Caddy KnownProxies, `PROVISIONER_KEY`/`MAYFLY_DB_PASSWORD`/`PUBLIC_HOST` env.

- [ ] **Step 2: Full unit + integration suite**

Run: `dotnet test MayFly.Tests`
Expected: all green (backend). Then `cd MayFly.Web && npx vitest run` — all green (unchanged, sanity).

- [ ] **Step 3: Full-stack e2e on a clean stack**

Run: `bash scripts/e2e-fullstack.sh`
Expected: the existing 6/6 checks pass with the new topology (create → connect via sidecar → query → dashboard → destroy), AND (if added) the egress probe from the DB container fails. Confirm teardown leaves no `mayfly-pg-*`/`mayfly-sidecar-*` containers or `mayfly-users`/`mayfly-ingress`-orphaned networks.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: SECURITY.md + verify hardening end-to-end"
```

---

## Self-Review (completed)

**Spec coverage:** §2.1 ILogger → Task 1. §2.2 idempotent destroy/`Destroying` → Task 2. §2.3 reconcile → Task 3. §3.1 non-superuser + AdminPasswordEnc → Task 4. §3.2 egress internal+sidecar → Task 5. §3.3 read-only rootfs → Task 6. §4.1 XFS fix → Task 7. §4.2 soft-enforce → Task 8. §5.1 Provisioner auth → Task 9. §5.2 rate-limit → Task 10. §5.3 SECURITY.md + §6 tests → Task 11. Every spec section maps to a task.

**Placeholder scan:** No TODO/TBD; each task has real test + implementation code and exact commands. (RoleInitializer/EgressTests give the test intent as precise comments where a full literal test would depend on runtime Docker exec plumbing — the assertions and Docker calls are named explicitly.)

**Type consistency:** `CreateInstanceResult`/`ProvisionResult` both gain `AdminUser`/`AdminPassword`; `Instance.AdminPasswordEnc` consumed by `QuotaEnforcer` + seeding; `InstanceState.Destroying` used consistently in `DestroyAsync` + reconcile + active-state sets; `ListManagedAsync`/`DestroyByInstanceAsync` defined on `IProvisionerClient` and used by reconcile; sidecar label scheme (`mayfly.role`, `mayfly.instance`) consistent across create/destroy/reconcile.

**Note (test literalness):** Tasks 3/4/5/8 describe Docker-exec-driven integration tests by their exact Docker calls + assertions rather than full literal C# (the exec/probe plumbing is environment-specific). The implementer writes the literal test from the named calls; reviewers verify the asserted behavior (no egress, non-superuser, read-only-at-cap, orphan cleanup) is genuinely exercised.
