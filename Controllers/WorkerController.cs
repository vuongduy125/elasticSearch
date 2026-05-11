using ElasticDemo.Data;
using ElasticDemo.Models;
using ElasticDemo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ElasticDemo.Controllers;

public class WorkerController(
    WorkerControl control,
    AppDbContext db,
    ElasticService elasticService,
    ProductService productService,
    SeedProgressService seedProgress,
    IServiceScopeFactory scopeFactory,
    ILogger<WorkerController> logger) : Controller
{
    // GET /Worker
    public IActionResult Index() => View();

    // POST /Worker/SeedSqlServer
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedSqlServer(int count = 200)
    {
        await elasticService.EnsureIndexAsync();

        if (count <= 10_000)
        {
            var total = await productService.SeedAsync(count, batchSize: 4000);
            TempData["Success"] = $"Đã seed thêm {total} sản phẩm vào SQLite. Worker sẽ sync lên ES trong vài giây...";
        }
        else
        {
            _ = Task.Run(async () =>
            {
                logger.LogInformation("Background seed bắt đầu: {Count} records", count);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ProductService>();
                    await svc.SeedAsync(count, batchSize: 4000);
                    logger.LogInformation("Background seed hoàn thành: {Count} records", count);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background seed thất bại sau khi chạy được một phần");
                }
            });
            TempData["Success"] = $"Đang seed thêm {count:N0} bản ghi ngầm — worker sync song song, theo dõi qua Dashboard.";
        }

        return RedirectToAction(nameof(Index));
    }

    // POST /Worker/ClearAll
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAll()
    {
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE OutboxEvents");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE Products");

        await elasticService.DeleteIndexAsync();
        await elasticService.EnsureIndexAsync();

        TempData["Success"] = "Đã xóa toàn bộ dữ liệu SQL + ES.";
        return RedirectToAction(nameof(Index));
    }

    // POST /Worker/ForceResync — xóa ES index, tạo lại, bulk index toàn bộ từ SQL
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ForceResync()
    {
        if (seedProgress.IsRunning)
            return Json(new { started = false, message = "Đang có tác vụ khác chạy." });

        var total = await db.Products.CountAsync();

        _ = Task.Run(async () =>
        {
            logger.LogInformation("ForceResync bắt đầu: {Total} records", total);
            seedProgress.Start(total);
            try
            {
                await elasticService.DeleteIndexAsync();
                await elasticService.EnsureIndexAsync();

                int done = 0, lastId = 0;
                const int batchSize = 5000;

                using var scope = scopeFactory.CreateScope();
                var batchDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                while (true)
                {
                    var batch = await batchDb.Products
                        .AsNoTracking()
                        .Where(p => p.Id > lastId)
                        .OrderBy(p => p.Id)
                        .Take(batchSize)
                        .ToListAsync();

                    if (batch.Count == 0) break;

                    await elasticService.BulkIndexAsync(batch.Select(p => p.ToSearchDoc()));
                    done  += batch.Count;
                    lastId = batch[^1].Id;
                    seedProgress.Report(done);
                }

                seedProgress.Complete();
                logger.LogInformation("ForceResync hoàn thành: {Done} records", done);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ForceResync thất bại");
                seedProgress.Fail(ex.Message);
            }
        });

        return Json(new { started = true, total });
    }

    // POST /Worker/RetryFailed — sync trực tiếp các event Failed lên ES (background)
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryFailed()
    {
        var total = await db.OutboxEvents.CountAsync(e => e.Status == OutboxStatus.Failed);
        if (total == 0)
            return Json(new { count = 0 });

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var batchDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var failedEvents = await batchDb.OutboxEvents
                .Where(e => e.Status == OutboxStatus.Failed)
                .ToListAsync();

            var upserts = failedEvents
                .Where(e => e.EventType == OutboxEventType.Created || e.EventType == OutboxEventType.Updated)
                .ToList();

            var deletes = failedEvents
                .Where(e => e.EventType == OutboxEventType.Deleted)
                .ToList();

            if (upserts.Count > 0)
            {
                try
                {
                    var docs = upserts
                        .Select(e => System.Text.Json.JsonSerializer.Deserialize<ProductSearchDoc>(e.Payload!))
                        .Where(d => d is not null)
                        .Cast<ProductSearchDoc>()
                        .ToList();

                    await elasticService.BulkIndexAsync(docs);

                    var ids = string.Join(',', upserts.Select(e => e.Id));
                    await batchDb.Database.ExecuteSqlRawAsync(
                        $"UPDATE OutboxEvents SET Status='Processed', ErrorMessage=NULL, ProcessedAt=GETDATE() WHERE Id IN ({ids})");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "RetryFailed upsert failed");
                }
            }

            foreach (var evt in deletes)
            {
                try
                {
                    await elasticService.DeleteDocAsync(evt.EntityId);
                    await batchDb.Database.ExecuteSqlRawAsync(
                        $"UPDATE OutboxEvents SET Status='Processed', ErrorMessage=NULL, ProcessedAt=GETDATE() WHERE Id = {evt.Id}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "RetryFailed delete {Id} failed", evt.EntityId);
                }
            }
        });

        return Json(new { count = total });
    }

    // POST /Worker/Pause
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Pause()
    {
        control.Pause();
        return RedirectToAction(nameof(Index));
    }

    // POST /Worker/Resume
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Resume()
    {
        control.Resume();
        return RedirectToAction(nameof(Index));
    }

    // GET /Worker/SeedProgress — poll tiến độ seed
    public IActionResult SeedProgress()
    {
        var elapsed = seedProgress.StartedAt is null ? 0
            : (int)(seedProgress.FinishedAt ?? DateTime.Now)
                .Subtract(seedProgress.StartedAt.Value).TotalSeconds;

        return Json(new
        {
            isRunning      = seedProgress.IsRunning,
            indexed        = seedProgress.IndexedCount,
            total          = seedProgress.TotalCount,
            percent        = seedProgress.ProgressPercent,
            elapsedSeconds = elapsed,
        });
    }

    // GET /Worker/Stats — poll từ UI mỗi 3s
    public async Task<IActionResult> Stats()
    {
        int  sqlCount = control.CachedSqlCount;
        int  pendingCount = control.CachedPendingCount;
        int  failedCount  = control.CachedFailedCount;
        int  doneCount    = control.CachedDoneCount;
        long esCount      = control.CachedEsCount;
        var  recentEvents = control.CachedRecentEvents;
        bool fromCache    = true;

        try
        {
            await using var conn = new SqlConnection(db.Database.GetConnectionString());
            conn.StatisticsEnabled = false;
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 3; // fail fast thay vì pending 30s
            cmd.CommandText = @"
                SELECT COUNT(*) FROM Products WITH (NOLOCK);

                SELECT Status, COUNT(*) AS Cnt
                FROM OutboxEvents WITH (NOLOCK)
                GROUP BY Status;

                SELECT TOP 20 Id, EntityId, EventType, Status, ErrorMessage, CreatedAt, ProcessedAt
                FROM OutboxEvents WITH (NOLOCK)
                ORDER BY Id DESC;";

            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync()) sqlCount = reader.GetInt32(0);

            await reader.NextResultAsync();
            pendingCount = failedCount = doneCount = 0;
            while (await reader.ReadAsync())
            {
                var status = reader.GetString(0);
                var cnt    = reader.GetInt32(1);
                if (status == OutboxStatus.Pending)        pendingCount = cnt;
                else if (status == OutboxStatus.Failed)    failedCount  = cnt;
                else if (status == OutboxStatus.Processed) doneCount    = cnt;
            }

            var events = new List<object>();
            await reader.NextResultAsync();
            while (await reader.ReadAsync())
            {
                events.Add(new
                {
                    Id           = reader.GetInt32(0),
                    EntityId     = reader.GetInt32(1),
                    EventType    = reader.GetString(2),
                    Status       = reader.GetString(3),
                    ErrorMessage = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt    = reader.GetDateTime(5).ToString("HH:mm:ss"),
                    ProcessedAt  = reader.IsDBNull(6) ? null : reader.GetDateTime(6).ToString("HH:mm:ss"),
                });
            }
            recentEvents = events;

            try { esCount = await elasticService.CountAsync(); } catch { }

            // cập nhật cache
            control.CachedSqlCount     = sqlCount;
            control.CachedPendingCount = pendingCount;
            control.CachedFailedCount  = failedCount;
            control.CachedDoneCount    = doneCount;
            control.CachedEsCount      = esCount;
            control.CachedRecentEvents = recentEvents;
            fromCache = false;
        }
        catch { /* SQL timeout — trả về cache bên dưới */ }

        return Json(new
        {
            workerRunning  = control.IsRunning,
            processedTotal = control.ProcessedTotal,
            failedTotal    = control.FailedTotal,
            lastRunAt      = control.LastRunAt?.ToString("HH:mm:ss"),
            lastError      = control.LastError,
            sqlCount,
            esCount,
            diff           = sqlCount - (int)esCount,
            pendingCount,
            failedCount,
            doneCount,
            recentEvents,
            fromCache,
        });
    }
}
