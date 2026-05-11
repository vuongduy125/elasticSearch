using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ElasticDemo.Models;
using ElasticDemo.Services;

namespace ElasticDemo.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ElasticService _elasticService;
    private readonly SeedService _seedService;
    private readonly SeedProgressService _progress;

    public HomeController(
        ILogger<HomeController> logger,
        ElasticService elasticService,
        SeedService seedService,
        SeedProgressService progress)
    {
        _logger = logger;
        _elasticService = elasticService;
        _seedService = seedService;
        _progress = progress;
    }

    // GET /
    public async Task<IActionResult> Index(string? query, int page = 1)
    {
        var vm = new SearchViewModel
        {
            Query = query,
            Page = Math.Max(1, page),
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            try
            {
                var (items, total, ms) = await _elasticService.SearchAsync(query, vm.Page, vm.PageSize);
                vm.Results = items;
                vm.TotalCount = total;
                vm.ElapsedMs = ms;
                vm.HasSearched = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Search failed");
                ViewBag.Error = $"Lỗi tìm kiếm: {ex.Message}";
            }
        }

        return View(vm);
    }

    // POST /Home/Seed — kick off background task, trả về ngay
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Seed()
    {
        if (_progress.IsRunning)
            return Json(new { started = false, message = "Đang chạy rồi!" });

        // Chạy trên thread pool, không await — response về ngay lập tức
        _ = Task.Run(async () =>
        {
            _progress.Start(1_000_000);
            try
            {
                await _elasticService.DeleteIndexAsync();
                await _elasticService.EnsureIndexAsync();

                int indexed = 0;
                foreach (var batch in _seedService.GenerateBatches(total: 1_000_000, batchSize: 5000))
                {
                    await _elasticService.BulkIndexAsync(batch);
                    indexed += batch.Count;
                    _progress.Report(indexed);
                    _logger.LogInformation("Seeded {Count}/1.000.000", indexed);
                }

                _progress.Complete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Seed failed");
                _progress.Fail(ex.Message);
            }
        });

        return Json(new { started = true });
    }

    // GET /Home/SeedProgress — UI poll mỗi 2 giây
    public IActionResult SeedProgress()
    {
        return Json(new
        {
            isRunning  = _progress.IsRunning,
            isDone     = _progress.IsDone,
            indexed    = _progress.IndexedCount,
            total      = _progress.TotalCount,
            percent    = _progress.ProgressPercent,
            error      = _progress.LastError,
            startedAt  = _progress.StartedAt?.ToString("HH:mm:ss"),
            finishedAt = _progress.FinishedAt?.ToString("HH:mm:ss"),
        });
    }

    // GET /Home/Status — đếm doc trong ES
    public async Task<IActionResult> Status()
    {
        try
        {
            var count = await _elasticService.CountAsync();
            return Json(new { count, ok = true });
        }
        catch (Exception ex)
        {
            return Json(new { count = 0, ok = false, error = ex.Message });
        }
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
