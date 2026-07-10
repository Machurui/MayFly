using Docker.DotNet;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class MySqlEngineClientTests
{
    [Fact]
    public async Task QueryExecutor_and_QuotaEnforcer_work_against_real_mysql()
    {
        var docker = new DockerClientBuilder().Build();
        var prov = new DockerProvisioner(
            docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            new IEngineProvider[] { new PostgresEngineProvider(), new MySqlEngineProvider() },
            NullLogger<DockerProvisioner>.Instance);

        var r = await prov.CreateAsync(new CreateInstanceRequest("mysql", 3, 256, "blank"), default);

        try
        {
            var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
            var inst = new Instance
            {
                Engine          = "mysql",
                InternalHost    = r.InternalHost,
                PublicPort      = r.PublicPort,
                DbName          = r.DbName,
                DbUser          = r.DbUser,
                DbPasswordEnc   = secrets.Protect(r.DbPassword),
                AdminUser       = r.AdminUser,
                AdminPasswordEnc = secrets.Protect(r.AdminPassword),
                StorageQuotaMb  = 256
            };

            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["QueryExecutor:UseInternalHost"] = "false" }).Build();

            var registry = new EngineClientRegistry(new IEngineClient[] { new MySqlEngineClient() });
            var executor  = new QueryExecutor(secrets, cfg, registry);
            var enforcer  = new QuotaEnforcer(secrets, cfg, NullLogger<QuotaEnforcer>.Instance, registry);

            // Wait for MySQL to be ready via the executor
            await WaitReadyAsync(executor, inst);

            // --- 1. SELECT 1 succeeds, returns one column and one row ---
            var ok = await executor.ExecuteAsync(inst, "SELECT 1 AS n", default);
            ok.Success.Should().BeTrue("SELECT 1 must succeed");
            ok.Columns.Should().ContainSingle().Which.Should().Be("n");
            ok.Rows.Should().ContainSingle();
            // MySqlConnector returns the integer 1L or 1 depending on the column type;
            // compare as converted long/int
            Convert.ToInt64(ok.Rows[0][0]).Should().Be(1L);

            // --- 2. Bad query → Success=false, Error non-empty ---
            var bad = await executor.ExecuteAsync(inst, "SELECT * FROM no_such_table", default);
            bad.Success.Should().BeFalse("unknown-table query must fail");
            bad.Error.Should().NotBeNullOrEmpty("error message must be populated on failure");

            // --- 3. Create table before enforcing so we can INSERT after ---
            var setup = await executor.ExecuteAsync(inst, "CREATE TABLE t (x INT)", default);
            setup.Success.Should().BeTrue("CREATE TABLE must succeed before enforcement");

            // --- 4. Soft-enforce: REVOKE writes from appuser ---
            // EnforceAsync only fires when sizeBytes >= quota; pass a value well over quota
            await enforcer.EnforceAsync(inst, 999_999_999L, default);

            // --- 5. Fresh connection as appuser → INSERT must be rejected ---
            // MySQL evaluates revoked grants on NEW connections, not existing pooled ones.
            var appCs = $"Server=localhost;Port={r.PublicPort};Database={r.DbName};" +
                        $"User={r.DbUser};Password={r.DbPassword};" +
                        $"AllowPublicKeyRetrieval=true;SslMode=None";

            await using var freshConn = new MySqlConnection(appCs);
            await freshConn.OpenAsync();
            await using var insertCmd = new MySqlCommand("INSERT INTO t VALUES (1)", freshConn);
            var ex = await Assert.ThrowsAsync<MySqlException>(() => insertCmd.ExecuteNonQueryAsync());
            ex.Should().NotBeNull("INSERT must throw after write privileges are revoked");
        }
        finally
        {
            await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
        }
    }

    private static async Task WaitReadyAsync(QueryExecutor sut, Instance inst)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 90; i++)
        {
            try
            {
                var res = await sut.ExecuteAsync(inst, "SELECT 1", default);
                if (res.Success) return;
            }
            catch (Exception ex) { lastEx = ex; }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"MySQL not ready after 90 s: {lastEx?.Message}", lastEx);
    }
}
