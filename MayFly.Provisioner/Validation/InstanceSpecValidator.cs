using MayFly.Provisioner.Contracts;

namespace MayFly.Provisioner.Validation;

public static class InstanceSpecValidator
{
    private static readonly HashSet<string> Engines = new() { "postgres", "mysql", "mariadb", "mssql", "mongo" };
    private static readonly HashSet<int> Ttls = new() { 3, 6, 12 };
    private static readonly HashSet<int> Storage = new() { 256, 512, 1024, 2048 };
    private static readonly HashSet<string> InitialData = new() { "blank", "northwind" };

    public static (bool Ok, string? Error) Validate(CreateInstanceRequest r)
    {
        if (!Engines.Contains(r.Engine)) return (false, $"engine '{r.Engine}' not allowed");
        if (!Ttls.Contains(r.TtlHours)) return (false, $"ttl '{r.TtlHours}' not allowed");
        if (!Storage.Contains(r.StorageMb)) return (false, $"storage '{r.StorageMb}' not allowed");
        if (!InitialData.Contains(r.InitialData)) return (false, $"initialData '{r.InitialData}' not allowed");
        return (true, null);
    }
}
