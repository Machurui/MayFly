using FluentAssertions;
using MayFly.Api.Data;
using MayFly.Api.Domain;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Moq;
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

[Trait("Category", "Docker")]
public class InstanceServiceQuotaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine").Build();
    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private (InstanceService sut, MayFlyContext ctx) NewSut()
    {
        var ctx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options);
        ctx.Database.EnsureCreated();
        var prov = new Mock<IProvisionerClient>();
        prov.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionResult("cid", "vol", "host", 20002, "appdb", "appuser", "pw"));
        var sut = new InstanceService(ctx, prov.Object, new TokenService(),
            new SecretProtector(DataProtectionProvider.Create("t")));
        return (sut, ctx);
    }

    [Fact]
    public async Task Fourth_instance_for_same_ip_is_rejected()
    {
        var (sut, _) = NewSut();
        for (int i = 0; i < 3; i++)
            (await sut.CreateAsync("postgres", 3, 256, "blank", "9.9.9.9", "s", default))
                .QuotaExceeded.Should().BeFalse();
        var fourth = await sut.CreateAsync("postgres", 3, 256, "blank", "9.9.9.9", "s", default);
        fourth.QuotaExceeded.Should().BeTrue();
    }

    [Fact]
    public async Task GetByToken_returns_created_instance()
    {
        var (sut, _) = NewSut();
        var created = (await sut.CreateAsync("postgres", 6, 512, "blank", "8.8.8.8", "s", default)).Instance!;
        (await sut.GetByTokenAsync(created.CapabilityToken, default))!.PublicPort.Should().Be(20002);
    }
}
