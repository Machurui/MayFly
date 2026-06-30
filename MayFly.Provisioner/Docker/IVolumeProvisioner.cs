namespace MayFly.Provisioner.Docker;
public interface IVolumeProvisioner
{
    Task<string> CreateAsync(string instanceId, int storageMb, CancellationToken ct);
    Task DestroyAsync(string volumeName, CancellationToken ct);
}
