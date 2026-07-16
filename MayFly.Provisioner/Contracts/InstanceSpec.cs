namespace MayFly.Provisioner.Contracts;

public record CreateInstanceRequest(string Engine, int TtlHours, int StorageMb, string InitialData);
public record CreateInstanceResult(string ContainerId, string VolumeName, string InternalHost,
                                   int PublicPort, string DbName, string DbUser, string DbPassword,
                                   string AdminUser, string AdminPassword);
public record InspectResult(string State, long SizeBytes);
public record ManagedContainerInfo(string ContainerId, string InstanceId);
public record SweepOrphansRequest(IReadOnlyCollection<string> ActiveVolumeNames);
public record ExecMongoshRequest(string Command, string User, string Password, string AuthDb, int TimeoutSeconds, int MaxOutputBytes);
public record ExecMongoshResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
public record ExecDumpRequest(string Engine, string DumpContent, string AdminUser, string AdminPassword, string AppUser, string Db, int TimeoutSeconds, int MaxOutputBytes);
public record ExecDumpResult(string Output, string Error, int ExitCode, bool Truncated, int Ms);
