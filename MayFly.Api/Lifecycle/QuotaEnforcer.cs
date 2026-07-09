using System.Text.RegularExpressions;
using MayFly.Api.Domain;
using MayFly.Api.Security;
using Npgsql;

namespace MayFly.Api.Lifecycle;

public sealed class QuotaEnforcer(ISecretProtector secrets, IConfiguration cfg, ILogger<QuotaEnforcer> log)
{
    private static readonly Regex SafeIdentifier = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private const string AdminUser = "mayflyadmin";

    public async Task EnforceAsync(Instance inst, long sizeBytes, CancellationToken ct)
    {
        if (sizeBytes < (long)inst.StorageQuotaMb * 1024 * 1024) return;

        if (!SafeIdentifier.IsMatch(inst.DbUser))
            throw new InvalidOperationException(
                $"DbUser '{inst.DbUser}' contains unsafe characters and cannot be used as an identifier.");

        var useInternal = cfg.GetValue("QueryExecutor:UseInternalHost", true);
        var host = useInternal ? inst.InternalHost : "localhost";
        var port = useInternal ? 5432 : inst.PublicPort;

        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = inst.DbName,
            Username = AdminUser,
            Password = secrets.Unprotect(inst.AdminPasswordEnc),
            Timeout = 5,
            CommandTimeout = 10
        }.ToString();

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"ALTER ROLE {inst.DbUser} SET default_transaction_read_only = on", conn);
        await cmd.ExecuteNonQueryAsync(ct);

        log.LogInformation(
            "Quota: flipped {DbUser} read-only on {Host}:{Port} (size={SizeBytes} >= quota={QuotaMb} MiB)",
            inst.DbUser, host, port, sizeBytes, inst.StorageQuotaMb);
    }
}
