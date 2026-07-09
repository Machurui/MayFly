namespace MayFly.Api.Provisioning;

public interface IProvisionerClient
{
    Task<ProvisionResult> CreateAsync(string engine, int ttl, int storageMb, string initData, CancellationToken ct);
    Task DestroyAsync(string containerId, string volume, int port, CancellationToken ct);
    Task<ProvisionInspect> InspectAsync(string containerId, CancellationToken ct);
    Task<IReadOnlyList<ManagedContainer>> ListManagedAsync(CancellationToken ct);
    Task DestroyByInstanceAsync(string instanceId, CancellationToken ct);
    Task SweepOrphansAsync(IReadOnlyCollection<string> activeVolumeNames, CancellationToken ct);
}
