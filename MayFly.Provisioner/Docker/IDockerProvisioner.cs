using MayFly.Provisioner.Contracts;
namespace MayFly.Provisioner.Docker;

public interface IDockerProvisioner
{
    Task<CreateInstanceResult> CreateAsync(CreateInstanceRequest req, CancellationToken ct);
    Task DestroyAsync(string containerId, string volumeName, int publicPort, CancellationToken ct);
    Task<InspectResult> InspectAsync(string containerId, CancellationToken ct);
    Task<IReadOnlyList<ManagedContainerInfo>> ListManagedAsync(CancellationToken ct);
    Task DestroyByInstanceAsync(string instanceId, CancellationToken ct);
    Task SweepOrphansAsync(IReadOnlyCollection<string> activeVolumeNames, CancellationToken ct);
    Task<ExecMongoshResult> ExecMongoshAsync(string containerId, ExecMongoshRequest req, CancellationToken ct);
    Task<ExecDumpResult> ExecDumpAsync(string containerId, ExecDumpRequest req, CancellationToken ct);
}
