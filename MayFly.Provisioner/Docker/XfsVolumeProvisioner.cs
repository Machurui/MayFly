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
