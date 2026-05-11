namespace ElasticDemo.Models;

public class ProductSearchDoc
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string BrandName { get; set; } = string.Empty;
    public string BrandCountry { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierCity { get; set; } = string.Empty;
}
