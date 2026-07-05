using System.Security.Cryptography;
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
        string? containerId = null;

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
                Labels = new Dictionary<string, string>
                {
                    ["mayfly.instance"] = id,
                    ["mayfly.role"] = "db"
                },
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
                    NanoCPUs = 500_000_000L,           // 0.5 CPU
                    PidsLimit = 200L,
                    CapDrop = new List<string> { "ALL" },
                    // Add back only the minimum capabilities postgres needs to initialise
                    // (drop ALL + add minimum is more secure than leaving defaults)
                    CapAdd = new List<string> { "CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE" },
                    SecurityOpt = new List<string> { "no-new-privileges" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
                }
            }, ct);
            containerId = create.ID;

            await docker.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);

            return new CreateInstanceResult(containerId, volume!, name, port, dbName, dbUser, password);
        }
        catch
        {
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
        try { await docker.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true }, ct); }
        catch (Exception ex) { log.LogWarning(ex, "destroy: remove container {Id} failed", containerId); }
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
            int port = 0;
            var portEntry = c.Ports?.FirstOrDefault(p => p.PrivatePort == 5432);
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
        try
        {
            await docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = Network,
                Driver = "bridge",
                Internal = false,           // false: containers need outbound port publish; isolation via icc
                Options = new Dictionary<string, string> { ["com.docker.network.bridge.enable_icc"] = "false" }
            }, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Another concurrent provision already created the network — treat as success.
        }
    }
}
