using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Provisioning;
using MayFly.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Lifecycle;

public sealed class LifecycleService(IServiceScopeFactory scopes, ILogger<LifecycleService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await RunReconcileAsync(ct); }
        catch (Exception ex) { log.LogError(ex, "startup reconcile failed"); }

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { log.LogError(ex, "lifecycle tick failed"); }
            await Task.Delay(Interval, ct);
        }
    }

    public async Task RunReconcileAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
        var prov = scope.ServiceProvider.GetRequiredService<IProvisionerClient>();

        var liveList = await prov.ListManagedAsync(ct);
        var liveContainerIds = liveList.Select(m => m.ContainerId).ToHashSet();

        // Direction 1: metadata says active but container not in live set → Failed + release resources
        var active = await db.Instances.Where(i =>
            i.State == InstanceState.Running || i.State == InstanceState.Provisioning ||
            i.State == InstanceState.Destroying).ToListAsync(ct);

        foreach (var inst in active.Where(i => !liveContainerIds.Contains(i.ContainerId)))
        {
            inst.State = InstanceState.Failed;
            try { await prov.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct); }
            catch (Exception ex) { log.LogWarning(ex, "reconcile: release resources for {Id} failed", inst.Id); }
        }
        await db.SaveChangesAsync(ct);

        // Direction 2: live containers with no active metadata → orphan, destroy
        var knownContainerIds = active.Select(i => i.ContainerId).ToHashSet();
        foreach (var m in liveList)
        {
            if (!knownContainerIds.Contains(m.ContainerId))
            {
                try { await prov.DestroyByInstanceAsync(m.InstanceId, ct); }
                catch (Exception ex) { log.LogWarning(ex, "reconcile: orphan destroy {Id} failed", m.InstanceId); }
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
        var prov = scope.ServiceProvider.GetRequiredService<IProvisionerClient>();
        var queryExec = scope.ServiceProvider.GetRequiredService<IQueryExecutor>();
        var enforcer = scope.ServiceProvider.GetService<QuotaEnforcer>();
        var now = DateTime.UtcNow;

        // Reaper: destroy expired instances in Provisioning or Running state
        var expired = await db.Instances
            .Where(i => i.ExpiresAt <= now &&
                        (i.State == InstanceState.Running || i.State == InstanceState.Provisioning))
            .ToListAsync(ct);

        foreach (var inst in expired)
        {
            try { await prov.DestroyAsync(inst.ContainerId, inst.VolumeName, inst.PublicPort, ct); }
            catch (Exception ex) { log.LogWarning(ex, "destroy {Id} failed", inst.Id); }
            inst.State = InstanceState.Destroyed;
        }

        // Size monitor: update LastSizeBytes via real DB size (best-effort per instance)
        var running = await db.Instances
            .Where(i => i.State == InstanceState.Running)
            .ToListAsync(ct);

        foreach (var inst in running)
        {
            try
            {
                var result = await queryExec.ExecuteAsync(inst, "SELECT pg_database_size(current_database())", ct);
                if (result.Success && result.Rows.Count == 1 && result.Rows[0].Length == 1)
                {
                    inst.LastSizeBytes = Convert.ToInt64(result.Rows[0][0]);
                    if (enforcer is not null)
                    {
                        try { await enforcer.EnforceAsync(inst, inst.LastSizeBytes, ct); }
                        catch (Exception ex) { log.LogWarning(ex, "quota enforce {Id} failed", inst.Id); }
                    }
                }
            }
            catch (Exception ex) { log.LogDebug(ex, "size check {Id} failed", inst.Id); }
        }

        await db.SaveChangesAsync(ct);
    }
}
