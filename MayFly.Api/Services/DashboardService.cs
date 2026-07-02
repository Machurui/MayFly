using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace MayFly.Api.Services;

public sealed class DashboardService(MayFlyContext db)
{
    public async Task<DashboardSummary> SummaryAsync(string sessionId, CancellationToken ct)
    {
        var alive = db.Instances.Where(i => i.SessionId == sessionId &&
            (i.State == InstanceState.Running || i.State == InstanceState.Provisioning));
        var today = DateTime.UtcNow.Date;
        var aliveIds = await alive.Select(i => i.Id).ToListAsync(ct);
        var queries = await db.QueryLogs.CountAsync(q => aliveIds.Contains(q.InstanceId) &&
            q.ExecutedAt >= today, ct);
        return new DashboardSummary(
            await alive.CountAsync(ct), 3, queries,
            await alive.SumAsync(i => i.LastSizeBytes, ct),
            await alive.OrderBy(i => i.ExpiresAt).Select(i => (DateTime?)i.ExpiresAt).FirstOrDefaultAsync(ct));
    }
}
