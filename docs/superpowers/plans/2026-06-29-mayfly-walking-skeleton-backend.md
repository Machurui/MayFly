# MayFly Walking Skeleton — Backend Implementation Plan (Plan 1/2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the real, curl-able backend for MayFly: an internet-facing API that provisions real ephemeral PostgreSQL containers (TTL, storage hard-cap, 3/IP quota), returns a working connection string, executes queries server-side, and auto-destroys instances on expiry.

**Architecture:** Two ASP.NET Core 8 services. `MayFly.Provisioner` is internal-only and the *only* component holding the Docker socket (Approach B privilege boundary); it creates/destroys/inspects hardened Postgres containers behind a narrow HTTP contract. `MayFly.Api` is internet-facing, holds zero Docker capability, persists metadata in PostgreSQL via EF Core, enforces IP quota + capability tokens, runs the lifecycle reaper, and executes user queries over the internal Docker network.

**Tech Stack:** .NET 8 (8.0.406), ASP.NET Core Web API, EF Core 8 + Npgsql, Docker.DotNet, ASP.NET Data Protection, xUnit + FluentAssertions + Moq + Testcontainers.

## Global Constraints

- **Runtime:** .NET 8 LTS (SDK 8.0.406). All projects `net8.0`.
- **Privilege boundary:** Only `MayFly.Provisioner` references `Docker.DotNet` or touches the Docker socket. `MayFly.Api` MUST NOT reference `Docker.DotNet`.
- **Allowed values (verbatim, validated server-side):** engine ∈ {`postgres`}; ttlHours ∈ {3, 6, 12}; storageMb ∈ {256, 512, 1024, 2048}; initialData ∈ {`blank`, `northwind`}.
- **Public DB port range:** 20000–21000 inclusive.
- **Times:** all timestamps UTC, stored absolute.
- **Secrets:** DB passwords encrypted at rest via ASP.NET Data Protection; never logged.
- **Capability tokens:** ≥128-bit CSPRNG; compared in constant time; never logged.
- **Dev/prod storage split:** storage hard-cap (XFS project quota) is Linux-only. Volume creation is abstracted behind `IVolumeProvisioner`; dev/macOS uses a plain-volume impl, prod uses the XFS impl. Selected by config, not `#if`.
- **Engine container image:** `postgres:16-alpine`.

---

## File Structure

```
MayFly/
  MayFly.sln
  .gitignore
  docker-compose.yml                      # caddy, web, api, provisioner, metadata-db
  Caddyfile
  MayFly.Provisioner/
    MayFly.Provisioner.csproj
    Program.cs                            # minimal API, internal binding
    Contracts/InstanceSpec.cs             # CreateInstanceRequest/Result, InspectResult records
    Validation/InstanceSpecValidator.cs   # whitelist engine/ttl/storage/initialData
    Docker/IPortAllocator.cs
    Docker/PortAllocator.cs               # range 20000-21000, thread-safe
    Docker/IVolumeProvisioner.cs
    Docker/PlainVolumeProvisioner.cs      # dev: plain docker volume
    Docker/XfsVolumeProvisioner.cs        # prod: XFS project-quota volume
    Docker/IDockerProvisioner.cs
    Docker/DockerProvisioner.cs           # Docker.DotNet create/destroy/inspect, hardening
    Endpoints/ProvisionerEndpoints.cs     # POST /instances, DELETE, GET inspect
  MayFly.Api/
    MayFly.Api.csproj
    Program.cs
    appsettings.json / appsettings.Development.json
    Data/MayFlyContext.cs                 # EF Core DbContext
    Domain/Instance.cs                    # entity + InstanceState enum
    Domain/QueryLog.cs
    Dtos/*.cs                             # CreateInstanceDto, InstanceDto, ConnectionInfoDto, QueryRequestDto, QueryResultDto, DashboardDto
    Security/ITokenService.cs / TokenService.cs
    Security/SecretProtector.cs           # Data Protection wrapper
    Security/SessionCookieMiddleware.cs   # anon session id cookie
    Provisioning/IProvisionerClient.cs / ProvisionerClient.cs  # HTTP -> Provisioner
    Services/IInstanceService.cs / InstanceService.cs
    Services/IQueryExecutor.cs / QueryExecutor.cs  # Npgsql over internal network
    Lifecycle/LifecycleService.cs         # BackgroundService: reaper + monitor + reconcile
    Controllers/InstancesController.cs
    Controllers/DashboardController.cs
  MayFly.Tests/
    MayFly.Tests.csproj
    Provisioner/InstanceSpecValidatorTests.cs
    Provisioner/PortAllocatorTests.cs
    Provisioner/DockerProvisionerTests.cs        # integration, real Docker
    Api/TokenServiceTests.cs
    Api/InstanceServiceTests.cs                  # Testcontainers Postgres metadata
    Api/QueryExecutorTests.cs                    # integration, real Docker
    Api/LifecycleServiceTests.cs
```

---

## Phase 0 — Scaffolding

### Task 1: Solution, projects, .gitignore

**Files:**
- Create: `MayFly.sln`, `MayFly.Provisioner/`, `MayFly.Api/`, `MayFly.Tests/`, `.gitignore`

- [ ] **Step 1: Scaffold solution and projects**

Run from `MayFly/`:
```bash
dotnet new sln -n MayFly
dotnet new webapi -n MayFly.Provisioner -f net8.0 --no-openapi false
dotnet new webapi -n MayFly.Api -f net8.0 --no-openapi false
dotnet new xunit -n MayFly.Tests -f net8.0
dotnet sln add MayFly.Provisioner MayFly.Api MayFly.Tests
dotnet add MayFly.Tests reference MayFly.Provisioner MayFly.Api
```

- [ ] **Step 2: Add packages**

```bash
dotnet add MayFly.Provisioner package Docker.DotNet
dotnet add MayFly.Api package Npgsql
dotnet add MayFly.Api package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add MayFly.Api package Microsoft.EntityFrameworkCore.Design
dotnet add MayFly.Tests package FluentAssertions
dotnet add MayFly.Tests package Moq
dotnet add MayFly.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add MayFly.Tests package Testcontainers.PostgreSql
```

- [ ] **Step 3: Add `.gitignore`**

Create `.gitignore`:
```gitignore
bin/
obj/
*.user
.vs/
node_modules/
dist/
.env
appsettings.*.local.json
```

- [ ] **Step 4: Verify build**

Run: `dotnet build MayFly.sln`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore: scaffold MayFly solution (Api, Provisioner, Tests)"
```

---

## Phase 1 — Provisioner (privileged core)

### Task 2: Provisioner contracts + spec validation

**Files:**
- Create: `MayFly.Provisioner/Contracts/InstanceSpec.cs`, `MayFly.Provisioner/Validation/InstanceSpecValidator.cs`
- Test: `MayFly.Tests/Provisioner/InstanceSpecValidatorTests.cs`

**Interfaces:**
- Produces: `CreateInstanceRequest(string Engine, int TtlHours, int StorageMb, string InitialData)`, `CreateInstanceResult(string ContainerId, string VolumeName, string InternalHost, int PublicPort, string DbName, string DbUser, string DbPassword)`, `InspectResult(string State, long SizeBytes)`, `InstanceSpecValidator.Validate(CreateInstanceRequest) -> (bool Ok, string? Error)`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Provisioner/InstanceSpecValidatorTests.cs`:
```csharp
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Validation;
using Xunit;

public class InstanceSpecValidatorTests
{
    [Theory]
    [InlineData("postgres", 3, 256, "blank")]
    [InlineData("postgres", 12, 2048, "northwind")]
    public void Validate_accepts_allowed_values(string e, int ttl, int mb, string init)
        => InstanceSpecValidator.Validate(new CreateInstanceRequest(e, ttl, mb, init)).Ok.Should().BeTrue();

    [Theory]
    [InlineData("mysql", 3, 256, "blank")]      // engine not allowed in slice 1
    [InlineData("postgres", 5, 256, "blank")]   // ttl not allowed
    [InlineData("postgres", 3, 999, "blank")]   // storage not allowed
    [InlineData("postgres", 3, 256, "ecom")]    // initialData not allowed
    public void Validate_rejects_out_of_whitelist(string e, int ttl, int mb, string init)
        => InstanceSpecValidator.Validate(new CreateInstanceRequest(e, ttl, mb, init)).Ok.Should().BeFalse();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter InstanceSpecValidatorTests`
Expected: FAIL (types `CreateInstanceRequest` / `InstanceSpecValidator` do not exist).

- [ ] **Step 3: Write minimal implementation**

`MayFly.Provisioner/Contracts/InstanceSpec.cs`:
```csharp
namespace MayFly.Provisioner.Contracts;

public record CreateInstanceRequest(string Engine, int TtlHours, int StorageMb, string InitialData);
public record CreateInstanceResult(string ContainerId, string VolumeName, string InternalHost,
                                   int PublicPort, string DbName, string DbUser, string DbPassword);
public record InspectResult(string State, long SizeBytes);
```

`MayFly.Provisioner/Validation/InstanceSpecValidator.cs`:
```csharp
using MayFly.Provisioner.Contracts;

namespace MayFly.Provisioner.Validation;

public static class InstanceSpecValidator
{
    private static readonly HashSet<string> Engines = new() { "postgres" };
    private static readonly HashSet<int> Ttls = new() { 3, 6, 12 };
    private static readonly HashSet<int> Storage = new() { 256, 512, 1024, 2048 };
    private static readonly HashSet<string> InitialData = new() { "blank", "northwind" };

    public static (bool Ok, string? Error) Validate(CreateInstanceRequest r)
    {
        if (!Engines.Contains(r.Engine)) return (false, $"engine '{r.Engine}' not allowed");
        if (!Ttls.Contains(r.TtlHours)) return (false, $"ttl '{r.TtlHours}' not allowed");
        if (!Storage.Contains(r.StorageMb)) return (false, $"storage '{r.StorageMb}' not allowed");
        if (!InitialData.Contains(r.InitialData)) return (false, $"initialData '{r.InitialData}' not allowed");
        return (true, null);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter InstanceSpecValidatorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): instance spec contracts + whitelist validation"
```

---

### Task 3: Port allocator (range 20000–21000, thread-safe)

**Files:**
- Create: `MayFly.Provisioner/Docker/IPortAllocator.cs`, `MayFly.Provisioner/Docker/PortAllocator.cs`
- Test: `MayFly.Tests/Provisioner/PortAllocatorTests.cs`

**Interfaces:**
- Produces: `IPortAllocator { int Allocate(); void Release(int port); }`. `Allocate` returns a free port in [20000,21000] or throws `InvalidOperationException` when exhausted. Seedable with already-used ports via constructor `PortAllocator(IEnumerable<int> inUse)`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Provisioner/PortAllocatorTests.cs`:
```csharp
using System.Collections.Concurrent;
using FluentAssertions;
using MayFly.Provisioner.Docker;
using Xunit;

public class PortAllocatorTests
{
    [Fact]
    public void Allocate_returns_port_in_range()
    {
        var p = new PortAllocator(Array.Empty<int>()).Allocate();
        p.Should().BeInRange(20000, 21000);
    }

    [Fact]
    public void Allocate_skips_ports_in_use()
        => new PortAllocator(new[] { 20000 }).Allocate().Should().NotBe(20000);

    [Fact]
    public void Released_port_can_be_reallocated()
    {
        var a = new PortAllocator(Array.Empty<int>());
        var taken = new HashSet<int>();
        for (int i = 0; i <= 1000; i++) taken.Add(a.Allocate());
        Action exhausted = () => a.Allocate();
        exhausted.Should().Throw<InvalidOperationException>();
        a.Release(20500);
        a.Allocate().Should().Be(20500);
    }

    [Fact]
    public void Allocate_is_unique_under_concurrency()
    {
        var a = new PortAllocator(Array.Empty<int>());
        var bag = new ConcurrentBag<int>();
        Parallel.For(0, 500, _ => bag.Add(a.Allocate()));
        bag.Distinct().Count().Should().Be(500);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter PortAllocatorTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Write minimal implementation**

`MayFly.Provisioner/Docker/IPortAllocator.cs`:
```csharp
namespace MayFly.Provisioner.Docker;
public interface IPortAllocator { int Allocate(); void Release(int port); }
```

`MayFly.Provisioner/Docker/PortAllocator.cs`:
```csharp
namespace MayFly.Provisioner.Docker;

public sealed class PortAllocator : IPortAllocator
{
    private const int Min = 20000, Max = 21000;
    private readonly HashSet<int> _used;
    private readonly object _lock = new();

    public PortAllocator(IEnumerable<int> inUse) => _used = new HashSet<int>(inUse);

    public int Allocate()
    {
        lock (_lock)
        {
            for (int p = Min; p <= Max; p++)
                if (_used.Add(p)) return p;
            throw new InvalidOperationException("port range exhausted");
        }
    }

    public void Release(int port) { lock (_lock) _used.Remove(port); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter PortAllocatorTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): thread-safe port allocator (20000-21000)"
```

---

### Task 4: Volume provisioner abstraction (dev plain + prod XFS)

**Files:**
- Create: `MayFly.Provisioner/Docker/IVolumeProvisioner.cs`, `Docker/PlainVolumeProvisioner.cs`, `Docker/XfsVolumeProvisioner.cs`
- Test: `MayFly.Tests/Provisioner/DockerProvisionerTests.cs` (volume portion; integration — real Docker)

**Interfaces:**
- Produces: `IVolumeProvisioner { Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct); Task DestroyAsync(string volumeName, CancellationToken ct); }`. Returns the Docker volume name to mount at `/var/lib/postgresql/data`.

- [ ] **Step 1: Write the failing integration test**

Add to `MayFly.Tests/Provisioner/DockerProvisionerTests.cs`:
```csharp
using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Docker;
using Xunit;

[Trait("Category", "Docker")]
public class VolumeProvisionerTests
{
    [Fact]
    public async Task Plain_create_and_destroy_roundtrips()
    {
        var sut = new PlainVolumeProvisioner(
            new DockerClientConfiguration().CreateClient());
        var name = await sut.CreateAsync(Guid.NewGuid().ToString("N"), 256, default);
        name.Should().StartWith("mayfly-vol-");
        await sut.DestroyAsync(name, default);   // must not throw
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter VolumeProvisionerTests`
Expected: FAIL (types not found).

- [ ] **Step 3: Write implementations**

`MayFly.Provisioner/Docker/IVolumeProvisioner.cs`:
```csharp
namespace MayFly.Provisioner.Docker;
public interface IVolumeProvisioner
{
    Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct);
    Task DestroyAsync(string volumeName, CancellationToken ct);
}
```

`MayFly.Provisioner/Docker/PlainVolumeProvisioner.cs` (dev/macOS — no hard cap, monitor still reports size):
```csharp
using Docker.DotNet;
using Docker.DotNet.Models;

namespace MayFly.Provisioner.Docker;

public sealed class PlainVolumeProvisioner(IDockerClient docker) : IVolumeProvisioner
{
    public async Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct)
    {
        var name = $"mayfly-vol-{instanceId}";
        await docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Labels = new Dictionary<string, string> { ["mayfly.instance"] = instanceId }
        }, ct);
        return name;
    }

    public Task DestroyAsync(string volumeName, CancellationToken ct)
        => docker.Volumes.RemoveAsync(volumeName, force: true, ct);
}
```

`MayFly.Provisioner/Docker/XfsVolumeProvisioner.cs` (prod — hard cap via XFS project quota on the `local` driver with `size` opt; requires data-root on XFS+pquota):
```csharp
using Docker.DotNet;
using Docker.DotNet.Models;

namespace MayFly.Provisioner.Docker;

public sealed class XfsVolumeProvisioner(IDockerClient docker) : IVolumeProvisioner
{
    public async Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct)
    {
        var name = $"mayfly-vol-{instanceId}";
        await docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Driver = "local",
            DriverOpts = new Dictionary<string, string>
            {
                ["type"] = "xfs",
                ["o"] = "pquota",
                ["size"] = $"{storageMb}m"
            },
            Labels = new Dictionary<string, string> { ["mayfly.instance"] = instanceId }
        }, ct);
        return name;
    }

    public Task DestroyAsync(string volumeName, CancellationToken ct)
        => docker.Volumes.RemoveAsync(volumeName, force: true, ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter VolumeProvisionerTests`
Expected: PASS (requires Docker running locally).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): volume provisioner abstraction (plain dev / XFS prod hard-cap)"
```

---

### Task 5: Docker provisioner — create hardened Postgres, inspect, destroy

**Files:**
- Create: `MayFly.Provisioner/Docker/IDockerProvisioner.cs`, `Docker/DockerProvisioner.cs`
- Test: `MayFly.Tests/Provisioner/DockerProvisionerTests.cs` (lifecycle portion)

**Interfaces:**
- Consumes: `IPortAllocator`, `IVolumeProvisioner`, `IDockerClient`, `CreateInstanceRequest`, `CreateInstanceResult`, `InspectResult`.
- Produces: `IDockerProvisioner { Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest, CancellationToken); Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken); Task<InspectResult> InspectAsync(string containerId, CancellationToken); }`. Container joins Docker network `mayfly-internal`; `InternalHost` = container name; image `postgres:16-alpine`.

- [ ] **Step 1: Write the failing integration test**

Add to `MayFly.Tests/Provisioner/DockerProvisionerTests.cs`:
```csharp
using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
public class DockerProvisionerLifecycleTests
{
    private static IDockerProvisioner NewSut()
    {
        var docker = new DockerClientConfiguration().CreateClient();
        return new DockerProvisioner(docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker));
    }

    [Fact]
    public async Task Create_yields_reachable_postgres_then_destroy_cleans_up()
    {
        var sut = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        try
        {
            res.PublicPort.Should().BeInRange(20000, 21000);
            // connect via the published public port (localhost in dev)
            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.DbUser};Password={res.DbPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            (await cmd.ExecuteScalarAsync()).Should().Be(1);

            var inspect = await sut.InspectAsync(res.ContainerId, default);
            inspect.State.Should().Be("running");
            inspect.SizeBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForPostgresAsync(string cs)
    {
        for (int i = 0; i < 30; i++)
        {
            try { await using var c = new NpgsqlConnection(cs); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new TimeoutException("postgres did not become ready");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter DockerProvisionerLifecycleTests`
Expected: FAIL (`DockerProvisioner` not found).

- [ ] **Step 3: Write minimal implementation**

`MayFly.Provisioner/Docker/IDockerProvisioner.cs`:
```csharp
using MayFly.Provisioner.Contracts;
namespace MayFly.Provisioner.Docker;

public interface IDockerProvisioner
{
    Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct);
    Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken ct);
    Task<InspectResult> InspectAsync(string containerId, CancellationToken ct);
}
```

`MayFly.Provisioner/Docker/DockerProvisioner.cs`:
```csharp
using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using MayFly.Provisioner.Contracts;

namespace MayFly.Provisioner.Docker;

public sealed class DockerProvisioner(
    IDockerClient docker, IPortAllocator ports, IVolumeProvisioner volumes) : IDockerProvisioner
{
    private const string Image = "postgres:16-alpine";
    private const string Network = "mayfly-internal";

    public async Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var name = $"mayfly-pg-{id}";
        var dbUser = "appuser";
        var dbName = "appdb";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var port = ports.Allocate();
        string? volume = null;

        try
        {
            await EnsureImageAsync(ct);
            await EnsureNetworkAsync(ct);
            volume = await volumes.CreateAsync(id, req.StorageMb, ct);

            var create = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = name,
                Hostname = name,
                Labels = new Dictionary<string, string> { ["mayfly.instance"] = id },
                Env = new List<string>
                {
                    $"POSTGRES_USER={dbUser}",
                    $"POSTGRES_PASSWORD={password}",
                    $"POSTGRES_DB={dbName}"
                },
                ExposedPorts = new Dictionary<string, EmptyStruct> { ["5432/tcp"] = default },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["5432/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } }
                    },
                    Mounts = new List<Mount>
                    {
                        new() { Type = "volume", Source = volume, Target = "/var/lib/postgresql/data" }
                    },
                    NetworkMode = Network,
                    Memory = 256L * 1024 * 1024,
                    NanoCPUs = 500_000_000,            // 0.5 CPU
                    PidsLimit = 200,
                    CapDropList = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                }
            }, ct);

            await docker.Containers.StartContainerAsync(create.ID, new ContainerStartParameters(), ct);

            return new CreateInstanceResult(create.ID, volume!, name, port, dbName, dbUser, password);
        }
        catch
        {
            ports.Release(port);
            if (volume is not null) { try { await volumes.DestroyAsync(volume, ct); } catch { } }
            throw;
        }
    }

    public async Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken ct)
    {
        try { await docker.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true }, ct); } catch { }
        try { await volumes.DestroyAsync(volumeName, ct); } catch { }
        ports.Release(publicPort);
    }

    public async Task<InspectResult> InspectAsync(string containerId, CancellationToken ct)
    {
        var c = await docker.Containers.InspectContainerAsync(containerId, ct);
        long size = c.SizeRw ?? 0;   // requires size=true on list; fallback below in monitor
        return new InspectResult(c.State.Running ? "running" : c.State.Status, size);
    }

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        var existing = await docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        if (existing.Any(i => i.RepoTags?.Contains(Image) == true)) return;
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "postgres", Tag = "16-alpine" },
            null, new Progress<JSONMessage>(), ct);
    }

    private async Task EnsureNetworkAsync(CancellationToken ct)
    {
        var nets = await docker.Networks.ListNetworksAsync(cancellationToken: ct);
        if (nets.Any(n => n.Name == Network)) return;
        await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = Network,
            Driver = "bridge",
            Internal = false,           // false: containers need outbound port publish; isolation via icc
            Options = new Dictionary<string, string> { ["com.docker.network.bridge.enable_icc"] = "false" }
        }, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter DockerProvisionerLifecycleTests`
Expected: PASS (pulls `postgres:16-alpine` first run; may take ~30s).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): hardened Postgres container create/inspect/destroy"
```

---

### Task 6: Provisioner HTTP endpoints + DI + internal binding

**Files:**
- Create: `MayFly.Provisioner/Endpoints/ProvisionerEndpoints.cs`
- Modify: `MayFly.Provisioner/Program.cs`

**Interfaces:**
- Produces HTTP contract (consumed by `MayFly.Api`):
  - `POST /instances` body `CreateInstanceRequest` → 200 `CreateInstanceResult` | 400 `{error}`
  - `DELETE /instances/{containerId}?volume={v}&port={p}` → 204
  - `GET /instances/{containerId}` → 200 `InspectResult` | 404

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Provisioner/ProvisionerEndpointsTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ProvisionerEndpointsTests : IClassFixture<WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker>>
{
    private readonly HttpClient _client;
    public ProvisionerEndpointsTests(WebApplicationFactory<MayFly.Provisioner.IProvisionerMarker> f)
        => _client = f.CreateClient();

    [Fact]
    public async Task Create_rejects_disallowed_engine_with_400()
    {
        var resp = await _client.PostAsJsonAsync("/instances",
            new CreateInstanceRequest("mysql", 3, 256, "blank"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter ProvisionerEndpointsTests`
Expected: FAIL (marker/endpoints not found).

- [ ] **Step 3: Write implementation**

`MayFly.Provisioner/Endpoints/ProvisionerEndpoints.cs`:
```csharp
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Validation;

namespace MayFly.Provisioner.Endpoints;

public static class ProvisionerEndpoints
{
    public static void MapProvisioner(this WebApplication app)
    {
        app.MapPost("/instances", async (CreateInstanceRequest req, IDockerProvisioner p, CancellationToken ct) =>
        {
            var (ok, error) = InstanceSpecValidator.Validate(req);
            if (!ok) return Results.BadRequest(new { error });
            var result = await p.CreateAsync(req, ct);
            return Results.Ok(result);
        });

        app.MapDelete("/instances/{containerId}", async (string containerId, string volume, int port,
            IDockerProvisioner p, CancellationToken ct) =>
        {
            await p.DestroyAsync(containerId, volume, port, ct);
            return Results.NoContent();
        });

        app.MapGet("/instances/{containerId}", async (string containerId, IDockerProvisioner p, CancellationToken ct) =>
        {
            try { return Results.Ok(await p.InspectAsync(containerId, ct)); }
            catch { return Results.NotFound(); }
        });
    }
}
```

`MayFly.Provisioner/Program.cs` (replace generated content):
```csharp
using Docker.DotNet;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Endpoints;

namespace MayFly.Provisioner;
public interface IProvisionerMarker { }   // marker for WebApplicationFactory

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<IDockerClient>(_ =>
            new DockerClientConfiguration().CreateClient());
        builder.Services.AddSingleton<IPortAllocator>(_ => new PortAllocator(Array.Empty<int>()));
        var useXfs = builder.Configuration.GetValue("Provisioner:UseXfsQuota", false);
        builder.Services.AddSingleton<IVolumeProvisioner>(sp =>
        {
            var d = sp.GetRequiredService<IDockerClient>();
            return useXfs ? new XfsVolumeProvisioner(d) : new PlainVolumeProvisioner(d);
        });
        builder.Services.AddSingleton<IDockerProvisioner, DockerProvisioner>();

        var app = builder.Build();
        app.MapProvisioner();
        app.Run();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter ProvisionerEndpointsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): HTTP endpoints + DI wiring"
```

---

### Task 6b: Initial-data seeding (blank no-op + Northwind subset)

**Files:**
- Create: `MayFly.Provisioner/Seeding/IInitialDataSeeder.cs`, `Seeding/PostgresSeeder.cs`, `Seeding/northwind.sql` (embedded resource)
- Modify: `MayFly.Provisioner/Endpoints/ProvisionerEndpoints.cs` (call seeder after create), `MayFly.Provisioner/Program.cs` (register seeder), `MayFly.Provisioner.csproj` (embed sql)
- Test: `MayFly.Tests/Provisioner/SeederTests.cs`

**Interfaces:**
- Produces: `IInitialDataSeeder { Task SeedAsync(string initialData, string host, int port, string db, string user, string password, CancellationToken ct); }`. `blank` = no-op; `northwind` = applies a compact Northwind subset (categories, products, customers, orders). The endpoint resolves `(host, port)`: dev = `localhost` + `PublicPort`; prod = `InternalHost` + `5432`, chosen by config `Provisioner:UseInternalHost`.

> Slice-1 scope: a *compact* Northwind subset (enough for the console to show real tables/rows). The full template library (full Northwind, E-commerce, Blog, IoT, import dump) is sub-project 3.

- [ ] **Step 1: Write the failing integration test**

`MayFly.Tests/Provisioner/SeederTests.cs`:
```csharp
using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Seeding;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
public class SeederTests
{
    [Fact]
    public async Task Northwind_seed_creates_products_table_with_rows()
    {
        var docker = new DockerClientConfiguration().CreateClient();
        var prov = new DockerProvisioner(docker, new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker));
        var r = await prov.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "northwind"), default);
        try
        {
            var seeder = new PostgresSeeder();
            await seeder.SeedAsync("northwind", "localhost", r.PublicPort, r.DbName, r.DbUser, r.DbPassword, default);

            var cs = $"Host=localhost;Port={r.PublicPort};Database={r.DbName};Username={r.DbUser};Password={r.DbPassword}";
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM products", conn);
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().BeGreaterThan(0);
        }
        finally { await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default); }
    }

    [Fact]
    public async Task Blank_seed_is_noop()
    {
        var seeder = new PostgresSeeder();
        // host unreachable on purpose; blank must return without connecting
        await seeder.SeedAsync("blank", "localhost", 1, "x", "x", "x", default);  // must not throw
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter SeederTests`
Expected: FAIL (types not found).

- [ ] **Step 3: Write implementation**

`MayFly.Provisioner/Seeding/IInitialDataSeeder.cs`:
```csharp
namespace MayFly.Provisioner.Seeding;

public interface IInitialDataSeeder
{
    Task SeedAsync(string initialData, string host, int port, string db, string user, string password, CancellationToken ct);
}
```

`MayFly.Provisioner/Seeding/northwind.sql` (compact subset):
```sql
CREATE TABLE categories (id serial PRIMARY KEY, name text NOT NULL);
CREATE TABLE products (id serial PRIMARY KEY, name text NOT NULL, category_id int REFERENCES categories(id), price numeric(10,2));
CREATE TABLE customers (id serial PRIMARY KEY, company text NOT NULL, country text);
CREATE TABLE orders (id serial PRIMARY KEY, customer_id int REFERENCES customers(id), ordered_at date, total numeric(10,2));
INSERT INTO categories(name) VALUES ('Beverages'),('Condiments'),('Confections');
INSERT INTO products(name,category_id,price) VALUES
  ('Chai',1,18.00),('Chang',1,19.00),('Aniseed Syrup',2,10.00),('Chocolade',3,12.75);
INSERT INTO customers(company,country) VALUES ('Alfreds',' Germany'),('Around the Horn','UK'),('Bottom-Dollar','Canada');
INSERT INTO orders(customer_id,ordered_at,total) VALUES (1,'2026-01-10',54.00),(2,'2026-02-02',38.00);
```

`MayFly.Provisioner/Seeding/PostgresSeeder.cs`:
```csharp
using System.Reflection;
using Npgsql;

namespace MayFly.Provisioner.Seeding;

public sealed class PostgresSeeder : IInitialDataSeeder
{
    public async Task SeedAsync(string initialData, string host, int port, string db, string user,
        string password, CancellationToken ct)
    {
        if (initialData == "blank") return;
        var sql = initialData switch
        {
            "northwind" => ReadEmbedded("northwind.sql"),
            _ => throw new ArgumentException($"unknown initialData '{initialData}'")
        };
        var cs = $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5";
        await WaitReadyAsync(cs, ct);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WaitReadyAsync(string cs, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            try { await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return; }
            catch { await Task.Delay(1000, ct); }
        }
        throw new TimeoutException("postgres not ready for seeding");
    }

    private static string ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().Single(n => n.EndsWith(name));
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
```

`MayFly.Provisioner/MayFly.Provisioner.csproj` add inside an `<ItemGroup>`:
```xml
<EmbeddedResource Include="Seeding/northwind.sql" />
```

Modify `ProvisionerEndpoints.cs` POST handler to seed after create:
```csharp
app.MapPost("/instances", async (CreateInstanceRequest req, IDockerProvisioner p,
    IInitialDataSeeder seeder, IConfiguration cfg, CancellationToken ct) =>
{
    var (ok, error) = InstanceSpecValidator.Validate(req);
    if (!ok) return Results.BadRequest(new { error });
    var result = await p.CreateAsync(req, ct);
    var useInternal = cfg.GetValue("Provisioner:UseInternalHost", true);
    var host = useInternal ? result.InternalHost : "localhost";
    var port = useInternal ? 5432 : result.PublicPort;
    await seeder.SeedAsync(req.InitialData, host, port, result.DbName, result.DbUser, result.DbPassword, ct);
    return Results.Ok(result);
});
```

Register in `MayFly.Provisioner/Program.cs`: `builder.Services.AddSingleton<IInitialDataSeeder, PostgresSeeder>();`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter SeederTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(provisioner): initial-data seeding (blank no-op + Northwind subset)"
```

---

## Phase 2 — Api: metadata, security, services

### Task 7: EF Core domain model + DbContext + migration

**Files:**
- Create: `MayFly.Api/Domain/Instance.cs`, `Domain/QueryLog.cs`, `Data/MayFlyContext.cs`
- Modify: `MayFly.Api/Program.cs` (register DbContext), `appsettings.Development.json`
- Test: `MayFly.Tests/Api/InstanceServiceTests.cs` (context creation portion)

**Interfaces:**
- Produces: `InstanceState { Provisioning, Running, Expired, Destroyed, Failed }`; `Instance` entity (fields per spec §7); `QueryLog` entity; `MayFlyContext : DbContext` with `DbSet<Instance> Instances`, `DbSet<QueryLog> QueryLogs`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Api/InstanceServiceTests.cs` (initial):
```csharp
using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class MetadataContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private MayFlyContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options;
        var ctx = new MayFlyContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task Can_persist_and_load_instance()
    {
        await using var ctx = NewContext();
        var inst = new Instance
        {
            CapabilityToken = "tok", SessionId = "sess", CreatorIp = "1.2.3.4",
            Engine = "postgres", TtlHours = 3, StorageQuotaMb = 256, InitialData = "blank",
            ContainerId = "c", InternalHost = "h", PublicPort = 20000,
            DbName = "appdb", DbUser = "appuser", DbPasswordEnc = "enc",
            State = InstanceState.Running, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(3)
        };
        ctx.Instances.Add(inst);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Instances.SingleAsync(i => i.CapabilityToken == "tok");
        loaded.PublicPort.Should().Be(20000);
        loaded.State.Should().Be(InstanceState.Running);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter MetadataContextTests`
Expected: FAIL (types not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Domain/Instance.cs`:
```csharp
namespace MayFly.Api.Domain;

public enum InstanceState { Provisioning, Running, Expired, Destroyed, Failed }

public class Instance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CapabilityToken { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CreatorIp { get; set; } = "";
    public string Engine { get; set; } = "postgres";
    public int TtlHours { get; set; }
    public int StorageQuotaMb { get; set; }
    public string InitialData { get; set; } = "blank";
    public string ContainerId { get; set; } = "";
    public string VolumeName { get; set; } = "";
    public string InternalHost { get; set; } = "";
    public int PublicPort { get; set; }
    public string DbName { get; set; } = "";
    public string DbUser { get; set; } = "";
    public string DbPasswordEnc { get; set; } = "";
    public InstanceState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public long LastSizeBytes { get; set; }
}
```

`MayFly.Api/Domain/QueryLog.cs`:
```csharp
namespace MayFly.Api.Domain;

public class QueryLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public DateTime ExecutedAt { get; set; }
    public int DurationMs { get; set; }
    public int RowCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
```

`MayFly.Api/Data/MayFlyContext.cs`:
```csharp
using MayFly.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Data;

public class MayFlyContext(DbContextOptions<MayFlyContext> options) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<QueryLog> QueryLogs => Set<QueryLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Instance>().HasIndex(i => i.CapabilityToken).IsUnique();
        b.Entity<Instance>().HasIndex(i => i.SessionId);
        b.Entity<Instance>().HasIndex(i => new { i.CreatorIp, i.State });
        b.Entity<Instance>().Property(i => i.State).HasConversion<string>();
        b.Entity<QueryLog>().HasIndex(q => q.InstanceId);
    }
}
```

Register in `MayFly.Api/Program.cs` (add after `CreateBuilder`):
```csharp
builder.Services.AddDbContext<MayFlyContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Metadata")));
```

`appsettings.Development.json` add:
```json
{ "ConnectionStrings": { "Metadata": "Host=localhost;Port=5433;Database=mayfly;Username=mayfly;Password=mayfly" } }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter MetadataContextTests`
Expected: PASS.

- [ ] **Step 5: Create initial EF migration & commit**

```bash
dotnet ef migrations add InitialCreate --project MayFly.Api
git add -A && git commit -m "feat(api): EF Core metadata model (Instance, QueryLog) + migration"
```

---

### Task 8: Token service (CSPRNG + constant-time compare)

**Files:**
- Create: `MayFly.Api/Security/ITokenService.cs`, `Security/TokenService.cs`
- Test: `MayFly.Tests/Api/TokenServiceTests.cs`

**Interfaces:**
- Produces: `ITokenService { string NewToken(); bool Matches(string candidate, string stored); }`. Token is URL-safe base64 of 32 random bytes (256-bit).

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Api/TokenServiceTests.cs`:
```csharp
using FluentAssertions;
using MayFly.Api.Security;
using Xunit;

public class TokenServiceTests
{
    private readonly TokenService _sut = new();

    [Fact]
    public void NewToken_is_long_and_urlsafe()
    {
        var t = _sut.NewToken();
        t.Length.Should().BeGreaterThanOrEqualTo(43);   // 32 bytes base64url
        t.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void NewToken_is_unique() => _sut.NewToken().Should().NotBe(_sut.NewToken());

    [Fact]
    public void Matches_true_for_equal_false_otherwise()
    {
        var t = _sut.NewToken();
        _sut.Matches(t, t).Should().BeTrue();
        _sut.Matches(t, _sut.NewToken()).Should().BeFalse();
        _sut.Matches("short", t).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter TokenServiceTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Security/ITokenService.cs`:
```csharp
namespace MayFly.Api.Security;
public interface ITokenService { string NewToken(); bool Matches(string candidate, string stored); }
```

`MayFly.Api/Security/TokenService.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace MayFly.Api.Security;

public sealed class TokenService : ITokenService
{
    public string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public bool Matches(string candidate, string stored)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(stored));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter TokenServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): capability token service (256-bit CSPRNG, constant-time)"
```

---

### Task 9: Provisioner client (Api → Provisioner HTTP)

**Files:**
- Create: `MayFly.Api/Provisioning/IProvisionerClient.cs`, `Provisioning/ProvisionerClient.cs`, `Provisioning/ProvisionerDtos.cs`
- Modify: `MayFly.Api/Program.cs` (register typed HttpClient)
- Test: `MayFly.Tests/Api/ProvisionerClientTests.cs`

**Interfaces:**
- Produces: `IProvisionerClient { Task<ProvisionResult> CreateAsync(string engine,int ttl,int storageMb,string initData,CancellationToken); Task DestroyAsync(string containerId,string volume,int port,CancellationToken); Task<ProvisionInspect> InspectAsync(string containerId,CancellationToken); }`; `record ProvisionResult(string ContainerId,string VolumeName,string InternalHost,int PublicPort,string DbName,string DbUser,string DbPassword)`; `record ProvisionInspect(string State,long SizeBytes)`.

- [ ] **Step 1: Write the failing test** (uses a stub handler, no real Provisioner)

`MayFly.Tests/Api/ProvisionerClientTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Api.Provisioning;
using Xunit;

public class ProvisionerClientTests
{
    private sealed class StubHandler(HttpResponseMessage resp) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
            => Task.FromResult(resp);
    }

    [Fact]
    public async Task CreateAsync_maps_result()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProvisionResult("cid","vol","host",20001,"appdb","appuser","pw"))
        };
        var http = new HttpClient(new StubHandler(resp)) { BaseAddress = new Uri("http://provisioner") };
        var sut = new ProvisionerClient(http);
        var r = await sut.CreateAsync("postgres", 3, 256, "blank", default);
        r.PublicPort.Should().Be(20001);
        r.DbName.Should().Be("appdb");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter ProvisionerClientTests`
Expected: FAIL (types not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Provisioning/ProvisionerDtos.cs`:
```csharp
namespace MayFly.Api.Provisioning;

public record ProvisionResult(string ContainerId, string VolumeName, string InternalHost,
                              int PublicPort, string DbName, string DbUser, string DbPassword);
public record ProvisionInspect(string State, long SizeBytes);
internal record CreateBody(string Engine, int TtlHours, int StorageMb, string InitialData);
```

`MayFly.Api/Provisioning/IProvisionerClient.cs`:
```csharp
namespace MayFly.Api.Provisioning;

public interface IProvisionerClient
{
    Task<ProvisionResult> CreateAsync(string engine, int ttl, int storageMb, string initData, CancellationToken ct);
    Task DestroyAsync(string containerId, string volume, int port, CancellationToken ct);
    Task<ProvisionInspect> InspectAsync(string containerId, CancellationToken ct);
}
```

`MayFly.Api/Provisioning/ProvisionerClient.cs`:
```csharp
using System.Net.Http.Json;

namespace MayFly.Api.Provisioning;

public sealed class ProvisionerClient(HttpClient http) : IProvisionerClient
{
    public async Task<ProvisionResult> CreateAsync(string engine, int ttl, int storageMb, string initData, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync("/instances",
            new CreateBody(engine, ttl, storageMb, initData), ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ProvisionResult>(cancellationToken: ct))!;
    }

    public async Task DestroyAsync(string containerId, string volume, int port, CancellationToken ct)
    {
        var resp = await http.DeleteAsync($"/instances/{containerId}?volume={volume}&port={port}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ProvisionInspect> InspectAsync(string containerId, CancellationToken ct)
        => (await http.GetFromJsonAsync<ProvisionInspect>($"/instances/{containerId}", ct))!;
}
```

Register in `MayFly.Api/Program.cs`:
```csharp
builder.Services.AddHttpClient<IProvisionerClient, ProvisionerClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Provisioner:BaseUrl"] ?? "http://provisioner:8080"));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter ProvisionerClientTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): typed Provisioner HTTP client"
```

---

### Task 10: Secret protector (Data Protection wrapper)

**Files:**
- Create: `MayFly.Api/Security/SecretProtector.cs`
- Modify: `MayFly.Api/Program.cs` (AddDataProtection)
- Test: `MayFly.Tests/Api/SecretProtectorTests.cs`

**Interfaces:**
- Produces: `ISecretProtector { string Protect(string plaintext); string Unprotect(string ciphertext); }`, impl `SecretProtector(IDataProtectionProvider)`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Api/SecretProtectorTests.cs`:
```csharp
using FluentAssertions;
using MayFly.Api.Security;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

public class SecretProtectorTests
{
    [Fact]
    public void Roundtrip_returns_original_and_ciphertext_differs()
    {
        var sut = new SecretProtector(DataProtectionProvider.Create("MayFlyTest"));
        var enc = sut.Protect("s3cr3t");
        enc.Should().NotBe("s3cr3t");
        sut.Unprotect(enc).Should().Be("s3cr3t");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter SecretProtectorTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Security/SecretProtector.cs`:
```csharp
using Microsoft.AspNetCore.DataProtection;

namespace MayFly.Api.Security;

public interface ISecretProtector { string Protect(string plaintext); string Unprotect(string ciphertext); }

public sealed class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _p;
    public SecretProtector(IDataProtectionProvider provider) => _p = provider.CreateProtector("MayFly.DbSecret");
    public string Protect(string plaintext) => _p.Protect(plaintext);
    public string Unprotect(string ciphertext) => _p.Unprotect(ciphertext);
}
```

Register in `MayFly.Api/Program.cs`:
```csharp
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();
builder.Services.AddSingleton<ITokenService, TokenService>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter SecretProtectorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): Data Protection secret wrapper for DB passwords"
```

---

### Task 11: InstanceService — create with IP quota, get, list, destroy

**Files:**
- Create: `MayFly.Api/Services/IInstanceService.cs`, `Services/InstanceService.cs`
- Test: `MayFly.Tests/Api/InstanceServiceTests.cs` (extend file from Task 7)

**Interfaces:**
- Consumes: `MayFlyContext`, `IProvisionerClient`, `ITokenService`, `ISecretProtector`.
- Produces: `IInstanceService` with:
  - `Task<CreateOutcome> CreateAsync(string engine,int ttl,int storageMb,string initData,string ip,string sessionId,CancellationToken)` — enforces ≤3 active per IP in a transaction; returns `CreateOutcome(bool QuotaExceeded, Instance? Instance)`.
  - `Task<Instance?> GetByTokenAsync(string token, CancellationToken)`
  - `Task<IReadOnlyList<Instance>> ListBySessionAsync(string sessionId, CancellationToken)`
  - `Task<bool> DestroyAsync(string token, CancellationToken)` — marks Destroyed + calls Provisioner.
  - Active states (count toward quota): `Provisioning`, `Running`.

- [ ] **Step 1: Write the failing test** (append to `InstanceServiceTests.cs`)

```csharp
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Api.Services;
using Moq;

[Trait("Category", "Docker")]
public class InstanceServiceQuotaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private (InstanceService sut, MayFlyContext ctx) NewSut()
    {
        var ctx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options);
        ctx.Database.EnsureCreated();
        var prov = new Mock<IProvisionerClient>();
        prov.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionResult("cid", "vol", "host", 20002, "appdb", "appuser", "pw"));
        var sut = new InstanceService(ctx, prov.Object, new TokenService(),
            new SecretProtector(DataProtectionProvider.Create("t")));
        return (sut, ctx);
    }

    [Fact]
    public async Task Fourth_instance_for_same_ip_is_rejected()
    {
        var (sut, _) = NewSut();
        for (int i = 0; i < 3; i++)
            (await sut.CreateAsync("postgres", 3, 256, "blank", "9.9.9.9", "s", default))
                .QuotaExceeded.Should().BeFalse();
        var fourth = await sut.CreateAsync("postgres", 3, 256, "blank", "9.9.9.9", "s", default);
        fourth.QuotaExceeded.Should().BeTrue();
    }

    [Fact]
    public async Task GetByToken_returns_created_instance()
    {
        var (sut, _) = NewSut();
        var created = (await sut.CreateAsync("postgres", 6, 512, "blank", "8.8.8.8", "s", default)).Instance!;
        (await sut.GetByTokenAsync(created.CapabilityToken, default))!.PublicPort.Should().Be(20002);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter InstanceServiceQuotaTests`
Expected: FAIL (service not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Services/IInstanceService.cs`:
```csharp
using MayFly.Api.Domain;
namespace MayFly.Api.Services;

public record CreateOutcome(bool QuotaExceeded, Instance? Instance);

public interface IInstanceService
{
    Task<CreateOutcome> CreateAsync(string engine, int ttl, int storageMb, string initData,
        string ip, string sessionId, CancellationToken ct);
    Task<Instance?> GetByTokenAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<Instance>> ListBySessionAsync(string sessionId, CancellationToken ct);
    Task<bool> DestroyAsync(string token, CancellationToken ct);
}
```

`MayFly.Api/Services/InstanceService.cs`:
```csharp
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Services;

public sealed class InstanceService(
    MayFlyContext db, IProvisionerClient provisioner, ITokenService tokens, ISecretProtector secrets)
    : IInstanceService
{
    private const int MaxPerIp = 3;
    private static readonly InstanceState[] Active = { InstanceState.Provisioning, InstanceState.Running };

    public async Task<CreateOutcome> CreateAsync(string engine, int ttl, int storageMb, string initData,
        string ip, string sessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var active = await db.Instances.CountAsync(i => i.CreatorIp == ip && Active.Contains(i.State), ct);
        if (active >= MaxPerIp) return new CreateOutcome(true, null);

        var prov = await provisioner.CreateAsync(engine, ttl, storageMb, initData, ct);
        var now = DateTime.UtcNow;
        var inst = new Instance
        {
            CapabilityToken = tokens.NewToken(), SessionId = sessionId, CreatorIp = ip,
            Engine = engine, TtlHours = ttl, StorageQuotaMb = storageMb, InitialData = initData,
            ContainerId = prov.ContainerId, VolumeName = prov.VolumeName, InternalHost = prov.InternalHost,
            PublicPort = prov.PublicPort, DbName = prov.DbName, DbUser = prov.DbUser,
            DbPasswordEnc = secrets.Protect(prov.DbPassword),
            State = InstanceState.Running, CreatedAt = now, ExpiresAt = now.AddHours(ttl)
        };
        db.Instances.Add(inst);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CreateOutcome(false, inst);
    }

    public Task<Instance?> GetByTokenAsync(string token, CancellationToken ct)
        => db.Instances.SingleOrDefaultAsync(i => i.CapabilityToken == token, ct);

    public async Task<IReadOnlyList<Instance>> ListBySessionAsync(string sessionId, CancellationToken ct)
        => await db.Instances.Where(i => i.SessionId == sessionId)
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);

    public async Task<bool> DestroyAsync(string token, CancellationToken ct)
    {
        var inst = await db.Instances.SingleOrDefaultAsync(i => i.CapabilityToken == token, ct);
        if (inst is null || inst.State is InstanceState.Destroyed) return false;
        await provisioner.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct);
        inst.State = InstanceState.Destroyed;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

Register in `Program.cs`: `builder.Services.AddScoped<IInstanceService, InstanceService>();`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter InstanceServiceQuotaTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): InstanceService with transactional 3/IP quota + lifecycle calls"
```

---

### Task 12: Query executor (internal network, timeout, row cap)

**Files:**
- Create: `MayFly.Api/Services/IQueryExecutor.cs`, `Services/QueryExecutor.cs`, `Dtos/QueryResultDto.cs`
- Test: `MayFly.Tests/Api/QueryExecutorTests.cs`

**Interfaces:**
- Consumes: `Instance`, `ISecretProtector`.
- Produces: `IQueryExecutor { Task<QueryResultDto> ExecuteAsync(Instance inst, string sql, CancellationToken ct); }`; `record QueryResultDto(bool Success, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, int RowCount, int DurationMs, string Message, string? Error)`. Caps rows at 500, command timeout 10s. In dev connects via `localhost:PublicPort`; in prod via `InternalHost:5432` (selected by config flag `QueryExecutor:UseInternalHost`).

- [ ] **Step 1: Write the failing integration test**

`MayFly.Tests/Api/QueryExecutorTests.cs`:
```csharp
using Docker.DotNet;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Security;
using MayFly.Api.Services;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

[Trait("Category", "Docker")]
public class QueryExecutorTests
{
    [Fact]
    public async Task Executes_select_against_real_postgres()
    {
        var docker = new DockerClientConfiguration().CreateClient();
        var prov = new DockerProvisioner(docker, new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker));
        var r = await prov.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
        var inst = new Instance
        {
            InternalHost = r.InternalHost, PublicPort = r.PublicPort, DbName = r.DbName,
            DbUser = r.DbUser, DbPasswordEnc = secrets.Protect(r.DbPassword)
        };
        try
        {
            // dev mode: connect via localhost public port
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["QueryExecutor:UseInternalHost"] = "false" }).Build();
            var sut = new QueryExecutor(secrets, cfg);
            await WaitReady(sut, inst);

            var res = await sut.ExecuteAsync(inst, "SELECT 1 AS n", default);
            res.Success.Should().BeTrue();
            res.Columns.Should().ContainSingle().Which.Should().Be("n");
            res.Rows.Should().ContainSingle();
            res.Rows[0][0].Should().Be(1);

            var bad = await sut.ExecuteAsync(inst, "SELECT * FROM nope", default);
            bad.Success.Should().BeFalse();
            bad.Error.Should().NotBeNullOrEmpty();
        }
        finally { await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default); }
    }

    private static async Task WaitReady(QueryExecutor sut, Instance inst)
    {
        for (int i = 0; i < 30; i++)
        {
            var res = await sut.ExecuteAsync(inst, "SELECT 1", default);
            if (res.Success) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException("not ready");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter QueryExecutorTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Dtos/QueryResultDto.cs`:
```csharp
namespace MayFly.Api.Dtos;

public record QueryResultDto(bool Success, IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows, int RowCount, int DurationMs, string Message, string? Error);
```

`MayFly.Api/Services/IQueryExecutor.cs`:
```csharp
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
namespace MayFly.Api.Services;
public interface IQueryExecutor { Task<QueryResultDto> ExecuteAsync(Instance inst, string sql, CancellationToken ct); }
```

`MayFly.Api/Services/QueryExecutor.cs`:
```csharp
using System.Diagnostics;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Security;
using Npgsql;

namespace MayFly.Api.Services;

public sealed class QueryExecutor(ISecretProtector secrets, IConfiguration cfg) : IQueryExecutor
{
    private const int RowCap = 500;
    private const int TimeoutSeconds = 10;

    public async Task<QueryResultDto> ExecuteAsync(Instance inst, string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var useInternal = cfg.GetValue("QueryExecutor:UseInternalHost", true);
        var host = useInternal ? inst.InternalHost : "localhost";
        var port = useInternal ? 5432 : inst.PublicPort;
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = inst.DbName, Username = inst.DbUser,
            Password = secrets.Unprotect(inst.DbPasswordEnc),
            Timeout = 5, CommandTimeout = TimeoutSeconds
        }.ToString();

        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = TimeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!reader.HasRows && reader.FieldCount == 0)
            {
                var affected = reader.RecordsAffected;
                return new QueryResultDto(true, Array.Empty<string>(), Array.Empty<object?[]>(),
                    0, (int)sw.ElapsedMilliseconds, $"{Math.Max(affected, 0)} row(s) affected", null);
            }

            var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct) && rows.Count < RowCap)
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return new QueryResultDto(true, cols, rows, rows.Count, (int)sw.ElapsedMilliseconds,
                $"{rows.Count} row(s)", null);
        }
        catch (Exception ex)
        {
            return new QueryResultDto(false, Array.Empty<string>(), Array.Empty<object?[]>(),
                0, (int)sw.ElapsedMilliseconds, "error", ex.Message);
        }
    }
}
```

Register in `Program.cs`: `builder.Services.AddScoped<IQueryExecutor, QueryExecutor>();`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter QueryExecutorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): query executor (timeout, row cap, error capture)"
```

---

## Phase 3 — Lifecycle & HTTP surface

### Task 13: LifecycleService (reaper + size monitor)

**Files:**
- Create: `MayFly.Api/Lifecycle/LifecycleService.cs`
- Modify: `MayFly.Api/Program.cs` (AddHostedService)
- Test: `MayFly.Tests/Api/LifecycleServiceTests.cs`

**Interfaces:**
- Consumes: `IServiceScopeFactory` → `MayFlyContext`, `IProvisionerClient`.
- Produces: `LifecycleService.RunOnceAsync(CancellationToken)` (internal, called by `ExecuteAsync` loop and tests). Reaper: instances with `ExpiresAt <= now` and state ∈ {Provisioning, Running} → Destroy + state Destroyed. Monitor: state Running → `Inspect` → set `LastSizeBytes`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Api/LifecycleServiceTests.cs`:
```csharp
using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Lifecycle;
using MayFly.Api.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class LifecycleServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Reaper_destroys_expired_and_marks_state()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MayFlyContext>(o => o.UseNpgsql(_db.GetConnectionString()));
        var prov = new Mock<IProvisionerClient>();
        prov.Setup(p => p.InspectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionInspect("running", 123));
        services.AddSingleton(prov.Object);
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
            ctx.Database.EnsureCreated();
            ctx.Instances.Add(new Instance
            {
                CapabilityToken = "x", ContainerId = "c", VolumeName = "v", PublicPort = 20003,
                State = InstanceState.Running, CreatedAt = DateTime.UtcNow.AddHours(-4),
                ExpiresAt = DateTime.UtcNow.AddHours(-1)   // already expired
            });
            await ctx.SaveChangesAsync();
        }

        var sut = new LifecycleService(sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<LifecycleService>>());
        await sut.RunOnceAsync(default);

        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MayFlyContext>();
        (await db.Instances.SingleAsync()).State.Should().Be(InstanceState.Destroyed);
        prov.Verify(p => p.DestroyAsync("c", "v", 20003, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter LifecycleServiceTests`
Expected: FAIL (type not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Lifecycle/LifecycleService.cs`:
```csharp
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Lifecycle;

public sealed class LifecycleService(IServiceScopeFactory scopes, ILogger<LifecycleService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { log.LogError(ex, "lifecycle tick failed"); }
            await Task.Delay(Interval, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
        var prov = scope.ServiceProvider.GetRequiredService<IProvisionerClient>();
        var now = DateTime.UtcNow;

        var expired = await db.Instances
            .Where(i => i.ExpiresAt <= now &&
                        (i.State == InstanceState.Running || i.State == InstanceState.Provisioning))
            .ToListAsync(ct);
        foreach (var inst in expired)
        {
            try { await prov.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct); }
            catch (Exception ex) { log.LogWarning(ex, "destroy {Id} failed", inst.Id); }
            inst.State = InstanceState.Destroyed;
        }

        var running = await db.Instances.Where(i => i.State == InstanceState.Running).ToListAsync(ct);
        foreach (var inst in running)
        {
            try { inst.LastSizeBytes = (await prov.InspectAsync(inst.ContainerId, ct)).SizeBytes; }
            catch (Exception ex) { log.LogDebug(ex, "inspect {Id} failed", inst.Id); }
        }

        await db.SaveChangesAsync(ct);
    }
}
```

Register in `Program.cs`: `builder.Services.AddHostedService<LifecycleService>();`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter LifecycleServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): lifecycle background service (reaper + size monitor)"
```

---

### Task 14: Session cookie middleware + DTOs + InstancesController

**Files:**
- Create: `MayFly.Api/Security/SessionCookieMiddleware.cs`, `Dtos/CreateInstanceDto.cs`, `Dtos/InstanceDto.cs`, `Controllers/InstancesController.cs`
- Modify: `MayFly.Api/Program.cs` (middleware, ForwardedHeaders, controllers, rate limiter)
- Test: `MayFly.Tests/Api/InstancesApiTests.cs`

**Interfaces:**
- Consumes: `IInstanceService`, `IQueryExecutor`, `ISecretProtector`.
- HTTP contract (consumed by frontend Plan 2):
  - `POST /api/instances` body `CreateInstanceDto{engine,ttlHours,storageMb,initialData}` → 201 `InstanceDto` | 400 | 429
  - `GET /api/instances/{token}` → 200 `InstanceDto` | 404
  - `GET /api/instances` (session cookie) → 200 `InstanceDto[]`
  - `DELETE /api/instances/{token}` → 204 | 404
  - `POST /api/instances/{token}/query` body `{sql}` → 200 `QueryResultDto` | 404
- `InstanceDto(string token,string engine,string state,int ttlHours,int storageQuotaMb,long lastSizeBytes,string initialData,DateTime createdAt,DateTime expiresAt,string connectionString,int publicPort,string dbName,string dbUser)`.
- Session cookie name: `mayfly_sid`, HttpOnly, SameSite=Lax, 30-day.

- [ ] **Step 1: Write the failing test** (validation + quota path, Provisioner stubbed via test host override)

`MayFly.Tests/Api/InstancesApiTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MayFly.Api.Provisioning;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

public class InstancesApiTests : IClassFixture<WebApplicationFactory<MayFly.Api.IApiMarker>>
{
    private readonly WebApplicationFactory<MayFly.Api.IApiMarker> _factory;
    public InstancesApiTests(WebApplicationFactory<MayFly.Api.IApiMarker> f) => _factory = f;

    [Fact]
    public async Task Create_with_bad_engine_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/instances",
            new { engine = "oracle", ttlHours = 3, storageMb = 256, initialData = "blank" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```
> Note: full create/query happy-path is covered by `InstanceServiceQuotaTests` and `QueryExecutorTests` (real Docker). This controller test asserts validation wiring only and uses the in-memory/Testcontainers metadata DB configured in `Program` test environment.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter InstancesApiTests`
Expected: FAIL (marker/controller not found).

- [ ] **Step 3: Write implementation**

`MayFly.Api/Dtos/CreateInstanceDto.cs`:
```csharp
namespace MayFly.Api.Dtos;
public record CreateInstanceDto(string Engine, int TtlHours, int StorageMb, string InitialData);
public record QueryRequestDto(string Sql);
```

`MayFly.Api/Dtos/InstanceDto.cs`:
```csharp
using MayFly.Api.Domain;
namespace MayFly.Api.Dtos;

public record InstanceDto(string Token, string Engine, string State, int TtlHours, int StorageQuotaMb,
    long LastSizeBytes, string InitialData, DateTime CreatedAt, DateTime ExpiresAt,
    string ConnectionString, int PublicPort, string DbName, string DbUser)
{
    public static InstanceDto From(Instance i, string publicHost, string plainPassword) => new(
        i.CapabilityToken, i.Engine, i.State.ToString(), i.TtlHours, i.StorageQuotaMb, i.LastSizeBytes,
        i.InitialData, i.CreatedAt, i.ExpiresAt,
        $"postgresql://{i.DbUser}:{plainPassword}@{publicHost}:{i.PublicPort}/{i.DbName}",
        i.PublicPort, i.DbName, i.DbUser);
}
```

`MayFly.Api/Security/SessionCookieMiddleware.cs`:
```csharp
namespace MayFly.Api.Security;

public sealed class SessionCookieMiddleware(RequestDelegate next)
{
    public const string CookieName = "mayfly_sid";

    public async Task Invoke(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var sid) || string.IsNullOrWhiteSpace(sid))
        {
            sid = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append(CookieName, sid, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = true,
                Expires = DateTimeOffset.UtcNow.AddDays(30), IsEssential = true
            });
        }
        ctx.Items[CookieName] = sid;
        await next(ctx);
    }
}
```

`MayFly.Api/Controllers/InstancesController.cs`:
```csharp
using MayFly.Api.Dtos;
using MayFly.Api.Security;
using MayFly.Api.Services;
using MayFly.Api.Validation;
using Microsoft.AspNetCore.Mvc;

namespace MayFly.Api.Controllers;

[ApiController]
[Route("api/instances")]
public sealed class InstancesController(
    IInstanceService instances, IQueryExecutor queryExec, ISecretProtector secrets, IConfiguration cfg)
    : ControllerBase
{
    private string PublicHost => cfg["PublicHost"] ?? "localhost";
    private string Sid => HttpContext.Items[SessionCookieMiddleware.CookieName] as string ?? "";
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInstanceDto dto, CancellationToken ct)
    {
        var (ok, error) = ApiSpecValidator.Validate(dto);
        if (!ok) return BadRequest(new { error });
        var outcome = await instances.CreateAsync(dto.Engine, dto.TtlHours, dto.StorageMb,
            dto.InitialData, Ip, Sid, ct);
        if (outcome.QuotaExceeded) return StatusCode(429, new { error = "IP quota of 3 active databases reached" });
        var inst = outcome.Instance!;
        return CreatedAtAction(nameof(GetByToken), new { token = inst.CapabilityToken },
            InstanceDto.From(inst, PublicHost, secrets.Unprotect(inst.DbPasswordEnc)));
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        return inst is null ? NotFound()
            : Ok(InstanceDto.From(inst, PublicHost, secrets.Unprotect(inst.DbPasswordEnc)));
    }

    [HttpGet]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        var list = await instances.ListBySessionAsync(Sid, ct);
        return Ok(list.Select(i => InstanceDto.From(i, PublicHost, secrets.Unprotect(i.DbPasswordEnc))));
    }

    [HttpDelete("{token}")]
    public async Task<IActionResult> Destroy(string token, CancellationToken ct)
        => await instances.DestroyAsync(token, ct) ? NoContent() : NotFound();

    [HttpPost("{token}/query")]
    public async Task<IActionResult> Query(string token, [FromBody] QueryRequestDto body, CancellationToken ct)
    {
        var inst = await instances.GetByTokenAsync(token, ct);
        if (inst is null) return NotFound();
        return Ok(await queryExec.ExecuteAsync(inst, body.Sql, ct));
    }
}
```

`MayFly.Api/Validation/ApiSpecValidator.cs`:
```csharp
using MayFly.Api.Dtos;
namespace MayFly.Api.Validation;

public static class ApiSpecValidator
{
    private static readonly HashSet<string> Engines = new() { "postgres" };
    private static readonly HashSet<int> Ttls = new() { 3, 6, 12 };
    private static readonly HashSet<int> Storage = new() { 256, 512, 1024, 2048 };
    private static readonly HashSet<string> Init = new() { "blank", "northwind" };

    public static (bool Ok, string? Error) Validate(CreateInstanceDto d)
    {
        if (!Engines.Contains(d.Engine)) return (false, "engine not supported");
        if (!Ttls.Contains(d.TtlHours)) return (false, "ttl not allowed");
        if (!Storage.Contains(d.StorageMb)) return (false, "storage not allowed");
        if (!Init.Contains(d.InitialData)) return (false, "initialData not allowed");
        return (true, null);
    }
}
```

`MayFly.Api/Program.cs` final wiring (add):
```csharp
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
// ...
builder.Services.AddControllers();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("perip", w =>
    { w.Window = TimeSpan.FromMinutes(1); w.PermitLimit = 60; }));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
// after build:
app.UseForwardedHeaders();
app.UseMiddleware<MayFly.Api.Security.SessionCookieMiddleware>();
app.UseRateLimiter();
app.MapControllers().RequireRateLimiting("perip");
```
And add `namespace MayFly.Api { public interface IApiMarker { } }` + `public partial class Program { }` for the test factory.

> Security note for prod (documented, configured in compose Task 15): `ForwardedHeadersOptions.KnownProxies`/`KnownNetworks` MUST be set to Caddy only so `X-Forwarded-For` cannot be spoofed.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter InstancesApiTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): instances controller, session cookie, validation, rate limiting"
```

---

### Task 15: Dashboard endpoint + docker-compose + Caddy

**Files:**
- Create: `MayFly.Api/Dtos/DashboardDto.cs`, `Controllers/DashboardController.cs`, `docker-compose.yml`, `Caddyfile`
- Test: `MayFly.Tests/Api/DashboardTests.cs`

**Interfaces:**
- HTTP: `GET /api/dashboard` (session cookie) → `DashboardDto(int aliveCount,int maxAlive,int queriesToday,long storageUsedBytes,DateTime? nextExpiry,InstanceDto[] instances)`.

- [ ] **Step 1: Write the failing test**

`MayFly.Tests/Api/DashboardTests.cs`:
```csharp
using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Services;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class DashboardServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Summary_counts_alive_and_next_expiry()
    {
        var ctx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options);
        ctx.Database.EnsureCreated();
        ctx.Instances.Add(new Instance { SessionId = "s", State = InstanceState.Running,
            LastSizeBytes = 1000, ExpiresAt = DateTime.UtcNow.AddHours(2), CreatedAt = DateTime.UtcNow });
        ctx.Instances.Add(new Instance { SessionId = "s", State = InstanceState.Destroyed,
            ExpiresAt = DateTime.UtcNow.AddHours(1), CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var sut = new DashboardService(ctx);
        var d = await sut.SummaryAsync("s", default);
        d.AliveCount.Should().Be(1);
        d.StorageUsedBytes.Should().Be(1000);
        d.NextExpiry.Should().NotBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MayFly.Tests --filter DashboardServiceTests`
Expected: FAIL.

- [ ] **Step 3: Write implementation**

`MayFly.Api/Dtos/DashboardDto.cs`:
```csharp
namespace MayFly.Api.Dtos;
public record DashboardSummary(int AliveCount, int MaxAlive, int QueriesToday,
    long StorageUsedBytes, DateTime? NextExpiry);
```

`MayFly.Api/Services/DashboardService.cs`:
```csharp
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Services;

public sealed class DashboardService(MayFlyContext db)
{
    public async Task<DashboardSummary> SummaryAsync(string sessionId, CancellationToken ct)
    {
        var alive = db.Instances.Where(i => i.SessionId == sessionId &&
            (i.State == InstanceState.Running || i.State == InstanceState.Provisioning));
        var today = DateTime.UtcNow.Date;
        var aliveIds = await alive.Select(i => i.Id).ToListAsync(ct);
        var queries = await db.QueryLogs.CountAsync(q => aliveIds.Contains(q.InstanceId) &&
            q.ExecutedAt >= today, ct);
        return new DashboardSummary(
            await alive.CountAsync(ct), 3, queries,
            await alive.SumAsync(i => i.LastSizeBytes, ct),
            await alive.OrderBy(i => i.ExpiresAt).Select(i => (DateTime?)i.ExpiresAt).FirstOrDefaultAsync(ct));
    }
}
```

`MayFly.Api/Controllers/DashboardController.cs`:
```csharp
using MayFly.Api.Security;
using MayFly.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace MayFly.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sid = HttpContext.Items[SessionCookieMiddleware.CookieName] as string ?? "";
        return Ok(await dashboard.SummaryAsync(sid, ct));
    }
}
```

Register in `Program.cs`: `builder.Services.AddScoped<DashboardService>();`

`docker-compose.yml` (dev):
```yaml
services:
  metadata-db:
    image: postgres:16-alpine
    environment: { POSTGRES_USER: mayfly, POSTGRES_PASSWORD: mayfly, POSTGRES_DB: mayfly }
    ports: ["5433:5432"]
    volumes: ["metadata:/var/lib/postgresql/data"]
  provisioner:
    build: { context: ., dockerfile: MayFly.Provisioner/Dockerfile }
    volumes: ["/var/run/docker.sock:/var/run/docker.sock"]
    networks: ["mayfly-internal"]
  api:
    build: { context: ., dockerfile: MayFly.Api/Dockerfile }
    environment:
      ConnectionStrings__Metadata: "Host=metadata-db;Database=mayfly;Username=mayfly;Password=mayfly"
      Provisioner__BaseUrl: "http://provisioner:8080"
      PublicHost: "localhost"
    depends_on: ["metadata-db", "provisioner"]
    networks: ["mayfly-internal"]
  caddy:
    image: caddy:2-alpine
    ports: ["80:80", "443:443"]
    volumes: ["./Caddyfile:/etc/caddy/Caddyfile"]
    depends_on: ["api"]
    networks: ["mayfly-internal"]
volumes: { metadata: {} }
networks: { mayfly-internal: { driver: bridge } }
```

`Caddyfile` (dev):
```
:80 {
    handle /api/* { reverse_proxy api:8080 }
    handle { reverse_proxy web:80 }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MayFly.Tests --filter DashboardServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): dashboard summary endpoint + docker-compose + Caddy"
```

---

## Phase 4 — End-to-end smoke

### Task 16: Manual end-to-end verification

**Files:** none (verification task)

- [ ] **Step 1: Start metadata DB and apply migrations**

```bash
docker compose up -d metadata-db
dotnet ef database update --project MayFly.Api
```

- [ ] **Step 2: Run Provisioner and Api locally**

```bash
dotnet run --project MayFly.Provisioner &     # internal :8080
dotnet run --project MayFly.Api &              # :5000
```
Set `Provisioner__BaseUrl=http://localhost:8080`, `QueryExecutor__UseInternalHost=false` for local run.

- [ ] **Step 3: Create an instance via curl**

```bash
curl -i -c cookies.txt -X POST http://localhost:5000/api/instances \
  -H 'Content-Type: application/json' \
  -d '{"engine":"postgres","ttlHours":3,"storageMb":256,"initialData":"blank"}'
```
Expected: `201`, JSON with `token`, `connectionString` (`postgresql://appuser:...@localhost:200xx/appdb`).

- [ ] **Step 4: Connect with the returned connection string**

```bash
psql "postgresql://appuser:<pw>@localhost:<port>/appdb" -c "SELECT version();"
```
Expected: PostgreSQL 16 version string.

- [ ] **Step 5: Run a query through the API + list + dashboard, then commit verification notes**

```bash
TOKEN=<token>
curl -s -X POST http://localhost:5000/api/instances/$TOKEN/query \
  -H 'Content-Type: application/json' -d '{"sql":"SELECT 1 AS n"}'
curl -s -b cookies.txt http://localhost:5000/api/instances
curl -s -b cookies.txt http://localhost:5000/api/dashboard
```
Expected: query returns `{success:true, columns:["n"], rows:[[1]]}`; list shows the instance; dashboard shows `aliveCount:1`.

```bash
git commit --allow-empty -m "test: verified backend e2e (create, connect, query, list, dashboard)"
```

---

## Self-Review (completed)

**Spec coverage:** §4 decisions → Tasks 5 (Docker B-boundary), 8 (tokens), 11 (IP quota), 4/5 (storage hard-cap + plain dev), 9 (Provisioner client). §5 components → Provisioner (Tasks 2-6), Api (7-15). §6 flows → create (Task 11/14), query via internal network (Task 12). §7 model → Task 7. §8 lifecycle → Task 13. §9 quotas → Tasks 4/11. §11 security → Tasks 5 (hardening), 8 (tokens), 14 (XFF/rate limit/cookie). §12 tests → every task is TDD; integration via Testcontainers/real Docker.

**Placeholder scan:** No TODO/TBD; every code step has concrete code; commands have expected output.

**Type consistency:** `CreateInstanceResult`/`ProvisionResult` both carry `VolumeName`; `DestroyAsync(containerId, volume, port)` signature consistent across `IDockerProvisioner`, `IProvisionerClient`, `LifecycleService`, `InstanceService`. `InstanceDto.From` matches entity fields. `QueryResultDto` shape identical in executor and controller.

**Initial data:** `blank` + `northwind` are both seeded end-to-end in Task 6b (Northwind = compact subset, applied by the Provisioner after container readiness). The full template library (full Northwind, E-commerce, Blog, IoT timeseries, import dump) is sub-project 3.
