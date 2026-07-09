namespace MayFly.Provisioner.Contracts;

public record CreateInstanceRequest(string Engine, int TtlHours, int StorageMb, string InitialData);
public record CreateInstanceResult(string ContainerId, string VolumeName, string InternalHost,
                                   int PublicPort, string DbName, string DbUser, string DbPassword,
                                   string AdminUser, string AdminPassword);
public record InspectResult(string State, long SizeBytes);
public record ManagedContainerInfo(string ContainerId, string InstanceId);
public record SweepOrphansRequest(IReadOnlyCollection<string> ActiveVolumeNames);
