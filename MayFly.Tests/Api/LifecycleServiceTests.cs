using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Engines;
using MayFly.Api.Lifecycle;
using MayFly.Api.Provisioning;
using MayFly.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class LifecycleServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Reconcile_marks_running_with_missing_container_as_failed()
    {
        // Arrange: real metadata DB, Running instance whose ContainerId is absent from live Docker set.
        // Mock ListManagedAsync returns empty → no live containers.
        var services = new ServiceCollection();
        services.AddDbContext<MayFlyContext>(o => o.UseNpgsql(_db.GetConnectionString()));
        var prov = new Mock<IProvisionerClient>();
        prov.Setup(p => p.ListManagedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ManagedContainer>());
        services.AddSingleton(prov.Object);
        services.AddScoped(_ => Mock.Of<IQueryExecutor>());
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
            ctx.Database.EnsureCreated();
            ctx.Instances.Add(new Instance
            {
                CapabilityToken = "x", ContainerId = "dead-container-abc", VolumeName = "dead-vol",
                PublicPort = 20099, State = InstanceState.Running,
                CreatedAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddHours(2)
            });
            await ctx.SaveChangesAsync();
        }

        var registry = new EngineClientRegistry(new IEngineClient[] { new PostgresEngineClient() });
        var sut = new LifecycleService(sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<LifecycleService>>(), registry);

        // Act
        await sut.RunReconcileAsync(default);

        // Assert: instance marked Failed and DestroyAsync called to release port
        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MayFlyContext>();
        (await db.Instances.SingleAsync()).State.Should().Be(InstanceState.Failed);
        prov.Verify(p => p.DestroyAsync("dead-container-abc", "dead-vol", 20099,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reconcile_redrives_Destroying_row_and_sweeps_orphans()
    {
        // Arrange: a row stuck in Destroying state (simulates a crash between claim and provisioner call).
        // ListManagedAsync returns empty (DB container is already gone).
        var services = new ServiceCollection();
        services.AddDbContext<MayFlyContext>(o => o.UseNpgsql(_db.GetConnectionString()));
        var prov = new Mock<IProvisionerClient>();
        prov.Setup(p => p.ListManagedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ManagedContainer>());
        prov.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        prov.Setup(p => p.SweepOrphansAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        services.AddSingleton(prov.Object);
        services.AddScoped(_ => Mock.Of<IQueryExecutor>());
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
            ctx.Database.EnsureCreated();
            ctx.Instances.Add(new Instance
            {
                CapabilityToken = "y", ContainerId = "d-container-xyz", VolumeName = "mayfly-vol-abc123",
                PublicPort = 20077, State = InstanceState.Destroying,
                CreatedAt = DateTime.UtcNow.AddHours(-2), ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await ctx.SaveChangesAsync();
        }

        var registry = new EngineClientRegistry(new IEngineClient[] { new PostgresEngineClient() });
        var sut = new LifecycleService(sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<LifecycleService>>(), registry);

        // Act
        await sut.RunReconcileAsync(default);

        // Assert: row became Destroyed and DestroyAsync was called exactly once
        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MayFlyContext>();
        (await db.Instances.SingleAsync()).State.Should().Be(InstanceState.Destroyed);
        prov.Verify(p => p.DestroyAsync("d-container-xyz", "mayfly-vol-abc123", 20077,
            It.IsAny<CancellationToken>()), Times.Once);

        // SweepOrphansAsync should be called; the Destroying instance's volume must NOT be in the active set
        // (it was Destroyed in the pre-pass, so active = Running/Provisioning only, which is empty here)
        prov.Verify(p => p.SweepOrphansAsync(
            It.Is<IReadOnlyCollection<string>>(v => !v.Contains("mayfly-vol-abc123")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reaper_destroys_expired_and_marks_state()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MayFlyContext>(o => o.UseNpgsql(_db.GetConnectionString()));
        var prov = new Mock<IProvisionerClient>();
        services.AddSingleton(prov.Object);
        services.AddScoped(_ => Mock.Of<IQueryExecutor>());
        var sp = services.BuildServiceProvider();

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MayFlyContext>();
            ctx.Database.EnsureCreated();
            ctx.Instances.Add(new Instance
            {
                CapabilityToken = "x", ContainerId = "c", VolumeName = "v", PublicPort = 20003,
                State = InstanceState.Running, CreatedAt = DateTime.UtcNow.AddHours(-4),
                ExpiresAt = DateTime.UtcNow.AddHours(-1)   // already expired
            });
            await ctx.SaveChangesAsync();
        }

        var registry = new EngineClientRegistry(new IEngineClient[] { new PostgresEngineClient() });
        var sut = new LifecycleService(sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<LifecycleService>>(), registry);
        await sut.RunOnceAsync(default);

        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MayFlyContext>();
        (await db.Instances.SingleAsync()).State.Should().Be(InstanceState.Destroyed);
        prov.Verify(p => p.DestroyAsync("c", "v", 20003, It.IsAny<CancellationToken>()), Times.Once);
    }
}
