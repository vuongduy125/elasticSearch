using ElasticDemo.Data;
using ElasticDemo.Services;
using Microsoft.EntityFrameworkCore;

// Bắt crash không xử lý — in ra console trước khi process chết
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] UnhandledException: {e.ExceptionObject}");
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] UnobservedTaskException: {e.Exception}");
    e.SetObserved();
};

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// SQL Server
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// Elasticsearch & Seed
builder.Services.AddSingleton<ElasticService>();
builder.Services.AddSingleton<SeedService>();
builder.Services.AddSingleton<SeedProgressService>();

// Outbox
builder.Services.AddSingleton<WorkerControl>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

// Tự tạo DB nếu chưa có
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Composite index cho OutboxWorker query: WHERE Status='Pending' ORDER BY Id
    db.Database.ExecuteSqlRaw(@"
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxEvents_Status_Id' AND object_id = OBJECT_ID('OutboxEvents'))
        CREATE INDEX IX_OutboxEvents_Status_Id ON OutboxEvents(Status, Id)");

    // Bật RCSI — writer không block reader, giảm lock contention khi seed song song với worker
    db.Database.ExecuteSqlRaw(@"
        IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = DB_NAME() AND is_read_committed_snapshot_on = 1)
        BEGIN
            DECLARE @db NVARCHAR(128) = DB_NAME();
            EXEC('ALTER DATABASE [' + @db + '] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
        END");

}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
