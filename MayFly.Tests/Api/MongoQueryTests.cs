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
public class MongoQueryTests
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
    public async Task Mongo_console_runs_via_provisioner_exec()
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
                AdminPasswordEnc = secrets.Protect(r.AdminPassword)
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

            var mongoOps = new MongoOps(prov.Object, secrets);

            // 1. Valid command: insert a doc and retrieve it
            var ok = await mongoOps.RunConsoleAsync(inst,
                "db.getCollection('t').insertOne({x:1}); db.getCollection('t').find().toArray()",
                default);
            ok.Success.Should().BeTrue("valid mongosh command must succeed");
            ok.Output.Should().NotBeNullOrEmpty("output must contain the inserted document");

            // 2. Invalid JS: must fail with a non-empty error
            var bad = await mongoOps.RunConsoleAsync(inst, "this is not valid js;;;", default);
            bad.Success.Should().BeFalse("invalid JS must fail");
            bad.Error.Should().NotBeNullOrEmpty("error must be populated on failure");
        }
        finally
        {
            await realProvisioner.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
        }
    }
}
