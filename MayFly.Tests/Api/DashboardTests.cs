using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Services;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class DashboardServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Summary_counts_alive_and_next_expiry()
    {
        var ctx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options);
        ctx.Database.EnsureCreated();

        // Capture alive instance so its Id can be used as FK for QueryLog rows (Fix 2)
        var aliveInstance = new Instance
        {
            SessionId = "s",
            State = InstanceState.Running,
            CapabilityToken = "tok-alive",
            LastSizeBytes = 1000,
            ExpiresAt = DateTime.UtcNow.AddHours(2),
            CreatedAt = DateTime.UtcNow
        };
        ctx.Instances.Add(aliveInstance);

        // Destroyed instance: ExpiresAt is EARLIER (+1h) – must be excluded from NextExpiry (Fix 1)
        ctx.Instances.Add(new Instance
        {
            SessionId = "s",
            State = InstanceState.Destroyed,
            CapabilityToken = "tok-dead",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow
        });

        // Cross-session instance: must not bleed into session "s" aggregates (Fix 3)
        ctx.Instances.Add(new Instance
        {
            SessionId = "other-session",
            State = InstanceState.Running,
            CapabilityToken = "tok-other",
            LastSizeBytes = 999,
            ExpiresAt = DateTime.UtcNow.AddHours(3),
            CreatedAt = DateTime.UtcNow
        });

        await ctx.SaveChangesAsync();

        // Seed QueryLog rows for the alive instance (Fix 2)
        ctx.QueryLogs.Add(new QueryLog
        {
            InstanceId = aliveInstance.Id,
            ExecutedAt = DateTime.UtcNow,          // today → should count
            DurationMs = 10,
            RowCount = 5,
            Success = true
        });
        ctx.QueryLogs.Add(new QueryLog
        {
            InstanceId = aliveInstance.Id,
            ExecutedAt = DateTime.UtcNow.AddDays(-1), // yesterday → should NOT count
            DurationMs = 10,
            RowCount = 5,
            Success = true
        });
        await ctx.SaveChangesAsync();

        var sut = new DashboardService(ctx);
        var d = await sut.SummaryAsync("s", default);

        // Original assertions
        d.AliveCount.Should().Be(1);
        d.StorageUsedBytes.Should().Be(1000);
        d.NextExpiry.Should().NotBeNull();

        // Fix 1: NextExpiry must be the Running instance's time (+2h), not the Destroyed one (+1h)
        d.NextExpiry!.Value.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(5));

        // Fix 2: only today's query counts
        d.QueriesToday.Should().Be(1);

        // Fix 3: cross-session isolation — aggregates must ignore the "other-session" instance
        d.AliveCount.Should().Be(1);
        d.StorageUsedBytes.Should().Be(1000);
    }
}
