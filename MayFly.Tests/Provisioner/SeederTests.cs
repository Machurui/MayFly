using Docker.DotNet;
using FluentAssertions;
using MayFly.Provisioner.Contracts;
using MayFly.Provisioner.Docker;
using MayFly.Provisioner.Seeding;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

[Collection("docker-sequential")]
[Trait("Category", "Docker")]
public class SeederTests
{
    [Fact]
    public async Task Northwind_seed_creates_products_table_with_rows()
    {
        var docker = new DockerClientBuilder().Build();
        var prov = new DockerProvisioner(docker, new PortAllocator(Array.Empty<int>()),
            new PlainVolumeProvisioner(docker), NullLogger<DockerProvisioner>.Instance);
        // Use "blank" so the init script does not include northwind; we seed explicitly below.
        var r = await prov.CreateAsync(new CreateInstanceRequest("postgres", 3, 256, "blank"), default);
        try
        {
            var seeder = new PostgresSeeder();
            // Seed as mayflyadmin after container is ready; ALTER DEFAULT PRIVILEGES set by
            // the init script ensures appuser has access to the newly-created tables.
            await seeder.SeedAsync("northwind", "localhost", r.PublicPort, r.DbName, r.AdminUser, r.AdminPassword, default);

            // Verify seeded data is visible to the unprivileged appuser.
            var cs = $"Host=localhost;Port={r.PublicPort};Database={r.DbName};Username={r.DbUser};Password={r.DbPassword}";
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM products", conn);
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().BeGreaterThan(0);
        }
        finally { await prov.DestroyAsync(r.ContainerId, r.VolumeName, r.PublicPort, default); }
    }

    [Fact]
    public async Task Blank_seed_is_noop()
    {
        var seeder = new PostgresSeeder();
        // host unreachable on purpose; blank must return without connecting
        await seeder.SeedAsync("blank", "localhost", 1, "x", "x", "x", default);  // must not throw
    }
}
