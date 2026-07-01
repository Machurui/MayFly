using System.Reflection;
using Npgsql;

namespace MayFly.Provisioner.Seeding;

public sealed class PostgresSeeder : IInitialDataSeeder
{
    public async Task SeedAsync(string initialData, string host, int port, string db, string user,
        string password, CancellationToken ct)
    {
        if (initialData == "blank") return;
        var sql = initialData switch
        {
            "northwind" => ReadEmbedded("northwind.sql"),
            _ => throw new ArgumentException($"unknown initialData '{initialData}'")
        };
        var cs = $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5";
        await WaitReadyAsync(cs, ct);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WaitReadyAsync(string cs, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            try { await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return; }
            catch (Exception ex) when (ex is not OperationCanceledException) { await Task.Delay(1000, ct); }
        }
        throw new TimeoutException("postgres not ready for seeding");
    }

    private static string ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().Single(n => n.EndsWith(name));
        using var s = asm.GetManifestResourceStream(res)
            ?? throw new InvalidOperationException($"Embedded resource '{res}' stream could not be opened.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
