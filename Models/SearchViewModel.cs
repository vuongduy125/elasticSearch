namespace ElasticDemo.Models;

public class SearchViewModel
{
    public string? Query { get; set; }
    public List<ProductSearchDoc> Results { get; set; } = new();
    public long TotalCount { get; set; }
    public long ElapsedMs { get; set; }

    // Phân trang
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public int DisplayTotalPages => TotalPages;

    // Trạng thái index
    public long IndexedDocCount { get; set; }
    public bool HasSearched { get; set; }
}
