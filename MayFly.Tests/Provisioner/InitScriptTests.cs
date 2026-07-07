using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

[Collection("docker-sequential")]
[Trait("Category", "Docker")]
public class InitScriptTests
{
    private static IDockerProvisioner NewSut()
    {
        var docker = new DockerClientBuilder().Build();
        return new DockerProvisioner(docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            NullLogger<DockerProvisioner>.Instance);
    }

    [Fact]
    public async Task Northwind_init_script_seeds_roles_and_data_without_external_connection()
    {
        var sut = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "northwind"), default);
        try
        {
            res.DbUser.Should().Be("appuser");
            res.AdminUser.Should().Be("mayflyadmin");

            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.DbUser};Password={res.DbPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            // appuser must NOT be superuser
            await using var rolCmd = new NpgsqlCommand(
                "SELECT rolsuper FROM pg_roles WHERE rolname='appuser'", conn);
            var rolsuper = (bool)(await rolCmd.ExecuteScalarAsync())!;
            rolsuper.Should().BeFalse("appuser must be NOSUPERUSER");

            // Northwind seeded via init script — products table must have rows
            await using var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM products", conn);
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            count.Should().BeGreaterThan(0, "northwind seed must have populated products");

            // appuser can do ordinary DDL
            await using var ddlCmd = new NpgsqlCommand("CREATE TABLE t_init_test(x int)", conn);
            await ddlCmd.Awaiting(c => c.ExecuteNonQueryAsync()).Should().NotThrowAsync(
                "appuser must be able to do ordinary DDL");

            // COPY TO PROGRAM must be rejected for non-superuser
            await using var copyCmd = new NpgsqlCommand("COPY (SELECT 1) TO PROGRAM 'id'", conn);
            await copyCmd.Awaiting(c => c.ExecuteNonQueryAsync())
                .Should().ThrowAsync<PostgresException>(
                    "appuser must not be allowed to run COPY … TO PROGRAM");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    private static async Task WaitForPostgresAsync(string cs)
    {
        for (int i = 0; i < 60; i++)
        {
            try
            {
                await using var c = new NpgsqlConnection(cs);
                await c.OpenAsync();
                return;
            }
            catch { await Task.Delay(1000); }
        }
        throw new TimeoutException("postgres did not become ready within 60 s");
    }
}
