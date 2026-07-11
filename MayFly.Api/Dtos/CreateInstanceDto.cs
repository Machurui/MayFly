namespace MayFly.Api.Dtos;

public record CreateInstanceDto(string Engine, int TtlHours, int StorageMb, string InitialData);
public record QueryRequestDto(string Query);
