namespace MayFly.Api.Dtos;

public record QueryResultDto(bool Success, IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows, int RowCount, int DurationMs, string Message, string? Error,
    string? Output = null, bool Truncated = false);
