using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using MayFly.Provisioner.Contracts;
using Microsoft.Extensions.Logging;

namespace MayFly.Provisioner.Docker;

public sealed class DockerProvisioner(
    IDockerClient docker, IPortAllocator ports, IVolumeProvisioner volumes,
    ILogger<DockerProvisioner> log) : IDockerProvisioner
{
    private const string Image = "postgres:16-alpine";
    private const string SidecarImage = "alpine/socat:1.8.0.0";

    /// <summary>Internal-only bridge network: user DB containers live here; no gateway → no egress.</summary>
    private const string UserNetwork = "mayfly-users";

    /// <summary>Normal bridge network: socat sidecar publishes the host port from here.</summary>
    private const string IngressNetwork = "mayfly-ingress";

    public async Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var name = $"mayfly-pg-{id}";
        const string adminUser = "mayflyadmin";
        const string appUser = "appuser";
        const string dbName = "appdb";
        var adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var port = ports.Allocate();
        string? volume = null;
        string? containerId = null;
        string? sidecarId = null;

        try
        {
            await EnsureImageAsync(ct);
            await EnsureSidecarImageAsync(ct);
            await EnsureNetworksAsync(ct);
            volume = await volumes.CreateAsync(id, req.StorageMb, ct);

            // Create DB container on the internal user network (no published ports).
            var create = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = Image,
                Name = name,
                Hostname = name,
                Labels = new Dictionary<string, string>
                {
                    ["mayfly.instance"] = id,
                    ["mayfly.role"] = "db"
                },
                Env = new List<string>
                {
                    $"POSTGRES_USER={adminUser}",
                    $"POSTGRES_PASSWORD={adminPassword}",
                    $"POSTGRES_DB={dbName}"
                },
                HostConfig = new HostConfig
                {
                    Mounts = new List<Mount>
                    {
                        new() { Type = "volume", Source = volume, Target = "/var/lib/postgresql/data" }
                    },
                    NetworkMode = UserNetwork,
                    Memory = 256L * 1024 * 1024,
                    NanoCPUs = 500_000_000L,           // 0.5 CPU
                    PidsLimit = 200L,
                    CapDrop = new List<string> { "ALL" },
                    CapAdd = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" },
                    SecurityOpt = new List<string> { "no-new-privileges" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                }
            }, ct);
            containerId = create.ID;

            // Upload init scripts into the created-but-not-yet-started container.
            await using var tarStream = BuildInitTar(appUser, appPassword, dbName, req.InitialData);
            await docker.Containers.ExtractArchiveToContainerAsync(
                containerId,
                new CopyToContainerParameters { Path = "/docker-entrypoint-initdb.d" },
                tarStream,
                ct);

            await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            // Create socat sidecar: listens on the public host port and forwards to the DB by name.
            // Create on mayfly-ingress first (for the host port binding), then connect to mayfly-users.
            var sidecarCreate = await docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = SidecarImage,
                Name = $"mayfly-sidecar-{id}",
                Labels = new Dictionary<string, string>
                {
                    ["mayfly.instance"] = id,
                    ["mayfly.role"] = "sidecar"
                },
                Cmd = new List<string> { "-d", "TCP-LISTEN:5432,fork,reuseaddr", $"TCP:{name}:5432" },
                ExposedPorts = new Dictionary<string, EmptyStruct> { ["5432/tcp"] = default },
                HostConfig = new HostConfig
                {
                    NetworkMode = IngressNetwork,
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["5432/tcp"] = new List<PortBinding> { new() { HostPort = port.ToString() } }
                    },
                    Memory = 64L * 1024 * 1024,
                    NanoCPUs = 250_000_000L,           // 0.25 CPU
                    PidsLimit = 50L,
                    CapDrop = new List<string> { "ALL" },
                    SecurityOpt = new List<string> { "no-new-privileges" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                }
            }, ct);
            sidecarId = sidecarCreate.ID;

            // Connect sidecar to mayfly-users so socat can reach the DB by container name.
            await docker.Networks.ConnectNetworkAsync(UserNetwork, new NetworkConnectParameters
            {
                Container = sidecarId
            }, ct);

            await docker.Containers.StartContainerAsync(sidecarId, new ContainerStartParameters(), ct);

            return new CreateInstanceResult(containerId, volume!, name, port, dbName,
                DbUser: appUser, DbPassword: appPassword,
                AdminUser: adminUser, AdminPassword: adminPassword);
        }
        catch
        {
            if (sidecarId is not null)
            {
                try { await docker.Containers.RemoveContainerAsync(sidecarId, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { log.LogWarning(ex, "cleanup: remove sidecar {Id} failed", sidecarId); }
            }
            if (containerId is not null)
            {
                try { await docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct); }
                catch (Exception ex) { log.LogWarning(ex, "cleanup: remove container {Id} failed", containerId); }
            }
            if (volume is not null)
            {
                try { await volumes.DestroyAsync(volume, ct); }
                catch (Exception ex) { log.LogWarning(ex, "cleanup: destroy volume {Volume} failed", volume); }
            }
            ports.Release(port);
            throw;
        }
    }

    public async Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken ct)
    {
        // Locate the sidecar before removing the DB container.
        string? sidecarId = null;
        try
        {
            var inspect = await docker.Containers.InspectContainerAsync(containerId, ct);
            if (inspect.Config?.Labels?.TryGetValue("mayfly.instance", out var instanceId) == true
                && !string.IsNullOrEmpty(instanceId))
            {
                var sidecars = await docker.Containers.ListContainersAsync(new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"mayfly.instance={instanceId}"] = true,
                            ["mayfly.role=sidecar"] = true
                        }
                    }
                }, ct);
                sidecarId = sidecars.FirstOrDefault()?.ID;
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "destroy: inspect {Id} failed while locating sidecar", containerId); }

        try { await docker.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true }, ct); }
        catch (Exception ex) { log.LogWarning(ex, "destroy: remove container {Id} failed", containerId); }

        if (sidecarId is not null)
        {
            try { await docker.Containers.RemoveContainerAsync(sidecarId,
                new ContainerRemoveParameters { Force = true }, ct); }
            catch (Exception ex) { log.LogWarning(ex, "destroy: remove sidecar {Id} failed", sidecarId); }
        }

        try { await volumes.DestroyAsync(volumeName, ct); }
        catch (Exception ex) { log.LogWarning(ex, "destroy: destroy volume {Volume} failed", volumeName); }
        ports.Release(publicPort);
    }

    public async Task<InspectResult> InspectAsync(string containerId, CancellationToken ct)
    {
        var c = await docker.Containers.InspectContainerAsync(containerId,
            new ContainerInspectParameters { IncludeSize = true }, ct);
        long size = c.SizeRootFs ?? c.SizeRw ?? 0;
        var state = c.State?.Running == true ? "running" : (c.State?.Status ?? "unknown");
        return new InspectResult(state, size);
    }

    public async Task<IReadOnlyList<ManagedContainerInfo>> ListManagedAsync(CancellationToken ct)
    {
        var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = false,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["mayfly.role=db"] = true }
            }
        }, ct);

        return containers
            .Where(c => c.Labels.ContainsKey("mayfly.instance"))
            .Select(c => new ManagedContainerInfo(c.ID, c.Labels["mayfly.instance"]))
            .ToList();
    }

    public async Task DestroyByInstanceAsync(string instanceId, CancellationToken ct)
    {
        // Queries by mayfly.instance label — catches both the DB and the sidecar container.
        var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"mayfly.instance={instanceId}"] = true }
            }
        }, ct);

        foreach (var c in containers)
        {
            string? volumeName = c.Mounts?.FirstOrDefault(m => m.Type == "volume")?.Name;
            // The sidecar holds the host port binding; the DB no longer publishes any port.
            int port = 0;
            var portEntry = c.Ports?.FirstOrDefault(p => p.PrivatePort == 5432 && p.PublicPort > 0);
            if (portEntry?.PublicPort is ushort pp) port = pp;

            try
            {
                await docker.Containers.RemoveContainerAsync(c.ID,
                    new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "DestroyByInstance: remove {Id} failed", c.ID); }

            if (!string.IsNullOrEmpty(volumeName))
            {
                try { await volumes.DestroyAsync(volumeName, ct); }
                catch (Exception ex) { log.LogWarning(ex, "DestroyByInstance: remove volume {Vol} failed", volumeName); }
            }

            if (port > 0) ports.Release(port);
        }
    }

    /// <summary>
    /// Builds an in-memory tar stream containing the Postgres init scripts to be uploaded
    /// into /docker-entrypoint-initdb.d before the container is started.
    /// </summary>
    private static MemoryStream BuildInitTar(
        string appUser, string appPassword, string dbName, string initialData)
    {
        var ms = new MemoryStream();
        using (var tw = new TarWriter(ms, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var rolesSql = BuildRolesSql(appUser, appPassword, dbName);
            WriteTarEntry(tw, "00-roles.sql", rolesSql);

            if (initialData == "northwind")
            {
                var northwindSql = ReadEmbeddedNorthwind();
                var seedSql = northwindSql +
                    "\nGRANT ALL ON ALL TABLES IN SCHEMA public TO appuser;" +
                    "\nGRANT ALL ON ALL SEQUENCES IN SCHEMA public TO appuser;\n";
                WriteTarEntry(tw, "01-seed.sql", seedSql);
            }
        } // TarWriter.Dispose() writes the end-of-archive marker

        ms.Position = 0;
        return ms;
    }

    private static void WriteTarEntry(TarWriter tw, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                   UnixFileMode.GroupRead | UnixFileMode.OtherRead
        };
        tw.WriteEntry(entry);
    }

    private static string BuildRolesSql(string appUser, string appPassword, string dbName)
    {
        var pwdLiteral = appPassword.Replace("'", "''");
        return
            $"CREATE ROLE \"{appUser}\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '{pwdLiteral}';\n" +
            $"GRANT CONNECT ON DATABASE \"{dbName}\" TO \"{appUser}\";\n" +
            $"GRANT ALL ON SCHEMA public TO \"{appUser}\";\n" +
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO \"{appUser}\";\n" +
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO \"{appUser}\";\n" +
            "CREATE EXTENSION IF NOT EXISTS pg_trgm;\n" +
            "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";\n";
    }

    private static string ReadEmbeddedNorthwind()
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().Single(n => n.EndsWith("northwind.sql"));
        using var stream = asm.GetManifestResourceStream(res)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task EnsureImageAsync(CancellationToken ct)
    {
        var existing = await docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        if (existing.Any(i => i.RepoTags?.Contains(Image) == true)) return;
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "postgres", Tag = "16-alpine" },
            null, new Progress<JSONMessage>(), ct);
    }

    private async Task EnsureSidecarImageAsync(CancellationToken ct)
    {
        var existing = await docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);
        if (existing.Any(i => i.RepoTags?.Contains(SidecarImage) == true)) return;
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = "alpine/socat", Tag = "1.8.0.0" },
            null, new Progress<JSONMessage>(), ct);
    }

    private async Task EnsureNetworksAsync(CancellationToken ct)
    {
        var nets = await docker.Networks.ListNetworksAsync(cancellationToken: ct);

        if (!nets.Any(n => n.Name == UserNetwork))
        {
            try
            {
                await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
                {
                    Name = UserNetwork,
                    Driver = "bridge",
                    Internal = true  // No gateway → no internet egress
                }, ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }

        if (!nets.Any(n => n.Name == IngressNetwork))
        {
            try
            {
                await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
                {
                    Name = IngressNetwork,
                    Driver = "bridge",
                    Internal = false
                }, ct);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }
    }
}
