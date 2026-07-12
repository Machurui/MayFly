using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;

namespace MayFly.Api.Mongo;

public sealed class MongoOps(IProvisionerClient prov, ISecretProtector secrets, ILogger<MongoOps> log) : IMongoOps
{
    private static readonly System.Text.RegularExpressions.Regex SafeIdentifier =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<QueryResultDto> RunConsoleAsync(Instance inst, string command, CancellationToken ct)
    {
        try
        {
            var pwd = secrets.Unprotect(inst.DbPasswordEnc);
            var r = await prov.ExecMongoshAsync(inst.ContainerId,
                new ExecMongoshRequest(command, inst.DbUser, pwd, inst.DbName, 10, 256 * 1024), ct);

            return new QueryResultDto(
                Success:   r.ExitCode == 0,
                Columns:   Array.Empty<string>(),
                Rows:      Array.Empty<object?[]>(),
                RowCount:  0,
                DurationMs: r.Ms,
                Message:   r.ExitCode == 0 ? "ok" : "error",
                Error:     r.ExitCode == 0 ? null : (string.IsNullOrEmpty(r.Error) ? r.Output : r.Error),
                Output:    r.Output,
                Truncated: r.Truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new QueryResultDto(false, Array.Empty<string>(), Array.Empty<object?[]>(),
                0, 0, "error", ex.Message);
        }
    }

    public async Task<long> GetSizeBytesAsync(Instance inst, CancellationToken ct)
    {
        if (!SafeIdentifier.IsMatch(inst.DbName))
            throw new InvalidOperationException($"DbName '{inst.DbName}' contains unsafe characters");

        var command = $"var s=db.getSiblingDB('{inst.DbName}').stats(); print(s.storageSize + s.indexSize);";
        var r = await ExecAdminAsync(inst, command, ct);

        var token = r.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (long.TryParse(token, out var n))
            return n;

        log.LogWarning("GetSizeBytesAsync: could not parse size from output '{Output}'", r.Output);
        return 0;
    }

    public async Task SoftEnforceReadOnlyAsync(Instance inst, CancellationToken ct)
    {
        if (!SafeIdentifier.IsMatch(inst.DbName))
            throw new InvalidOperationException($"DbName '{inst.DbName}' contains unsafe characters");
        if (!SafeIdentifier.IsMatch(inst.DbUser))
            throw new InvalidOperationException($"DbUser '{inst.DbUser}' contains unsafe characters");

        var command = $"db.getSiblingDB('{inst.DbName}').updateUser('{inst.DbUser}', {{roles:[{{role:'read', db:'{inst.DbName}'}}]}});";
        var r = await ExecAdminAsync(inst, command, ct);

        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"SoftEnforceReadOnlyAsync failed (exit {r.ExitCode}): {r.Error}");
    }

    private Task<ExecMongoshResult> ExecAdminAsync(Instance inst, string command, CancellationToken ct)
        => prov.ExecMongoshAsync(inst.ContainerId,
            new ExecMongoshRequest(command, inst.AdminUser, secrets.Unprotect(inst.AdminPasswordEnc), "admin", 10, 64 * 1024), ct);
}
