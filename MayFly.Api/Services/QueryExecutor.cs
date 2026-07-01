using System.Diagnostics;
using MayFly.Api.Domain;
using MayFly.Api.Dtos;
using MayFly.Api.Security;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MayFly.Api.Services;

public sealed class QueryExecutor(ISecretProtector secrets, IConfiguration cfg) : IQueryExecutor
{
    private const int RowCap = 500;
    private const int TimeoutSeconds = 10;

    public async Task<QueryResultDto> ExecuteAsync(Instance inst, string sql, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var useInternal = cfg.GetValue("QueryExecutor:UseInternalHost", true);
        var host = useInternal ? inst.InternalHost : "localhost";
        var port = useInternal ? 5432 : inst.PublicPort;
        var cs = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = inst.DbName, Username = inst.DbUser,
            Password = secrets.Unprotect(inst.DbPasswordEnc),
            Timeout = 5, CommandTimeout = TimeoutSeconds
        }.ToString();

        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = TimeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!reader.HasRows && reader.FieldCount == 0)
            {
                var affected = reader.RecordsAffected;
                return new QueryResultDto(true, Array.Empty<string>(), Array.Empty<object?[]>(),
                    0, (int)sw.ElapsedMilliseconds, $"{Math.Max(affected, 0)} row(s) affected", null);
            }

            var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            while (rows.Count < RowCap && await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return new QueryResultDto(true, cols, rows, rows.Count, (int)sw.ElapsedMilliseconds,
                $"{rows.Count} row(s)", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new QueryResultDto(false, Array.Empty<string>(), Array.Empty<object?[]>(),
                0, (int)sw.ElapsedMilliseconds, "error", ex.Message);
        }
    }
}
