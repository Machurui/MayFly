using MayFly.Api.Dtos;

namespace MayFly.Api.Validation;

public static class ApiSpecValidator
{
    private static readonly HashSet<string> Engines = new() { "postgres", "mysql", "mariadb", "mssql", "mongo" };
    private static readonly HashSet<int> Ttls = new() { 3, 6, 12 };
    private static readonly HashSet<int> Storage = new() { 256, 512, 1024, 2048 };
    private static readonly HashSet<string> Init = new() { "blank", "northwind", "ecommerce", "blog", "iot" };

    public static (bool Ok, string? Error) Validate(CreateInstanceDto d)
    {
        if (!Engines.Contains(d.Engine)) return (false, "engine not supported");
        if (!Ttls.Contains(d.TtlHours)) return (false, "ttl not allowed");
        if (!Storage.Contains(d.StorageMb)) return (false, "storage not allowed");
        if (!Init.Contains(d.InitialData)) return (false, "initialData not allowed");
        return (true, null);
    }
}
