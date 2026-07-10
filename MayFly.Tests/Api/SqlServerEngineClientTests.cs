using Docker.DotNet;
using Docker.DotNet.Models;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Engines;
using MayFly.Api.Lifecycle;
using MayFly.Api.Security;
using MayFly.Api.Services;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Engines;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class SqlServerEngineClientTests
{
    // ARM64 host (Apple Silicon): mssql runs under amd64 emulation — boot is ~60-120s.
    // Fact timeout is 4 minutes to accommodate emulated boot + readiness polling (75×2s=150s).
    [Fact(Timeout = 240000)]
    public async Task QueryExecutor_and_QuotaEnforcer_work_against_real_mssql()
    {
        // Clean leaked containers from previous failed runs.
        await CleanLeakedContainersAsync();

        var docker = new DockerClientBuilder().Build();
        var prov = new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[]
            {
                new PostgresEngineProvider(),
                new MySqlEngineProvider(),
                new MariaDbEngineProvider(),
                new SqlServerEngineProvider()
            },
            NullLogger<DockerProvisioner>.Instance);

        var r = await prov.CreateAsync(new CreateInstanceRequest("mssql", 3, 256, "blank"), default);

        try
        {
            var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
            var inst = new Instance
            {
                Engine           = "mssql",
                InternalHost     = r.InternalHost,
                PublicPort       = r.PublicPort,
                DbName           = r.DbName,
                DbUser           = r.DbUser,
                DbPasswordEnc    = secrets.Protect(r.DbPassword),
                AdminUser        = r.AdminUser,
                AdminPasswordEnc = secrets.Protect(r.AdminPassword),
                StorageQuotaMb   = 256
            };

            // dev mode: connect via localhost:PublicPort (the sidecar)
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["QueryExecutor:UseInternalHost"] = "false" }).Build();

            var registry = new EngineClientRegistry(new IEngineClient[] { new SqlServerEngineClient() });
            var executor  = new QueryExecutor(secrets, cfg, registry);
            var enforcer  = new QuotaEnforcer(secrets, cfg, NullLogger<QuotaEnforcer>.Instance, registry);

            // Wait for SQL Server to accept connections (emulated mssql can take 60-120s)
            await WaitReadyAsync(executor, inst);

            // --- 1. SELECT 1 succeeds, returns one column and one row ---
            var ok = await executor.ExecuteAsync(inst, "SELECT 1 AS n", default);
            ok.Success.Should().BeTrue("SELECT 1 must succeed");
            ok.Columns.Should().ContainSingle().Which.Should().Be("n");
            ok.Rows.Should().ContainSingle();
            Convert.ToInt32(ok.Rows[0][0]).Should().Be(1);

            // --- 2. Bad query → Success=false, Error non-empty ---
            var bad = await executor.ExecuteAsync(inst, "SELECT * FROM no_such_table", default);
            bad.Success.Should().BeFalse("unknown-table query must fail");
            bad.Error.Should().NotBeNullOrEmpty("error message must be populated on failure");

            // --- 3. Create table as appuser before enforcing so we can INSERT after ---
            var setup = await executor.ExecuteAsync(inst, "CREATE TABLE t (x INT)", default);
            setup.Success.Should().BeTrue("CREATE TABLE must succeed before enforcement");

            // --- 4. Soft-enforce: set DATABASE read-only via admin ---
            // EnforceAsync only fires when sizeBytes >= quota; pass a value well over quota
            await enforcer.EnforceAsync(inst, 999_999_999L, default);

            // --- 5. Fresh appuser connection → INSERT must be rejected (db is read-only) ---
            var appCs = $"Server=localhost,{r.PublicPort};Database={r.DbName};" +
                        $"User Id={r.DbUser};Password={r.DbPassword};" +
                        $"TrustServerCertificate=True;Encrypt=True;";

            await using var freshConn = new SqlConnection(appCs);
            await freshConn.OpenAsync();
            await using var insertCmd = new SqlCommand("INSERT INTO t VALUES (1)", freshConn);
            var ex = await Assert.ThrowsAsync<SqlException>(() => insertCmd.ExecuteNonQueryAsync());
            ex.Should().NotBeNull("INSERT must throw after database is set to read-only");
        }
        finally
        {
            await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
        }
    }

    private static async Task WaitReadyAsync(QueryExecutor executor, Instance inst)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 75; i++)
        {
            try
            {
                var res = await executor.ExecuteAsync(inst, "SELECT 1", default);
                if (res.Success) return;
            }
            catch (Exception ex) { lastEx = ex; }
            await Task.Delay(2000);
        }
        throw new TimeoutException(
            $"SQL Server not ready after 75 attempts (150 s): {lastEx?.Message}", lastEx);
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
