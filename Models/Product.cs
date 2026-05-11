using System.ComponentModel.DataAnnotations;

namespace ElasticDemo.Models;

public class Product
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    [Range(0, 999_999_999)]
    public decimal Price { get; set; }

    [Range(0, 999_999)]
    public int Stock { get; set; }

    [MaxLength(100)]
    public string CategoryName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string BrandName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string BrandCountry { get; set; } = string.Empty;

    [MaxLength(200)]
    public string SupplierName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string SupplierCity { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ProductSearchDoc ToSearchDoc() => new()
    {
        Id           = Id,
        ProductName  = ProductName,
        Price        = Price,
        Stock        = Stock,
        CategoryName = CategoryName,
        BrandName    = BrandName,
        BrandCountry = BrandCountry,
        SupplierName = SupplierName,
        SupplierCity = SupplierCity,
    };
}
