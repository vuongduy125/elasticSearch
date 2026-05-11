using Bogus;
using System.Text.Json;
using ElasticDemo.Data;
using ElasticDemo.Models;

namespace ElasticDemo.Services;

public class ProductService(AppDbContext db, IServiceScopeFactory scopeFactory, SeedProgressService seedProgress)
{
    private static readonly string[] Brands     = ["Apple","Samsung","Xiaomi","Sony","Dell","Asus","Lenovo","Oppo","Vivo","Realme"];
    private static readonly string[] Categories = ["Điện thoại","Laptop","Tablet","Tai nghe","Đồng hồ","Máy ảnh","Phụ kiện"];
    private static readonly string[] Cities     = ["Hà Nội","TP.HCM","Đà Nẵng","Seoul","Tokyo","Thượng Hải"];

    private static readonly Dictionary<string, string> BrandCountries = new()
    {
        ["Apple"]="Mỹ",["Samsung"]="Hàn Quốc",["Xiaomi"]="Trung Quốc",
        ["Sony"]="Nhật Bản",["Dell"]="Mỹ",["Asus"]="Đài Loan",
        ["Lenovo"]="Trung Quốc",["Oppo"]="Trung Quốc",["Vivo"]="Trung Quốc",["Realme"]="Trung Quốc",
    };

    // ── SEED SQLite + OutboxEvents ────────────────────────────────────────────
    public async Task<int> SeedAsync(int count = 200, int batchSize = 100)
    {

        var faker = new Faker<Product>("vi")
            .RuleFor(p => p.ProductName, f =>
            {
                var brand    = f.PickRandom(Brands);
                var category = f.PickRandom(Categories);
                return $"{brand} {category} {f.Commerce.ProductAdjective()} {f.Random.AlphaNumeric(4).ToUpper()}";
            })
            .RuleFor(p => p.Price,        f => Math.Round(f.Random.Decimal(99_000, 50_000_000), 0))
            .RuleFor(p => p.Stock,        f => f.Random.Int(0, 999))
            .RuleFor(p => p.CategoryName, f => f.PickRandom(Categories))
            .RuleFor(p => p.BrandName,    f => f.PickRandom(Brands))
            .RuleFor(p => p.BrandCountry, (f, p) => BrandCountries.GetValueOrDefault(p.BrandName, "Khác"))
            .RuleFor(p => p.SupplierName, f => $"Công ty {f.Company.CompanyName()}")
            .RuleFor(p => p.SupplierCity, f => f.PickRandom(Cities))
            .RuleFor(p => p.CreatedAt,    f => f.Date.Recent(30))
            .RuleFor(p => p.UpdatedAt,    (f, p) => p.CreatedAt);

        var logPath = Path.Combine(AppContext.BaseDirectory, "seed.log");
        await File.AppendAllTextAsync(logPath, $"{DateTime.Now:HH:mm:ss} | SEED START count={count} batchSize={batchSize}\n");

        seedProgress.Start(count);
        int total = 0;
        while (total < count)
        {
            var batch = faker.Generate(Math.Min(batchSize, count - total));

            // Tạo DbContext mới mỗi batch — dispose hoàn toàn sau mỗi vòng, không tích lũy RAM
            using var scope = scopeFactory.CreateScope();
            var batchDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            batchDb.ChangeTracker.AutoDetectChangesEnabled = false;

            using var tx = await batchDb.Database.BeginTransactionAsync();

            await batchDb.Products.AddRangeAsync(batch);
            await batchDb.SaveChangesAsync();

            var events = batch.Select(p => new OutboxEvent
            {
                EntityId  = p.Id,
                EventType = OutboxEventType.Created,
                Payload   = JsonSerializer.Serialize(p.ToSearchDoc()),
                Status    = OutboxStatus.Pending,
                CreatedAt = DateTime.Now,
            }).ToList();

            await batchDb.OutboxEvents.AddRangeAsync(events);
            await batchDb.SaveChangesAsync();

            await tx.CommitAsync();
            // scope.Dispose() ở cuối using — DbContext bị hủy hoàn toàn, GC thu hồi tất cả

            total += batch.Count;
            seedProgress.Report(total);

            var mem = GC.GetTotalMemory(false) / 1024 / 1024;
            await File.AppendAllTextAsync(logPath,
                $"{DateTime.Now:HH:mm:ss} | total={total}/{count} | RAM={mem}MB\n");
        }

        await File.AppendAllTextAsync(logPath, $"{DateTime.Now:HH:mm:ss} | SEED DONE total={total}\n");
        seedProgress.Complete();

        return total;
    }

    // ── CREATE ───────────────────────────────────────────────────────────────
    public async Task<Product> CreateAsync(Product product)
    {
        product.CreatedAt = DateTime.Now;
        product.UpdatedAt = DateTime.Now;

        using var tx = await db.Database.BeginTransactionAsync();

        // Bước 1: save product trước để có Id
        db.Products.Add(product);
        await db.SaveChangesAsync();

        // Bước 2: tạo OutboxEvent với Id đúng — cùng transaction
        db.OutboxEvents.Add(new OutboxEvent
        {
            EntityId  = product.Id,
            EventType = OutboxEventType.Created,
            Payload   = JsonSerializer.Serialize(product.ToSearchDoc()),
            Status    = OutboxStatus.Pending,
            CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync();

        await tx.CommitAsync();
        return product;
    }

    // ── UPDATE ───────────────────────────────────────────────────────────────
    public async Task<bool> UpdateAsync(Product updated)
    {
        var existing = await db.Products.FindAsync(updated.Id);
        if (existing is null) return false;

        existing.ProductName  = updated.ProductName;
        existing.Price        = updated.Price;
        existing.Stock        = updated.Stock;
        existing.CategoryName = updated.CategoryName;
        existing.BrandName    = updated.BrandName;
        existing.BrandCountry = updated.BrandCountry;
        existing.SupplierName = updated.SupplierName;
        existing.SupplierCity = updated.SupplierCity;
        existing.UpdatedAt    = DateTime.Now;

        // Outbox event trong cùng transaction
        db.OutboxEvents.Add(new OutboxEvent
        {
            EntityId  = existing.Id,
            EventType = OutboxEventType.Updated,
            Payload   = JsonSerializer.Serialize(existing.ToSearchDoc()),
            Status    = OutboxStatus.Pending,
            CreatedAt = DateTime.Now,
        });

        await db.SaveChangesAsync();
        return true;
    }

    // ── DELETE ───────────────────────────────────────────────────────────────
    public async Task<bool> DeleteAsync(int id)
    {
        var product = await db.Products.FindAsync(id);
        if (product is null) return false;

        db.Products.Remove(product);

        // Outbox event: không cần payload vì chỉ cần EntityId để xóa ES
        db.OutboxEvents.Add(new OutboxEvent
        {
            EntityId  = id,
            EventType = OutboxEventType.Deleted,
            Payload   = null,
            Status    = OutboxStatus.Pending,
            CreatedAt = DateTime.Now,
        });

        await db.SaveChangesAsync();
        return true;
    }
}
