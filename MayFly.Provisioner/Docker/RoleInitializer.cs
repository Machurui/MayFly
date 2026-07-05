using System.Text.RegularExpressions;
using Npgsql;

namespace MayFly.Provisioner.Docker;

public sealed class RoleInitializer
{
    // Only lowercase letters, digits and underscores, starting with a letter or underscore.
    // These identifiers are server-controlled constants, but validate defensively.
    private static readonly Regex SafeIdentifier =
        new(@"^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    public async Task InitAsync(
        string host, int port, string db,
        string adminUser, string adminPassword,
        string appUser, string appPassword,
        CancellationToken ct)
    {
        var cs = $"Host={host};Port={port};Database={db};" +
                 $"Username={adminUser};Password={adminPassword};Timeout=5";
        await WaitReadyAsync(cs, ct);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);

        var sql = $@"
            CREATE ROLE {Quote(appUser)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
                PASSWORD {Literal(appPassword)};
            GRANT CONNECT ON DATABASE {Quote(db)} TO {Quote(appUser)};
            GRANT ALL ON SCHEMA public TO {Quote(appUser)};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                GRANT ALL ON TABLES TO {Quote(appUser)};
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                GRANT ALL ON SEQUENCES TO {Quote(appUser)};
            CREATE EXTENSION IF NOT EXISTS pg_trgm;
            CREATE EXTENSION IF NOT EXISTS ""uuid-ossp"";";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WaitReadyAsync(string cs, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            try
            {
                await using var c = new NpgsqlConnection(cs);
                await c.OpenAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
        throw new TimeoutException("postgres not ready for role initialisation");
    }

    /// <summary>Wraps an identifier in double-quotes after validating its charset.</summary>
    private static string Quote(string identifier)
    {
        if (!SafeIdentifier.IsMatch(identifier))
            throw new ArgumentException($"Unsafe identifier: '{identifier}'");
        return $"\"{identifier}\"";
    }

    /// <summary>
    /// Produces a PostgreSQL single-quoted string literal for a password value,
    /// escaping any embedded single-quotes by doubling them.
    /// </summary>
    private static string Literal(string value) =>
        $"'{value.Replace("'", "''")}'";
}
