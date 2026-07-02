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
        ctx.Instances.Add(new Instance { SessionId = "s", State = InstanceState.Running,
            CapabilityToken = "tok-alive",
            LastSizeBytes = 1000, ExpiresAt = DateTime.UtcNow.AddHours(2), CreatedAt = DateTime.UtcNow });
        ctx.Instances.Add(new Instance { SessionId = "s", State = InstanceState.Destroyed,
            CapabilityToken = "tok-dead",
            ExpiresAt = DateTime.UtcNow.AddHours(1), CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var sut = new DashboardService(ctx);
        var d = await sut.SummaryAsync("s", default);
        d.AliveCount.Should().Be(1);
        d.StorageUsedBytes.Should().Be(1000);
        d.NextExpiry.Should().NotBeNull();
    }
}
