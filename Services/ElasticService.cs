using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Analysis;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using ElasticDemo.Models;
using System.Diagnostics;

namespace ElasticDemo.Services;

public class ElasticService
{
    private const string IndexName = "products";
    private readonly ElasticsearchClient _client;
    private readonly string _esUrl;

    public ElasticService(IConfiguration config)
    {
        _esUrl = config["Elasticsearch:Url"] ?? "http://localhost:9200";
        var settings = new ElasticsearchClientSettings(new Uri(_esUrl))
            .DefaultIndex(IndexName);
        _client = new ElasticsearchClient(settings);
    }

    // ── Tạo index với ngram analyzer (hỗ trợ tìm substring: "ple" → "Apple") ─
    public async Task EnsureIndexAsync()
    {
        var exists = await _client.Indices.ExistsAsync(IndexName);
        if (exists.Exists) return;

        await _client.Indices.CreateAsync(IndexName, c => c
            .Settings(s => s
                .MaxNgramDiff(8)
                .Analysis(a => a
                    .Tokenizers(t => t
                        .NGram("ngram_tokenizer", ng => ng
                            .MinGram(2)
                            .MaxGram(10)
                            .TokenChars([TokenChar.Letter, TokenChar.Digit])
                        )
                    )
                    .Analyzers(an => an
                        .Custom("ngram_analyzer", ca => ca
                            .Tokenizer("ngram_tokenizer")
                            .Filter(["lowercase"])
                        )
                    )
                )
            )
            .Mappings(m => m
                .Properties<ProductSearchDoc>(p => p
                    .IntegerNumber(x => x.Id)
                    .Text(x => x.ProductName,  t => t.Analyzer("ngram_analyzer").SearchAnalyzer("standard"))
                    .FloatNumber(x => x.Price)
                    .IntegerNumber(x => x.Stock)
                    .Text(x => x.CategoryName, t => t.Analyzer("ngram_analyzer").SearchAnalyzer("standard"))
                    .Text(x => x.BrandName,    t => t.Analyzer("ngram_analyzer").SearchAnalyzer("standard"))
                    .Keyword(x => x.BrandCountry)
                    .Text(x => x.SupplierName, t => t.Analyzer("ngram_analyzer").SearchAnalyzer("standard"))
                    .Keyword(x => x.SupplierCity)
                )
            )
        );
    }

    // ── Bulk index một batch ─────────────────────────────────────────────────
    public async Task BulkIndexAsync(IEnumerable<ProductSearchDoc> docs)
    {
        var response = await _client.BulkAsync(b => b
            .Index(IndexName)
            .IndexMany(docs, (op, doc) => op.Id(doc.Id.ToString()))
        );

        if (!response.IsValidResponse)
            throw new Exception($"Bulk index failed: {response.ElasticsearchServerError?.Error?.Reason ?? response.DebugInformation}");

        if (response.Errors)
        {
            var firstError = response.ItemsWithErrors.FirstOrDefault();
            throw new Exception($"Bulk index error: {firstError?.Error?.Reason}");
        }
    }

    // ── Đếm số document trong index ─────────────────────────────────────────
    public async Task<long> CountAsync()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var json = await http.GetStringAsync($"{_esUrl}/{IndexName}/_count");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("count").GetInt64();
    }

    // ── MultiMatch search với boost ─────────────────────────────────────────
    public async Task<(List<ProductSearchDoc> Items, long Total, long ElapsedMs)> SearchAsync(
        string query, int page = 1, int pageSize = 20)
    {
        var sw = Stopwatch.StartNew();

        var from = (page - 1) * pageSize;

        var response = await _client.SearchAsync<ProductSearchDoc>(s => s
            .Indices(IndexName)
            .From(from)
            .Size(pageSize)
            .TrackTotalHits(new Elastic.Clients.Elasticsearch.Core.Search.TrackHits(true))
            .Query(q => q
                .MultiMatch(mm => mm
                    .Query(query)
                    .Fields(new[]
                    {
                        "productName^3",
                        "brandName^2",
                        "categoryName^1",
                        "supplierCity^1"
                    })
                    .Type(TextQueryType.BestFields)
                    //.Fuzziness(new Fuzziness("AUTO"))
                )
            )
        );

        sw.Stop();

        if (!response.IsValidResponse)
            throw new Exception($"Search error: {response.DebugInformation}");

        return (response.Documents.ToList(), response.Total, sw.ElapsedMilliseconds);
    }

    // ── Index hoặc update 1 document đơn lẻ (dùng cho Outbox) ──────────────
    public async Task IndexDocAsync(ProductSearchDoc doc)
    {
        var response = await _client.IndexAsync(doc, i => i
            .Index(IndexName)
            .Id(doc.Id.ToString())
        );
        if (!response.IsValidResponse)
            throw new Exception($"Index doc error: {response.DebugInformation}");
    }

    // ── Xóa 1 document (dùng cho Outbox khi ProductDeleted) ─────────────────
    public async Task DeleteDocAsync(int id)
    {
        await _client.DeleteAsync(IndexName, id.ToString());
    }

    // ── Xóa và tạo lại index (dùng khi re-seed) ─────────────────────────────
    public async Task DeleteIndexAsync()
    {
        var exists = await _client.Indices.ExistsAsync(IndexName);
        if (exists.Exists)
            await _client.Indices.DeleteAsync(IndexName);
    }
}
