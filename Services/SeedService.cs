using Bogus;
using ElasticDemo.Models;

namespace ElasticDemo.Services;

public class SeedService
{
    private static readonly string[] Brands = ["Apple", "Samsung", "Xiaomi", "Sony", "Dell", "Asus", "Lenovo", "Oppo", "Vivo", "Realme"];
    private static readonly string[] Categories = ["Điện thoại", "Laptop", "Tablet", "Tai nghe", "Đồng hồ", "Máy ảnh", "Phụ kiện"];
    private static readonly string[] Cities = ["Hà Nội", "TP.HCM", "Đà Nẵng", "Seoul", "Tokyo", "Thượng Hải"];

    private static readonly Dictionary<string, string> BrandCountries = new()
    {
        ["Apple"]   = "Mỹ",
        ["Samsung"] = "Hàn Quốc",
        ["Xiaomi"]  = "Trung Quốc",
        ["Sony"]    = "Nhật Bản",
        ["Dell"]    = "Mỹ",
        ["Asus"]    = "Đài Loan",
        ["Lenovo"]  = "Trung Quốc",
        ["Oppo"]    = "Trung Quốc",
        ["Vivo"]    = "Trung Quốc",
        ["Realme"]  = "Trung Quốc",
    };

    public IEnumerable<List<ProductSearchDoc>> GenerateBatches(int total = 1_000_000, int batchSize = 5000)
    {
        var faker = new Faker<ProductSearchDoc>("vi")
            .RuleFor(p => p.Id, f => f.IndexFaker + 1)
            .RuleFor(p => p.ProductName, f =>
            {
                var brand = f.PickRandom(Brands);
                var category = f.PickRandom(Categories);
                var model = f.Commerce.ProductAdjective();
                return $"{brand} {category} {model} {f.Random.AlphaNumeric(4).ToUpper()}";
            })
            .RuleFor(p => p.Price, f => Math.Round(f.Random.Decimal(99_000, 50_000_000), 0))
            .RuleFor(p => p.Stock, f => f.Random.Int(0, 9999))
            .RuleFor(p => p.CategoryName, f => f.PickRandom(Categories))
            .RuleFor(p => p.BrandName, f => f.PickRandom(Brands))
            .RuleFor(p => p.BrandCountry, (f, p) =>
                BrandCountries.TryGetValue(p.BrandName, out var country) ? country : "Khác")
            .RuleFor(p => p.SupplierName, f => $"Công ty {f.Company.CompanyName()}")
            .RuleFor(p => p.SupplierCity, f => f.PickRandom(Cities));

        int generated = 0;
        while (generated < total)
        {
            int count = Math.Min(batchSize, total - generated);
            var batch = faker.Generate(count);
            generated += count;
            yield return batch;
        }
    }
}
