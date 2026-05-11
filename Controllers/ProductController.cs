using ElasticDemo.Data;
using ElasticDemo.Models;
using ElasticDemo.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ElasticDemo.Controllers;

public class ProductController(AppDbContext db, ProductService productService) : Controller
{
    // GET /Product
    public async Task<IActionResult> Index(string? search, int page = 1)
    {
        const int pageSize = 20;
        var query = db.Products.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p =>
                p.ProductName.Contains(search)  ||
                p.BrandName.Contains(search)    ||
                p.CategoryName.Contains(search) ||
                p.SupplierCity.Contains(search));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Khi không search: dùng sys.partitions (~0ms) thay COUNT(*) (~4s trên 4M rows)
        long total;
        if (string.IsNullOrWhiteSpace(search))
        {
            total = await db.Database
                .SqlQueryRaw<long>(@"SELECT CAST(SUM(rows) AS bigint) AS Value FROM sys.partitions
                                     WHERE object_id = OBJECT_ID('Products') AND index_id IN (0,1)")
                .SingleAsync();
        }
        else
        {
            total = await query.CountAsync();
        }
        var items = await query
            .OrderByDescending(p => p.Id) // Id là clustered index — không cần sort
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        sw.Stop();

        ViewBag.Search     = search;
        ViewBag.Page       = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.Total      = total;
        ViewBag.ElapsedMs  = sw.ElapsedMilliseconds;
        ViewBag.HasSearched = !string.IsNullOrWhiteSpace(search);
        return View(items);
    }

    // GET /Product/Create
    public IActionResult Create() => View(new Product());

    // POST /Product/Create
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Product product)
    {
        if (!ModelState.IsValid) return View(product);
        await productService.CreateAsync(product);
        TempData["Success"] = $"Đã thêm \"{product.ProductName}\" — OutboxEvent: ProductCreated ✓";
        return RedirectToAction(nameof(Index));
    }

    // GET /Product/Edit/5
    public async Task<IActionResult> Edit(int id)
    {
        var product = await db.Products.FindAsync(id);
        if (product is null) return NotFound();
        return View(product);
    }

    // POST /Product/Edit/5
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Product product)
    {
        if (!ModelState.IsValid) return View(product);
        var ok = await productService.UpdateAsync(product);
        if (!ok) return NotFound();
        TempData["Success"] = $"Đã cập nhật \"{product.ProductName}\" — OutboxEvent: ProductUpdated ✓";
        return RedirectToAction(nameof(Index));
    }

    // POST /Product/Delete/5
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await db.Products.FindAsync(id);
        var ok = await productService.DeleteAsync(id);
        if (!ok) return NotFound();
        TempData["Success"] = $"Đã xóa \"{product?.ProductName}\" — OutboxEvent: ProductDeleted ✓";
        return RedirectToAction(nameof(Index));
    }
}
