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
