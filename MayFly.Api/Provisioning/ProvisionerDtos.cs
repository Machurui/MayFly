namespace MayFly.Api.Provisioning;

public record ProvisionResult(string ContainerId, string VolumeName, string InternalHost,
                              int PublicPort, string DbName, string DbUser, string DbPassword,
                              string AdminUser, string AdminPassword);
public record ProvisionInspect(string State, long SizeBytes);
public record ManagedContainer(string ContainerId, string InstanceId);
internal record CreateBody(string Engine, int TtlHours, int StorageMb, string InitialData);
