using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;

namespace MayFly.Api.Mongo;

public sealed class MongoOps(IProvisionerClient prov, ISecretProtector secrets) : IMongoOps
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
}
