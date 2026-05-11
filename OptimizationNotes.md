# Optimization Notes

## 1. SeedAsync — fix OOM crash

**Vấn đề:** EF Core ChangeTracker tích lũy toàn bộ entities qua các batch → crash ở 615k-950k records.

**Fix:** Tạo DbContext mới mỗi batch thay vì dùng chung 1 context suốt vòng loop. `scope.Dispose()` sau mỗi batch giải phóng hoàn toàn tracked entities.

```csharp
// Trước — 1 DbContext suốt vòng loop, ChangeTracker tích lũy
db.ChangeTracker.AutoDetectChangesEnabled = false;
while (total < count) {
    await db.Products.AddRangeAsync(batch);
    await db.SaveChangesAsync();
    // ChangeTracker vẫn giữ toàn bộ entities đã save
}

// Sau — DbContext mới mỗi batch, dispose hoàn toàn
while (total < count) {
    using var scope = scopeFactory.CreateScope();
    var batchDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // ... insert batch ...
    // scope.Dispose() → DbContext.Dispose() → toàn bộ tracked entities giải phóng
}
```

---

## 2. OutboxWorker — tăng throughput sync SQL→ES

| | Trước | Sau |
|---|---|---|
| Delay giữa batch | Luôn 1s | Chỉ delay khi hết pending |
| Batch size | 1000 | 10000 |
| SELECT | EF Core có tracking | Raw ADO.NET + `WITH (NOLOCK)` |
| ORDER BY | `CreatedAt` (sort toàn bảng) | `Id` (clustered index) |
| UPDATE status | N câu riêng lẻ qua EF | 1 câu `WHERE Id IN (1,2,3,...)` |

**Tại sao bỏ EF Core Contains cho UPDATE:**
EF Core dịch `Contains(list)` thành `WHERE Id IN (@p0, @p1, ...)` — SQL Server giới hạn 2100 parameters/query. Dùng integer literal trong raw SQL không có giới hạn này.

```csharp
// Trước — EF Core Contains, giới hạn 2100 params, parse chậm
await db.OutboxEvents
    .Where(e => ids.Contains(e.Id))
    .ExecuteUpdateAsync(...);

// Sau — raw SQL, integer literal, không giới hạn, parse nhanh
var ids = string.Join(',', events.Select(e => e.Id));
await db.Database.ExecuteSqlRawAsync(
    $"UPDATE OutboxEvents SET Status='Processed', ProcessedAt=GETDATE() WHERE Id IN ({ids})");
```

**Tại sao dùng NOLOCK cho SELECT:**
Worker SELECT trên `OutboxEvents` bị block bởi seeding đang giữ lock insert trên cùng bảng. `WITH (NOLOCK)` cho phép dirty read — chấp nhận được vì worst case chỉ là process lại 1 event đã xử lý.

---

## 3. Stats endpoint — tránh block UI

**Vấn đề:** `COUNT(*)` bị block bởi lock từ seeding transaction, timeout 30s → UI pending.

**Fix:**
- Raw ADO.NET + `WITH (NOLOCK)` thay EF Core
- `CommandTimeout = 3` — fail fast thay vì pending 30s
- Cache giá trị cuối trong `WorkerControl` — trả về ngay khi SQL timeout, UI không bao giờ bị pending

```csharp
cmd.CommandTimeout = 3;
cmd.CommandText = @"
    SELECT COUNT(*) FROM Products WITH (NOLOCK);
    SELECT Status, COUNT(*) AS Cnt FROM OutboxEvents WITH (NOLOCK) GROUP BY Status;
    SELECT TOP 20 ... FROM OutboxEvents WITH (NOLOCK) ORDER BY Id DESC;";
```

---

## 4. Composite Index (Status, Id)

**Vấn đề:** Index đơn `(Status)` trên OutboxEvents không đủ cho query `WHERE Status='Pending' ORDER BY Id` — SQL Server phải scan toàn bộ Pending rows rồi sort lại.

**Fix:** Composite index `(Status, Id)` — SQL Server đọc trực tiếp theo thứ tự Id trong index, không cần sort.

```sql
CREATE INDEX IX_OutboxEvents_Status_Id ON OutboxEvents(Status, Id)
```

Tạo tự động khi app khởi động (idempotent):
```csharp
db.Database.ExecuteSqlRaw(@"
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxEvents_Status_Id'
                  AND object_id = OBJECT_ID('OutboxEvents'))
    CREATE INDEX IX_OutboxEvents_Status_Id ON OutboxEvents(Status, Id)");
```

---

## 5. Pause OutboxWorker khi seed lớn

**Vấn đề:** Seeding và OutboxWorker cùng insert/update bảng `OutboxEvents` → lock contention → cả 2 chậm, SQL Server overload.

**Fix:** Tự động pause worker khi seed >10k records, resume sau khi xong.

```csharp
control.Pause();
try {
    await svc.SeedAsync(count, batchSize: 1000);
} finally {
    if (!wasPaused) control.Resume();
}
```

---

## 6. OutboxWorker — không crash app khi exception

**Vấn đề:** ASP.NET Core mặc định `BackgroundServiceExceptionBehavior = StopHost` — exception không catch trong BackgroundService sẽ kill toàn bộ app.

**Fix:** Bắt exception trong `ExecuteAsync`, log lỗi và retry sau 5s.

```csharp
try {
    int processed = await ProcessPendingEventsAsync();
    if (processed == 0) await Task.Delay(_interval, ct);
}
catch (Exception ex) when (ex is not OperationCanceledException) {
    logger.LogError(ex, "OutboxWorker batch failed, retrying in 5s");
    await Task.Delay(TimeSpan.FromSeconds(5), ct);
}
```
