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

    public async Task<CreateOutcome> CreateAsync(string engine, int ttl, int storageMb, string initData,
        string ip, string sessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        // Explicit OR avoids EF translation issues with string-converted enum in array.Contains()
        var active = await db.Instances.CountAsync(
            i => i.CreatorIp == ip &&
                 (i.State == InstanceState.Provisioning || i.State == InstanceState.Running), ct);

        if (active >= MaxPerIp) return new CreateOutcome(true, null);

        var prov = await provisioner.CreateAsync(engine, ttl, storageMb, initData, ct);
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
        if (inst is null || inst.State is InstanceState.Destroyed) return false;
        await provisioner.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct);
        inst.State = InstanceState.Destroyed;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
