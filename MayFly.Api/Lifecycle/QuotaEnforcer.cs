using System.Text.RegularExpressions;
using MayFly.Api.Domain;
using MayFly.Api.Engines;
using MayFly.Api.Security;

namespace MayFly.Api.Lifecycle;

public sealed class QuotaEnforcer(
    ISecretProtector secrets, IConfiguration cfg, ILogger<QuotaEnforcer> log,
    EngineClientRegistry registry)
{
    private static readonly Regex SafeIdentifier = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    public async Task EnforceAsync(Instance inst, long sizeBytes, CancellationToken ct)
    {
        if (sizeBytes < (long)inst.StorageQuotaMb * 1024 * 1024) return;

        if (!SafeIdentifier.IsMatch(inst.DbUser))
            throw new InvalidOperationException(
                $"DbUser '{inst.DbUser}' contains unsafe characters and cannot be used as an identifier.");

        var useInternal = cfg.GetValue("QueryExecutor:UseInternalHost", true);
        var client = registry.For(inst.Engine);
        var host = useInternal ? inst.InternalHost : "localhost";
        var port = useInternal ? client.Port : inst.PublicPort;

        var cs = client.BuildAdoConnectionString(host, port, inst.DbName, inst.AdminUser,
            secrets.Unprotect(inst.AdminPasswordEnc));

        await using var conn = client.CreateConnection(cs);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = client.SoftEnforceReadOnlySql(inst.DbUser, inst.DbName);
        await cmd.ExecuteNonQueryAsync(ct);

        log.LogInformation(
            "Quota: flipped {DbUser} read-only on {Host}:{Port} (size={SizeBytes} >= quota={QuotaMb} MiB)",
            inst.DbUser, host, port, sizeBytes, inst.StorageQuotaMb);
    }
}
