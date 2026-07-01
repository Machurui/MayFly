namespace MayFly.Api.Domain;

public class QueryLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public DateTime ExecutedAt { get; set; }
    public int DurationMs { get; set; }
    public int RowCount { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
