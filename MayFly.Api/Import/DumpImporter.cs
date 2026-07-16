using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Provisioning;
using MayFly.Api.Security;

namespace MayFly.Api.Import;

public sealed class DumpImporter(IProvisionerClient prov, ISecretProtector secrets) : IDumpImporter
{
    public async Task<ImportResultDto> ImportAsync(Instance inst, string dumpContent, CancellationToken ct)
    {
        try
        {
            var pwd = secrets.Unprotect(inst.AdminPasswordEnc);
            var r = await prov.ExecDumpAsync(inst.ContainerId,
                new ExecDumpRequest(inst.Engine, dumpContent, inst.AdminUser, pwd, inst.DbName, 60, 256 * 1024), ct);

            return new ImportResultDto(
                Success:   r.ExitCode == 0,
                Output:    r.Output,
                Error:     r.ExitCode == 0 ? null : (string.IsNullOrEmpty(r.Error) ? r.Output : r.Error),
                Truncated: r.Truncated,
                Ms:        r.Ms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ImportResultDto(false, string.Empty, ex.Message, false, 0);
        }
    }
}
