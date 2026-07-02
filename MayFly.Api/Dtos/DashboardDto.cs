namespace MayFly.Api.Dtos;

public record DashboardSummary(int AliveCount, int MaxAlive, int QueriesToday,
    long StorageUsedBytes, DateTime? NextExpiry);
