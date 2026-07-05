using FluentAssertions;
using MayFly.Provisioner.Docker;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Docker")]
[Collection("docker-sequential")]
public class RoleInitializerTests : IAsyncLifetime
{
    private const string AdminUser = "mayflyadmin";
    private const string AdminPassword = "adminpw";
    private const string AppUser = "appuser";
    private const string AppPassword = "appuserpw";
    private const string DbName = "appdb";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16-alpine")
        .WithUsername(AdminUser)
        .WithPassword(AdminPassword)
        .WithDatabase(DbName)
        .Build();

    public Task InitializeAsync() => _db.StartAsync();
    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Appuser_is_not_superuser_and_cannot_copy_program()
    {
        var host = _db.Hostname;
        var port = _db.GetMappedPublicPort(5432);

        var init = new RoleInitializer();
        await init.InitAsync(host, port, DbName, AdminUser, AdminPassword, AppUser, AppPassword, default);

        // --- appuser assertions ---
        var appCs = $"Host={host};Port={port};Database={DbName};Username={AppUser};Password={AppPassword}";
        await using var appConn = new NpgsqlConnection(appCs);
        await appConn.OpenAsync();

        // appuser must NOT be a superuser
        await using var rolCheck = new NpgsqlCommand(
            "SELECT rolsuper FROM pg_roles WHERE rolname='appuser'", appConn);
        var rolsuper = (bool)(await rolCheck.ExecuteScalarAsync())!;
        rolsuper.Should().BeFalse("appuser must be NOSUPERUSER");

        // COPY PROGRAM must be refused for appuser (superuser-only operation)
        await using var copyCmd = new NpgsqlCommand("COPY (SELECT 1) TO PROGRAM 'id'", appConn);
        Func<Task> appCopy = () => copyCmd.ExecuteNonQueryAsync();
        await appCopy.Should().ThrowAsync<PostgresException>(
            "appuser must not be allowed to run COPY … TO PROGRAM");

        // --- mayflyadmin assertions ---
        var adminCs = $"Host={host};Port={port};Database={DbName};Username={AdminUser};Password={AdminPassword}";
        await using var adminConn = new NpgsqlConnection(adminCs);
        await adminConn.OpenAsync();

        // mayflyadmin must remain a superuser
        await using var adminRolCheck = new NpgsqlCommand(
            "SELECT rolsuper FROM pg_roles WHERE rolname='mayflyadmin'", adminConn);
        var adminSuperuser = (bool)(await adminRolCheck.ExecuteScalarAsync())!;
        adminSuperuser.Should().BeTrue("mayflyadmin must remain a superuser");

        // COPY PROGRAM must succeed for admin (proves the privilege is the gate, not the OS program)
        await using var adminCopyCmd = new NpgsqlCommand(
            "COPY (SELECT 1) TO PROGRAM '/bin/true'", adminConn);
        Func<Task> adminCopy = () => adminCopyCmd.ExecuteNonQueryAsync();
        await adminCopy.Should().NotThrowAsync("mayflyadmin must be allowed to run COPY … TO PROGRAM");
    }
}
