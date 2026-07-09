using Docker.DotNet;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Security;
using MayFly.Api.Services;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Collection("docker-sequential")]
[Trait("Category", "Docker")]
public class QueryExecutorTests
{
    [Fact]
    public async Task Executes_select_against_real_postgres()
    {
        var docker = new DockerClientBuilder().Build();
        var prov = new DockerProvisioner(docker, new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker), new[] { new PostgresEngineProvider() },
            NullLogger<DockerProvisioner>.Instance);
        var r = await prov.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
        var inst = new Instance
        {
            InternalHost = r.InternalHost, PublicPort = r.PublicPort, DbName = r.DbName,
            DbUser = r.DbUser, DbPasswordEnc = secrets.Protect(r.DbPassword)
        };
        try
        {
            // dev mode: connect via localhost public port
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["QueryExecutor:UseInternalHost"] = "false" }).Build();
            var sut = new QueryExecutor(secrets, cfg);
            await WaitReady(sut, inst);

            var res = await sut.ExecuteAsync(inst, "SELECT 1 AS n", default);
            res.Success.Should().BeTrue();
            res.Columns.Should().ContainSingle().Which.Should().Be("n");
            res.Rows.Should().ContainSingle();
            res.Rows[0][0].Should().Be(1);

            var bad = await sut.ExecuteAsync(inst, "SELECT * FROM nope", default);
            bad.Success.Should().BeFalse();
            bad.Error.Should().NotBeNullOrEmpty();
        }
        finally { await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default); }
    }

    private static async Task WaitReady(QueryExecutor sut, Instance inst)
    {
        for (int i = 0; i < 30; i++)
        {
            var res = await sut.ExecuteAsync(inst, "SELECT 1", default);
            if (res.Success) return;
            await Task.Delay(1000);
        }
        throw new TimeoutException("not ready");
    }
}
