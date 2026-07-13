using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class SeedTemplateTests
{
    private static IDockerProvisioner NewSut() =>
        new DockerProvisioner(
            new DockerClientBuilder().Build(),
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(new DockerClientBuilder().Build()),
            new IEngineProvider[]
            {
                new PostgresEngineProvider(),
                new MySqlEngineProvider(),
                new MariaDbEngineProvider(),
                new SqlServerEngineProvider()
            },
            NullLogger<DockerProvisioner>.Instance);

    [Theory(Timeout = 240000)]
    [InlineData("postgres",  "northwind",  "products")]
    [InlineData("mysql",     "northwind",  "products")]
    [InlineData("mariadb",   "northwind",  "products")]
    [InlineData("mssql",     "northwind",  "products")]
    [InlineData("postgres",  "ecommerce",  "products")]
    [InlineData("mysql",     "ecommerce",  "products")]
    [InlineData("mariadb",   "ecommerce",  "products")]
    [InlineData("mssql",     "ecommerce",  "products")]
    public async Task Template_seeds_table_readable_by_appuser(string engine, string template, string countTable)
    {
        await CleanLeakedContainersAsync();

        var sut = NewSut();
        var storageMb = engine == "mssql" ? 1024 : 256;
        var res = await sut.CreateAsync(new CreateInstanceRequest(engine, 3, storageMb, template), default);

        try
        {
            switch (engine)
            {
                case "postgres":
                    await AssertPostgres(res, countTable);
                    break;
                case "mysql":
                case "mariadb":
                    await AssertMySql(res, countTable);
                    break;
                case "mssql":
                    await AssertMssql(res, countTable);
                    break;
            }
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    // --- Postgres ---

    private static async Task AssertPostgres(CreateInstanceResult res, string countTable)
    {
        var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                 $"Username={res.DbUser};Password={res.DbPassword}";
        await WaitForPostgresAsync(cs);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {countTable}", conn);
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        count.Should().BeGreaterThan(0, $"seed must have populated {countTable} on postgres");
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

    // --- MySQL / MariaDB ---

    private static async Task AssertMySql(CreateInstanceResult res, string countTable)
    {
        var cs = $"Server=localhost;Port={res.PublicPort};Database={res.DbName};" +
                 $"User={res.DbUser};Password={res.DbPassword};AllowPublicKeyRetrieval=true;SslMode=None";
        await WaitForMySqlAsync(cs);

        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();

        await using var countCmd = new MySqlCommand($"SELECT COUNT(*) FROM {countTable}", conn);
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        count.Should().BeGreaterThan(0, $"seed must have populated {countTable} on mysql/mariadb");
    }

    private static async Task WaitForMySqlAsync(string cs)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                await using var c = new MySqlConnection(cs);
                await c.OpenAsync();
                return;
            }
            catch (Exception ex) { lastEx = ex; await Task.Delay(1000); }
        }
        throw new TimeoutException($"MySQL/MariaDB not ready after 90 s: {lastEx?.Message}", lastEx);
    }

    // --- SQL Server ---

    private static async Task AssertMssql(CreateInstanceResult res, string countTable)
    {
        var cs = $"Server=localhost,{res.PublicPort};Database={res.DbName};" +
                 $"User Id={res.DbUser};Password={res.DbPassword};" +
                 $"TrustServerCertificate=True;Encrypt=True;";
        await WaitForSqlServerAsync(cs);

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();

        await using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM {countTable}", conn);
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        count.Should().BeGreaterThan(0, $"seed must have populated {countTable} on mssql");
    }

    private static async Task WaitForSqlServerAsync(string cs)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 75; i++)
        {
            try
            {
                await using var c = new SqlConnection(cs);
                await c.OpenAsync();
                return;
            }
            catch (Exception ex) { lastEx = ex; await Task.Delay(2000); }
        }
        throw new TimeoutException(
            $"SQL Server not reachable after 75 attempts (150 s): {lastEx?.Message}", lastEx);
    }

    // --- Leak cleanup ---

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
