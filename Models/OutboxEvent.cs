namespace ElasticDemo.Models;

public class OutboxEvent
{
    public int Id { get; set; }
    public int EntityId { get; set; }          // Product.Id
    public string EventType { get; set; } = string.Empty; // ProductCreated | ProductUpdated | ProductDeleted
    public string? Payload { get; set; }       // JSON snapshot của product
    public string Status { get; set; } = OutboxStatus.Pending;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ProcessedAt { get; set; }
}

public static class OutboxStatus
{
    public const string Pending   = "Pending";
    public const string Processed = "Processed";
    public const string Failed    = "Failed";
}

public static class OutboxEventType
{
    public const string Created = "ProductCreated";
    public const string Updated = "ProductUpdated";
    public const string Deleted = "ProductDeleted";
}
