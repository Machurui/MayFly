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

    private (InstanceService sut, MayFlyContext ctx) NewSut(
        Mock<ITokenService>? tokenMock = null,
        Mock<IProvisionerClient>? provMock = null)
    {
        var ctx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
            .UseNpgsql(_db.GetConnectionString()).Options);
        ctx.Database.EnsureCreated();

        if (provMock is null)
        {
            provMock = new Mock<IProvisionerClient>();
            provMock.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProvisionResult("cid", "vol", "host", 20002, "appdb", "appuser", "pw", "mayflyadmin", "adminpw"));
        }

        ITokenService tokenService = tokenMock is not null ? tokenMock.Object : new TokenService();

        var sut = new InstanceService(ctx, provMock.Object, tokenService,
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

    [Fact]
    public async Task OrphanContainer_IsDestroyed_WhenSaveChangesFails()
    {
        // Pre-insert a row with the fixed token (different IP – won't trigger quota for 5.5.5.5)
        const string dupeToken = "dupe-token";
        {
            await using var seedCtx = new MayFlyContext(new DbContextOptionsBuilder<MayFlyContext>()
                .UseNpgsql(_db.GetConnectionString()).Options);
            seedCtx.Database.EnsureCreated();
            seedCtx.Instances.Add(new Instance
            {
                CapabilityToken = dupeToken, SessionId = "seed", CreatorIp = "1.1.1.1",
                Engine = "postgres", TtlHours = 3, StorageQuotaMb = 256, InitialData = "blank",
                ContainerId = "seed-cid", VolumeName = "seed-vol", InternalHost = "h", PublicPort = 20099,
                DbName = "appdb", DbUser = "appuser", DbPasswordEnc = "enc",
                State = InstanceState.Running, CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(3)
            });
            await seedCtx.SaveChangesAsync();
        }

        var tokenMock = new Mock<ITokenService>();
        tokenMock.Setup(t => t.NewToken()).Returns(dupeToken);
        tokenMock.Setup(t => t.Matches(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var provMock = new Mock<IProvisionerClient>();
        provMock.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionResult("orphan-cid", "orphan-vol", "host", 20003, "appdb", "appuser", "pw", "mayflyadmin", "adminpw"));
        provMock.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (sut, _) = NewSut(tokenMock, provMock);

        // SaveChanges throws a unique-constraint violation on CapabilityToken
        Func<Task> act = () => sut.CreateAsync("postgres", 3, 256, "blank", "5.5.5.5", "s", default);
        await act.Should().ThrowAsync<Exception>();

        // The just-provisioned container must be destroyed, not orphaned
        provMock.Verify(p => p.DestroyAsync("orphan-cid", "orphan-vol", 20003,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DestroyAsync_is_idempotent_single_transition()
    {
        var provMock = new Mock<IProvisionerClient>();
        provMock.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionResult("cid", "vol", "host", 20010, "appdb", "appuser", "pw", "mayflyadmin", "adminpw"));
        provMock.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (sut, ctx) = NewSut(provMock: provMock);
        var created = (await sut.CreateAsync("postgres", 3, 256, "blank", "7.7.7.7", "s", default)).Instance!;
        var token = created.CapabilityToken;

        var first = await sut.DestroyAsync(token, default);
        var second = await sut.DestroyAsync(token, default);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await ctx.Instances.AsNoTracking().SingleAsync(i => i.CapabilityToken == token)).State
            .Should().Be(InstanceState.Destroyed);
        provMock.Verify(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DestroyAsync_concurrent_calls_only_one_destroys()
    {
        // Arrange: shared provisioner mock with both Create and Destroy set up.
        var provMock = new Mock<IProvisionerClient>();
        provMock.Setup(p => p.CreateAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisionResult("cid-conc", "vol-conc", "host", 20020, "appdb", "appuser", "pw", "mayflyadmin", "adminpw"));
        provMock.Setup(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Two services sharing the same metadata DB but with independent EF Core contexts.
        var (svcA, _)    = NewSut(provMock: provMock);
        var (svcB, ctxB) = NewSut(provMock: provMock);

        // Create the instance via svcA; capture the capability token.
        var token = (await svcA.CreateAsync("postgres", 3, 256, "blank", "6.6.6.6", "s", default)).Instance!.CapabilityToken;

        // Act: fire both destroys concurrently — a naive read-then-act would let both through.
        var results = await Task.WhenAll(svcA.DestroyAsync(token, default), svcB.DestroyAsync(token, default));

        // Assert: exactly one caller wins the atomic DB-level claim; provisioner called once.
        results.Count(r => r).Should().Be(1, "exactly one concurrent caller should win the atomic DB claim");
        (await ctxB.Instances.AsNoTracking().SingleAsync(i => i.CapabilityToken == token)).State
            .Should().Be(InstanceState.Destroyed);
        provMock.Verify(p => p.DestroyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
