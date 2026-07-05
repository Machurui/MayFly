namespace MayFly.Api.Domain;

public enum InstanceState { Provisioning, Running, Destroying, Expired, Destroyed, Failed }

public class Instance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CapabilityToken { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string CreatorIp { get; set; } = "";
    public string Engine { get; set; } = "postgres";
    public int TtlHours { get; set; }
    public int StorageQuotaMb { get; set; }
    public string InitialData { get; set; } = "blank";
    public string ContainerId { get; set; } = "";
    public string VolumeName { get; set; } = "";
    public string InternalHost { get; set; } = "";
    public int PublicPort { get; set; }
    public string DbName { get; set; } = "";
    public string DbUser { get; set; } = "";
    public string DbPasswordEnc { get; set; } = "";
    public string AdminPasswordEnc { get; set; } = "";
    public InstanceState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public long LastSizeBytes { get; set; }
}
