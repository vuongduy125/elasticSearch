using ElasticDemo.Models;
using Microsoft.EntityFrameworkCore;

namespace ElasticDemo.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Price).HasColumnType("decimal(18,0)");
        });

        mb.Entity<OutboxEvent>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.Status);  // index để query Pending nhanh
        });
    }
}
