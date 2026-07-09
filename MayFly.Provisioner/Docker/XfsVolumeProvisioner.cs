using Docker.DotNet;
using Docker.DotNet.Models;

namespace MayFly.Provisioner.Docker;

public sealed class XfsVolumeProvisioner(IDockerClient docker) : IVolumeProvisioner
{
    /// <summary>
    /// Creates a Docker volume with an XFS-backed size quota.
    /// </summary>
    /// <remarks>
    /// Real hard cap (project quota enforcement) only works when the Docker data-root
    /// is on an XFS filesystem mounted with the <c>pquota</c> option. This is a host
    /// prerequisite documented in SECURITY.md and must be configured before provisioning
    /// database instances. The local driver's <c>size</c> option alone sets a soft limit
    /// that Docker enforces, but true hard-cap enforcement depends on the XFS host mount.
    /// </remarks>
    public async Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct)
    {
        var name = $"mayfly-vol-{instanceId}";
        await docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Driver = "local",
            DriverOpts = new Dictionary<string, string>
            {
                ["size"] = $"{storageMb}m"
            },
            Labels = new Dictionary<string, string> { ["mayfly.instance"] = instanceId }
        }, ct);
        return name;
    }

    public Task DestroyAsync(string volumeName, CancellationToken ct)
        => docker.Volumes.RemoveAsync(volumeName, force: true, ct);
}
