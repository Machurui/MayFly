using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
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

        var sut = new LifecycleService(sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<LifecycleService>>());
        await sut.RunOnceAsync(default);

        using var verify = sp.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<MayFlyContext>();
        (await db.Instances.SingleAsync()).State.Should().Be(InstanceState.Destroyed);
        prov.Verify(p => p.DestroyAsync("c", "v", 20003, It.IsAny<CancellationToken>()), Times.Once);
    }
}
