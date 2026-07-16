using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlConnector;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class ExecDumpTests
{
    private static IDockerProvisioner NewSut()
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

    [Theory(Timeout = 240000)]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("mssql")]
    [InlineData("mongo")]
    public async Task Dump_restores_data_as_admin(string engine)
    {
        await CleanLeakedContainersAsync();
        var sut = NewSut();
        int storageMb = engine == "mssql" ? 1024 : 256;
        var res = await sut.CreateAsync(new CreateInstanceRequest(engine, 3, storageMb, "blank"), default);

        try
        {
            string dump = engine == "mongo"
                ? "db.getSiblingDB('appdb').getCollection('imp').insertMany([{_id:1,name:'x'},{_id:2,name:'y'}]);"
                : "CREATE TABLE imp (id INT PRIMARY KEY, name VARCHAR(50)); INSERT INTO imp (id,name) VALUES (1,'x'),(2,'y');";

            var req = new ExecDumpRequest(engine, dump, res.AdminUser, res.AdminPassword, res.DbName, 60, 256 * 1024);
            var result = await sut.ExecDumpAsync(res.ContainerId, req, default);

            result.ExitCode.Should().Be(0, $"{engine} dump should restore successfully; stderr: {result.Error}");

            long count = await CountRestoredRowsAsync(engine, res);
            count.Should().Be(2, $"{engine}: restored table/collection 'imp' should have 2 rows/docs");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    [Fact(Timeout = 180000)]
    public async Task Postgres_dump_can_run_admin_only_statements()
    {
        await CleanLeakedContainersAsync();
        var sut = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);

        try
        {
            const string dump =
                "CREATE EXTENSION IF NOT EXISTS pg_trgm;\n" +
                "CREATE TABLE ext_t(id int);\n" +
                "INSERT INTO ext_t VALUES (1);";

            var req = new ExecDumpRequest("postgres", dump, res.AdminUser, res.AdminPassword, res.DbName, 60, 256 * 1024);
            var result = await sut.ExecDumpAsync(res.ContainerId, req, default);

            result.ExitCode.Should().Be(0,
                $"admin must be able to CREATE EXTENSION (proves admin role); stderr: {result.Error}");

            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.AdminUser};Password={res.AdminPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM ext_t", conn);
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            count.Should().Be(1, "admin dump should have inserted 1 row into ext_t");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    /// <summary>
    /// Regression guard: a dump larger than the old ~96 KiB env-var ceiling must succeed.
    /// This test builds ~1.2 MiB of SQL (5 000 INSERT rows × ~233 B) and restores it via
    /// ExecDumpAsync.  It FAILS against the previous base64-via-DUMP_B64 delivery (E2BIG /
    /// execve) and PASSES with the stdin-pipe delivery.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Postgres_large_dump_exceeds_env_ceiling()
    {
        await CleanLeakedContainersAsync();
        var sut = NewSut();
        var res = await sut.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);

        try
        {
            const int rowCount = 5_000;
            var payload = new string('x', 200);   // 200-char VARCHAR padding

            // Build the dump: DDL + a GRANT so appuser can SELECT, then the bulk INSERTs.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CREATE TABLE big(id INT PRIMARY KEY, payload VARCHAR(200));");
            sb.AppendLine($"GRANT SELECT ON big TO {res.DbUser};");
            for (int i = 1; i <= rowCount; i++)
                sb.AppendLine($"INSERT INTO big VALUES ({i}, '{payload}');");

            string dump = sb.ToString();
            // Assert dump is above the old env ceiling (~96 KiB) — otherwise this test is vacuous.
            dump.Length.Should().BeGreaterThan(96 * 1024,
                "the large-dump test must exercise more than the old env-var ceiling");

            var req = new ExecDumpRequest("postgres", dump, res.AdminUser, res.AdminPassword, res.DbName, 90, 256 * 1024);
            var result = await sut.ExecDumpAsync(res.ContainerId, req, default);

            result.ExitCode.Should().Be(0,
                $"large postgres dump should restore successfully; stderr: {result.Error}");

            // Verify via the app user that all rows landed.
            var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                     $"Username={res.DbUser};Password={res.DbPassword}";
            await WaitForPostgresAsync(cs);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM big", conn);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            count.Should().Be(rowCount, $"all {rowCount} inserted rows must be present after large-dump restore");
        }
        finally
        {
            await sut.DestroyAsync(res.ContainerId, res.VolumeName, res.PublicPort, default);
        }
    }

    // -------------------------------------------------------------------------
    // Verification helpers
    // -------------------------------------------------------------------------

    private static async Task<long> CountRestoredRowsAsync(string engine, CreateInstanceResult res)
    {
        switch (engine)
        {
            case "postgres":
            {
                var cs = $"Host=localhost;Port={res.PublicPort};Database={res.DbName};" +
                         $"Username={res.AdminUser};Password={res.AdminPassword}";
                await WaitForPostgresAsync(cs);
                await using var conn = new NpgsqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM imp", conn);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            case "mysql":
            case "mariadb":
            {
                var cs = $"Server=localhost;Port={res.PublicPort};Database={res.DbName};" +
                         $"User={res.AdminUser};Password={res.AdminPassword};" +
                         $"AllowPublicKeyRetrieval=true;SslMode=None";
                await WaitForMySqlAsync(cs);
                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM imp", conn);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            case "mssql":
            {
                var cs = $"Server=localhost,{res.PublicPort};Database={res.DbName};" +
                         $"User Id={res.AdminUser};Password={res.AdminPassword};" +
                         $"TrustServerCertificate=True;Encrypt=True;";
                await WaitForSqlServerAsync(cs);
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT COUNT(*) FROM imp", conn);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }
            case "mongo":
            {
                var cs = $"mongodb://{res.DbUser}:{res.DbPassword}@localhost:{res.PublicPort}/{res.DbName}" +
                         $"?authSource={res.DbName}";
                await WaitForMongoAsync(cs, res.DbName);
                var client = new MongoClient(cs);
                var db = client.GetDatabase(res.DbName);
                return await db.GetCollection<BsonDocument>("imp")
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            }
            default:
                throw new InvalidOperationException($"Unknown engine: {engine}");
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

    private static async Task WaitForMySqlAsync(string cs)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try { await using var c = new MySqlConnection(cs); await c.OpenAsync(); return; }
            catch (Exception ex) { lastEx = ex; await Task.Delay(1000); }
        }
        throw new TimeoutException($"MySQL/MariaDB not ready after 90 s: {lastEx?.Message}", lastEx);
    }

    private static async Task WaitForSqlServerAsync(string cs)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 75; i++)
        {
            try { await using var c = new SqlConnection(cs); await c.OpenAsync(); return; }
            catch (Exception ex) { lastEx = ex; await Task.Delay(2000); }
        }
        throw new TimeoutException(
            $"SQL Server not reachable after 75 attempts (150 s): {lastEx?.Message}", lastEx);
    }

    private static async Task WaitForMongoAsync(string cs, string dbName)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                var client = new MongoClient(MongoClientSettings.FromConnectionString(cs));
                var db = client.GetDatabase(dbName);
                await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
                return;
            }
            catch (Exception ex) { lastEx = ex; await Task.Delay(1000); }
        }
        throw new TimeoutException($"MongoDB not ready after 90 attempts: {lastEx?.Message}", lastEx);
    }

    // -------------------------------------------------------------------------
    // Leak cleanup
    // -------------------------------------------------------------------------

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
