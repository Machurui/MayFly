using MayFly.Api.Domain;
using MayFly.Api.Dtos;

namespace MayFly.Api.Import;

public interface IDumpImporter
{
    Task<ImportResultDto> ImportAsync(Instance inst, byte[] dumpBytes, CancellationToken ct);
}
