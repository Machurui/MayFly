using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Import;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

using ApiExecRequest = MayFly.Api.Provisioning.ExecDumpRequest;
using ApiExecResult  = MayFly.Api.Provisioning.ExecDumpResult;
using ProvRequest    = MayFly.Provisioner.Contracts.ExecDumpRequest;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class ImportEndpointTests
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
    public async Task Import_restores_dump_via_DumpImporter()
    {
        await CleanLeakedContainersAsync();
        var realProvisioner = BuildProvisioner();
        var r = await realProvisioner.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);

        try
        {
            var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
            var inst = new Instance
            {
                Engine           = "postgres",
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
            prov.Setup(p => p.ExecDumpAsync(
                    It.IsAny<string>(),
                    It.IsAny<ApiExecRequest>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (string cid, ApiExecRequest req, CancellationToken ct) =>
                {
                    var pr = new ProvRequest(req.Engine, req.DumpContent, req.AdminUser, req.AdminPassword,
                        req.AppUser, req.Db, req.TimeoutSeconds, req.MaxOutputBytes);
                    var sr = await realProvisioner.ExecDumpAsync(cid, pr, ct);
                    return new ApiExecResult(sr.Output, sr.Error, sr.ExitCode, sr.Truncated, sr.Ms);
                });

            var importer = new DumpImporter(prov.Object, secrets);

            // 1. Valid SQL: create table and insert rows
            const string validDump = "CREATE TABLE imp(id int); INSERT INTO imp VALUES (1),(2);";
            var ok = await importer.ImportAsync(inst, validDump, default);
            ok.Success.Should().BeTrue($"valid dump must succeed; error: {ok.Error}");

            // Verify via appuser — ExecDumpAsync auto-grants access after restore, no manual GRANT needed
            var appCs = $"Host=localhost;Port={r.PublicPort};Database={r.DbName};" +
                        $"Username={r.DbUser};Password={r.DbPassword}";
            await WaitForPostgresAsync(appCs);

            await using var conn = new NpgsqlConnection(appCs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM imp", conn);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            count.Should().Be(2, "restored dump must have 2 rows in imp");

            // 2. Invalid SQL: must fail
            var bad = await importer.ImportAsync(inst, "this is not valid sql;;;", default);
            bad.Success.Should().BeFalse("invalid SQL must fail");
            (bad.Error ?? bad.Output).Should().NotBeNullOrEmpty("error/output must be populated on failure");
        }
        finally
        {
            await realProvisioner.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
        }
    }

    private static async Task WaitForPostgresAsync(string cs)
    {
        for (int i = 0; i < 60; i++)
        {
            try { await using var c = new NpgsqlConnection(cs); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new TimeoutException("postgres did not become ready within 60 s");
    }

    private static async Task CleanLeakedContainersAsync()
    {
        try
        {
            using var docker = new DockerClientBuilder().Build();
            var containers = await docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { ["mayfly-"] = true }
                    }
                }, default);

            foreach (var c in containers)
            {
                try
                {
                    await docker.Containers.RemoveContainerAsync(
                        c.ID, new ContainerRemoveParameters { Force = true }, default);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }
}
