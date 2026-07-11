using MayFly.Api.Domain;
using MayFly.Api.Dtos;

namespace MayFly.Api.Mongo;

public interface IMongoOps
{
    Task<QueryResultDto> RunConsoleAsync(Instance inst, string command, CancellationToken ct);
}
