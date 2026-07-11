using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;

namespace MayFly.Api.Mongo;

public sealed class MongoOps(IProvisionerClient prov, ISecretProtector secrets, ILogger<MongoOps> log) : IMongoOps
{
    public async Task<QueryResultDto> RunConsoleAsync(Instance inst, string command, CancellationToken ct)
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

    public async Task<long> GetSizeBytesAsync(Instance inst, CancellationToken ct)
    {
        var adminPwd = secrets.Unprotect(inst.AdminPasswordEnc);
        var command = $"var s=db.getSiblingDB('{inst.DbName}').stats(); print(s.storageSize + s.indexSize);";
        var r = await prov.ExecMongoshAsync(inst.ContainerId,
            new ExecMongoshRequest(command, inst.AdminUser, adminPwd, "admin", 10, 64 * 1024), ct);

        if (long.TryParse(r.Output.Trim(), out var n))
            return n;

        log.LogWarning("GetSizeBytesAsync: could not parse size from output '{Output}'", r.Output);
        return 0;
    }

    public async Task SoftEnforceReadOnlyAsync(Instance inst, CancellationToken ct)
    {
        var adminPwd = secrets.Unprotect(inst.AdminPasswordEnc);
        var command = $"db.getSiblingDB('{inst.DbName}').updateUser('{inst.DbUser}', {{roles:[{{role:'read', db:'{inst.DbName}'}}]}});";
        var r = await prov.ExecMongoshAsync(inst.ContainerId,
            new ExecMongoshRequest(command, inst.AdminUser, adminPwd, "admin", 10, 64 * 1024), ct);

        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                $"SoftEnforceReadOnlyAsync failed (exit {r.ExitCode}): {r.Error}");
    }
}
