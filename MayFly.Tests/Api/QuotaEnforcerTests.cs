using Docker.DotNet;
using FluentAssertions;
using MayFly.Api.Domain;
using MayFly.Api.Lifecycle;
using MayFly.Api.Security;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class QuotaEnforcerTests
{
    [Fact]
    public async Task Over_quota_flips_appuser_read_only()
    {
        var docker = new DockerClientBuilder().Build();
        var prov = new DockerProvisioner(docker,
            new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker),
            NullLogger<DockerProvisioner>.Instance);

        var r = await prov.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        try
        {
            // Wait for postgres to accept connections
            var appCs = $"Host=localhost;Port={r.PublicPort};Database={r.DbName};" +
                        $"Username={r.DbUser};Password={r.DbPassword}";
            await WaitForPostgresAsync(appCs);

            // Build Instance with encrypted admin password
            var secrets = new SecretProtector(DataProtectionProvider.Create("t"));
            var inst = new Instance
            {
                InternalHost = r.InternalHost,
                PublicPort = r.PublicPort,
                DbName = r.DbName,
                DbUser = r.DbUser,
                DbPasswordEnc = secrets.Protect(r.DbPassword),
                AdminPasswordEnc = secrets.Protect(r.AdminPassword),
                StorageQuotaMb = 256
            };

            // Configure enforcer to use localhost:PublicPort (not internal host)
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["QueryExecutor:UseInternalHost"] = "false" }).Build();
            var enforcer = new QuotaEnforcer(secrets, cfg, NullLogger<QuotaEnforcer>.Instance);

            // Enforce: 999_999_999 bytes >> 256 MiB quota
            await enforcer.EnforceAsync(inst, 999_999_999, default);

            // Clear the Npgsql pool so the next open is a truly fresh TCP session;
            // pooled connections established before ALTER ROLE would not carry the new GUC.
            NpgsqlConnection.ClearAllPools();

            // Connect as appuser in a NEW session — must be read-only after enforcement
            await using var conn = new NpgsqlConnection(appCs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("CREATE TABLE quota_test(x int)", conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            ex.Message.Should().Contain("read-only",
                because: "appuser sessions must be read-only after quota enforcement");
        }
        finally
        {
            await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default);
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
