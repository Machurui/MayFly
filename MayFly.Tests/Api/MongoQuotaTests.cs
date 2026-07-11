using Docker.DotNet;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Mongo;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using ApiExecRequest = MayFly.Api.Provisioning.ExecMongoshRequest;
using ApiExecResult  = MayFly.Api.Provisioning.ExecMongoshResult;
using ProvRequest    = MayFly.Provisioner.Contracts.ExecMongoshRequest;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class MongoQuotaTests
{
    private static IDockerProvisioner BuildProvisioner()
    {
        var docker = new DockerClientBuilder().Build();
        return new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[]
            {
                new PostgresEngineProvider(),
                new MySqlEngineProvider(),
                new MariaDbEngineProvider(),
                new SqlServerEngineProvider(),
                new MongoEngineProvider()
            },
            NullLogger<DockerProvisioner>.Instance);
    }

    [Fact(Timeout = 120_000)]
    public async Task Mongo_soft_enforce_flips_appuser_read_only()
    {
        var realProvisioner = BuildProvisioner();
        var r = await realProvisioner.CreateAsync(new CreateInstanceRequest("mongo", 3, 256, "blank"), default);

        try
        {
            var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
            var inst = new Instance
            {
                Engine           = "mongo",
                ContainerId      = r.ContainerId,
                InternalHost     = r.InternalHost,
                PublicPort       = r.PublicPort,
                DbName           = r.DbName,
                DbUser           = r.DbUser,
                DbPasswordEnc    = secrets.Protect(r.DbPassword),
                AdminUser        = r.AdminUser,
                AdminPasswordEnc = secrets.Protect(r.AdminPassword),
                StorageQuotaMb   = 256
            };

            var prov = new Mock<IProvisionerClient>();
            prov.Setup(p => p.ExecMongoshAsync(
                    It.IsAny<string>(),
                    It.IsAny<ApiExecRequest>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (string cid, ApiExecRequest req, CancellationToken ct) =>
                {
                    var pr = new ProvRequest(req.Command, req.User, req.Password, req.AuthDb,
                        req.TimeoutSeconds, req.MaxOutputBytes);
                    var sr = await realProvisioner.ExecMongoshAsync(cid, pr, ct);
                    return new ApiExecResult(sr.Output, sr.Error, sr.ExitCode, sr.Truncated, sr.Ms);
                });

            var mongoOps = new MongoOps(prov.Object, secrets, NullLogger<MongoOps>.Instance);

            // 1. Insert a doc as appuser
            var insert = await mongoOps.RunConsoleAsync(inst, "db.getCollection('t').insertOne({x:1})", default);
            insert.Success.Should().BeTrue("appuser insert must succeed before enforcement");

            // 2. Measure size via dbStats (admin exec)
            var size = await mongoOps.GetSizeBytesAsync(inst, default);
            size.Should().BeGreaterThan(0, "dbStats must report non-zero storage after insert");

            // 3. Flip appuser to read-only
            await mongoOps.SoftEnforceReadOnlyAsync(inst, default);

            // 4. Attempt a write as appuser in a fresh mongosh — must be rejected
            var writeAfter = await mongoOps.RunConsoleAsync(inst, "db.getCollection('t').insertOne({y:2})", default);
            writeAfter.Success.Should().BeFalse("write must fail after appuser is flipped to read-only");
        }
        finally
        {
            await realProvisioner.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
        }
    }
}
