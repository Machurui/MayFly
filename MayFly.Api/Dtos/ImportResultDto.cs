namespace MayFly.Api.Dtos;

public record ImportResultDto(bool Success, string Output, string? Error, bool Truncated, int Ms);
