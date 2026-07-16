using MayFly.Api.Domain;
using MayFly.Api.Dtos;

namespace MayFly.Api.Import;

public interface IDumpImporter
{
    Task<ImportResultDto> ImportAsync(Instance inst, string dumpContent, CancellationToken ct);
}
