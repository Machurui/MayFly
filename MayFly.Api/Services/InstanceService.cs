using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace MayFly.Api.Services;

public sealed class InstanceService(
    MayFlyContext db, IProvisionerClient provisioner, ITokenService tokens, ISecretProtector secrets)
    : IInstanceService
{
    private const int MaxPerIp = 3;

    public async Task<CreateOutcome> CreateAsync(string engine, int ttl, int storageMb, string initData,
        string ip, string sessionId, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            // Explicit OR avoids EF translation issues with string-converted enum in array.Contains()
            var active = await db.Instances.CountAsync(
                i => i.CreatorIp == ip &&
                     (i.State == InstanceState.Provisioning || i.State == InstanceState.Running), ct);

            if (active >= MaxPerIp) return new CreateOutcome(true, null);

            ProvisionResult? prov = null;
            try
            {
                prov = await provisioner.CreateAsync(engine, ttl, storageMb, initData, ct);
                var now = DateTime.UtcNow;
                var inst = new Instance
                {
                    CapabilityToken  = tokens.NewToken(),
                    SessionId        = sessionId,
                    CreatorIp        = ip,
                    Engine           = engine,
                    TtlHours         = ttl,
                    StorageQuotaMb   = storageMb,
                    InitialData      = initData,
                    ContainerId      = prov.ContainerId,
                    VolumeName       = prov.VolumeName,
                    InternalHost     = prov.InternalHost,
                    PublicPort       = prov.PublicPort,
                    DbName           = prov.DbName,
                    DbUser           = prov.DbUser,
                    DbPasswordEnc    = secrets.Protect(prov.DbPassword),
                    State            = InstanceState.Running,
                    CreatedAt        = now,
                    ExpiresAt        = now.AddHours(ttl),
                };
                db.Instances.Add(inst);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new CreateOutcome(false, inst);
            }
            catch (Exception ex)
            {
                // best-effort: destroy the container we just created so it is never orphaned
                if (prov is not null)
                {
                    try { await provisioner.DestroyAsync(prov.ContainerId, prov.VolumeName, prov.PublicPort, ct); }
                    catch { /* best-effort */ }
                }
                // retry once on a Postgres serialization failure (40001); otherwise rethrow
                if (attempt < maxAttempts && IsSerializationFailure(ex))
                {
                    // Detach any tracked entities so the retry starts with a clean change tracker.
                    db.ChangeTracker.Clear();
                    continue;
                }
                throw;
            }
        }
    }

    public Task<Instance?> GetByTokenAsync(string token, CancellationToken ct)
        => db.Instances.SingleOrDefaultAsync(i => i.CapabilityToken == token, ct);

    public async Task<IReadOnlyList<Instance>> ListBySessionAsync(string sessionId, CancellationToken ct)
        => await db.Instances
            .Where(i => i.SessionId == sessionId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

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

        try
        {
            await provisioner.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct);
        }
        catch
        {
            await db.Instances.Where(i => i.Id == inst.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, InstanceState.Failed), ct);
            throw;   // surface the failure; reconcile will clean the orphan container
        }
        await db.Instances.Where(i => i.Id == inst.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, InstanceState.Destroyed), ct);
        return true;
    }

    private static bool IsSerializationFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException pg && pg.SqlState == PostgresErrorCodes.SerializationFailure)
                return true;
        return false;
    }
}
