namespace MayFly.Api.Provisioning;

public record ProvisionResult(string ContainerId, string VolumeName, string InternalHost,
                              int PublicPort, string DbName, string DbUser, string DbPassword,
                              string AdminUser, string AdminPassword);
public record ProvisionInspect(string State, long SizeBytes);
public record ManagedContainer(string ContainerId, string InstanceId);
public record ExecMongoshRequest(string Command, string User, string Password, string AuthDb, int TimeoutSeconds, int MaxOutputBytes);
public record ExecMongoshResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
public record ExecDumpRequest(string Engine, string DumpContent, string AdminUser, string AdminPassword, string Db, int TimeoutSeconds, int MaxOutputBytes);
public record ExecDumpResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
internal record CreateBody(string Engine, int TtlHours, int StorageMb, string InitialData);
internal record SweepOrphansBody(IReadOnlyCollection<string> ActiveVolumeNames);
