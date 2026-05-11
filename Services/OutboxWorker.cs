using System.Text.Json;
using ElasticDemo.Data;
using ElasticDemo.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
#pragma warning disable EF1002 // IDs là integer từ DB, không phải user input — không có SQL injection risk

namespace ElasticDemo.Services;

// Projection nhỏ gọn — SqlQueryRaw cần class với settable properties
file class OutboxEventRow
{
    public int     Id        { get; set; }
    public int     EntityId  { get; set; }
    public string  EventType { get; set; } = "";
    public string? Payload   { get; set; }
    public string  Status    { get; set; } = "";
}

public class OutboxWorker(
    IServiceScopeFactory scopeFactory,
    ElasticService elasticService,
    WorkerControl control,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1); // poll mỗi 1s

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("OutboxWorker started");

        while (!ct.IsCancellationRequested)
        {
            if (control.IsPaused)
            {
                await Task.Delay(_interval, ct);
                continue;
            }

            try
            {
                // Trả về số events đã xử lý — nếu còn pending thì chạy tiếp ngay, không delay
                int processed = await ProcessPendingEventsAsync();
                if (processed == 0)
                    await Task.Delay(_interval, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OutboxWorker batch failed, retrying in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task<int> ProcessPendingEventsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Raw ADO.NET + NOLOCK — không bị block, không cần type đăng ký EF Core
        var events = new List<OutboxEventRow>();
        await using (var conn = new SqlConnection(db.Database.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 10;
            cmd.CommandText = @"
                SELECT TOP 10000 Id, EntityId, EventType, Payload, Status
                FROM OutboxEvents WITH (NOLOCK)
                WHERE Status = 'Pending'
                ORDER BY Id";

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new OutboxEventRow
                {
                    Id        = reader.GetInt32(0),
                    EntityId  = reader.GetInt32(1),
                    EventType = reader.GetString(2),
                    Payload   = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Status    = reader.GetString(4),
                });
            }
        }

        if (events.Count == 0) return 0;

        logger.LogInformation("Processing {Count} outbox events", events.Count);

        // Tách riêng upsert và delete
        var upsertEvents = events
            .Where(e => e.EventType == OutboxEventType.Created || e.EventType == OutboxEventType.Updated)
            .ToList();

        var deleteEvents = events
            .Where(e => e.EventType == OutboxEventType.Deleted)
            .ToList();

        int success = 0;

        // Bulk upsert — gửi cả batch 1 lần thay vì từng doc
        if (upsertEvents.Count > 0)
        {
            try
            {
                var docs = upsertEvents
                    .Select(e => JsonSerializer.Deserialize<ProductSearchDoc>(e.Payload!))
                    .Where(d => d is not null)
                    .Cast<ProductSearchDoc>()
                    .ToList();

                await elasticService.BulkIndexAsync(docs);

                var ids = string.Join(',', upsertEvents.Select(e => e.Id));
                await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE OutboxEvents SET Status='Processed', ProcessedAt=GETDATE() WHERE Id IN ({ids})");
                success += upsertEvents.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk upsert failed");
                var ids = string.Join(',', upsertEvents.Select(e => e.Id));
                await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE OutboxEvents SET Status='Failed', ErrorMessage='{ex.Message.Replace("'", "''")}', ProcessedAt=GETDATE() WHERE Id IN ({ids})");
                control.RecordFailure(ex.Message);
            }
        }

        // Delete từng doc (thường ít hơn)
        var doneDeletes   = new List<int>();
        var failedDeletes = new List<(int Id, string Error)>();

        foreach (var evt in deleteEvents)
        {
            try
            {
                await elasticService.DeleteDocAsync(evt.EntityId);
                doneDeletes.Add(evt.Id);
                success++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delete doc {Id} failed", evt.EntityId);
                failedDeletes.Add((evt.Id, ex.Message));
                control.RecordFailure(ex.Message);
            }
        }

        if (doneDeletes.Count > 0)
        {
            var ids = string.Join(',', doneDeletes);
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE OutboxEvents SET Status='Processed', ProcessedAt=GETDATE() WHERE Id IN ({ids})");
        }

        if (failedDeletes.Count > 0)
        {
            var ids = string.Join(',', failedDeletes.Select(x => x.Id));
            var err = failedDeletes[0].Error.Replace("'", "''");
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE OutboxEvents SET Status='Failed', ErrorMessage='{err}', ProcessedAt=GETDATE() WHERE Id IN ({ids})");
        }

        if (success > 0)
            control.RecordSuccess(success);

        return events.Count;
    }
}
