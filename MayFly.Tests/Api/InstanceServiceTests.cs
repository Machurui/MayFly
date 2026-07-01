using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
public class MetadataContextTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private MayFlyContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options;
        var ctx = new MayFlyContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public async Task Can_persist_and_load_instance()
    {
        await using var ctx = NewContext();
        var inst = new Instance
        {
            CapabilityToken = "tok", SessionId = "sess", CreatorIp = "1.2.3.4",
            Engine = "postgres", TtlHours = 3, StorageQuotaMb = 256, InitialData = "blank",
            ContainerId = "c", InternalHost = "h", PublicPort = 20000,
            DbName = "appdb", DbUser = "appuser", DbPasswordEnc = "enc",
            State = InstanceState.Running, CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(3)
        };
        ctx.Instances.Add(inst);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Instances.SingleAsync(i => i.CapabilityToken == "tok");
        loaded.PublicPort.Should().Be(20000);
        loaded.State.Should().Be(InstanceState.Running);
    }
}
