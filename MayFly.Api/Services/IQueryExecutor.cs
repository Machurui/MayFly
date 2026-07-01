using MayFly.Api.Domain;
using MayFly.Api.Dtos;

namespace MayFly.Api.Services;

public interface IQueryExecutor
{
    Task<QueryResultDto> ExecuteAsync(Instance inst, string sql, CancellationToken ct);
}
