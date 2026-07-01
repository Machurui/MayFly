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
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { log.LogError(ex, "lifecycle tick failed"); }
            await Task.Delay(Interval, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
        var prov = scope.ServiceProvider.GetRequiredService<IProvisionerClient>();
        var queryExec = scope.ServiceProvider.GetRequiredService<IQueryExecutor>();
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
                    inst.LastSizeBytes = Convert.ToInt64(result.Rows[0][0]);
            }
            catch (Exception ex) { log.LogDebug(ex, "size check {Id} failed", inst.Id); }
        }

        await db.SaveChangesAsync(ct);
    }
}
