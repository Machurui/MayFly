using MayFly.Api.Domain;

namespace MayFly.Api.Dtos;

public record InstanceDto(string Token, string Engine, string State, int TtlHours, int StorageQuotaMb,
    long LastSizeBytes, string InitialData, DateTime CreatedAt, DateTime ExpiresAt,
    string ConnectionString, int PublicPort, string DbName, string DbUser)
{
    public static InstanceDto From(Instance i, string publicHost, string plainPassword) => new(
        i.CapabilityToken, i.Engine, i.State.ToString(), i.TtlHours, i.StorageQuotaMb, i.LastSizeBytes,
        i.InitialData, i.CreatedAt, i.ExpiresAt,
        $"postgresql://{i.DbUser}:{plainPassword}@{publicHost}:{i.PublicPort}/{i.DbName}",
        i.PublicPort, i.DbName, i.DbUser);
}
