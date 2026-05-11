# Elasticsearch vs SQL Full-Text Search

## 1. Cơ chế hoạt động của Elasticsearch

### Inverted Index — trái tim của ES

Khi bạn index một document vào ES, ES không lưu nguyên văn bản — nó **phân tích và đảo ngược** thành một cấu trúc gọi là Inverted Index.

**Ví dụ 3 sản phẩm:**
```
Doc 1: "Apple Laptop Pro Max"
Doc 2: "Samsung Laptop Gaming"
Doc 3: "Apple Điện thoại iPhone"
```

**Inverted Index được tạo ra:**
```
Token      → Documents chứa token
──────────────────────────────────
"apple"    → [Doc1, Doc3]
"laptop"   → [Doc1, Doc2]
"pro"      → [Doc1]
"max"      → [Doc1]
"samsung"  → [Doc2]
"gaming"   → [Doc2]
"điện"     → [Doc3]
"thoại"    → [Doc3]
"iphone"   → [Doc3]
```

**Khi search "laptop":**
```
Tra bảng → "laptop" → [Doc1, Doc2]  ← tìm ngay, không scan
Trả về Doc1, Doc2 trong ~1ms
```

---

### Pipeline phân tích văn bản (Text Analysis)

Trước khi lưu vào Inverted Index, ES chạy text qua một pipeline:

```
Input: "Apple Laptop PRO MAX 2024!"
         │
         ▼
1. Character Filter    → loại bỏ ký tự đặc biệt
   "Apple Laptop PRO MAX 2024"
         │
         ▼
2. Tokenizer           → tách thành tokens
   ["Apple", "Laptop", "PRO", "MAX", "2024"]
         │
         ▼
3. Token Filter        → lowercase, bỏ stopwords, stemming
   ["apple", "laptop", "pro", "max", "2024"]
         │
         ▼
   Lưu vào Inverted Index
```

---

### Relevance Scoring — BM25

ES không chỉ tìm "có hay không" — nó **xếp hạng** kết quả theo độ liên quan:

```
Search: "apple laptop"

BM25 Score tính dựa trên:
  TF  (Term Frequency)  — từ xuất hiện nhiều lần trong doc → score cao hơn
  IDF (Inverse Doc Frequency) — từ hiếm trong toàn index  → score cao hơn
  Field length — doc ngắn mà có từ khóa → score cao hơn doc dài

Kết quả:
  Doc1: "Apple Laptop Pro"         → score: 2.8  ← có cả 2 từ
  Doc2: "Samsung Laptop Gaming"    → score: 1.2  ← chỉ có "laptop"
  Doc3: "Apple iPhone"             → score: 0.9  ← chỉ có "apple"
```

---

### Fuzzy Search

ES cho phép tìm gần đúng dựa trên **Levenshtein distance** (số lần chỉnh sửa):

```
Search: "laptp"  (typo, thiếu 'o')

Fuzziness AUTO:
  "laptp" → khoảng cách 1 đến "laptop" → match ✅

Search: "aplpe"  (typo, đảo chữ)
  "aplpe" → khoảng cách 2 đến "apple"  → match ✅ (nếu fuzziness=2)
```

SQL LIKE không có khả năng này.

---

### Multi-Field Search với Boost

```json
{
  "multi_match": {
    "query": "apple laptop",
    "fields": [
      "productName^3",    ← nhân score × 3
      "brandName^2",      ← nhân score × 2
      "categoryName^1"    ← giữ nguyên score
    ]
  }
}
```

Cùng từ khóa nhưng xuất hiện ở `productName` được ưu tiên hơn `categoryName`.

---

## 2. Cơ chế SQL Full-Text Search

### SQL Server FTS — cách hoạt động

SQL Server cũng có inverted index riêng, tách biệt với B-Tree index thông thường.

```sql
-- Tạo Full-Text Catalog
CREATE FULLTEXT CATALOG ftCatalog AS DEFAULT;

-- Tạo Full-Text Index trên bảng
CREATE FULLTEXT INDEX ON Products(ProductName, BrandName, CategoryName)
    KEY INDEX PK_Products
    ON ftCatalog;
```

**Quá trình build index:**
```
SQL Server chạy background process "Full-Text Indexer"
→ Đọc từng row trong bảng
→ Tách từ (word breaking) theo ngôn ngữ
→ Bỏ stopwords ("the", "a", "là", "của"...)
→ Stemming ("running" → "run")
→ Lưu vào Full-Text Index (cấu trúc riêng, không phải B-Tree)
```

### Các loại query FTS trong SQL Server

```sql
-- CONTAINS: tìm chính xác hoặc gần đúng
SELECT * FROM Products WHERE CONTAINS(ProductName, 'laptop')
SELECT * FROM Products WHERE CONTAINS(ProductName, '"laptop computer"')  -- cụm từ

-- FREETEXT: tự động tách từ, linh hoạt hơn
SELECT * FROM Products WHERE FREETEXT(ProductName, 'laptop gaming')

-- CONTAINSTABLE: trả về relevance score
SELECT p.*, ft.RANK
FROM Products p
JOIN CONTAINSTABLE(Products, ProductName, 'laptop') ft
  ON p.Id = ft.[KEY]
ORDER BY ft.RANK DESC
```

---

## 3. So sánh trực tiếp

### Tốc độ với 1.000.000 records

| Query type | SQL LIKE | SQL FTS | Elasticsearch |
|---|---|---|---|
| Tìm chính xác | ~3000ms | ~50ms | ~5ms |
| Tìm nhiều field | ~5000ms | ~100ms | ~8ms |
| Fuzzy search | ❌ không có | ~200ms | ~10ms |
| Aggregation | ~4000ms | ❌ hạn chế | ~15ms |

---

### Tính năng

| Tính năng | SQL LIKE | SQL FTS | Elasticsearch |
|---|---|---|---|
| Tìm chính xác | ✅ | ✅ | ✅ |
| Tìm không dấu | ❌ | ✅ (collation) | ✅ (analyzer) |
| Fuzzy / Typo | ❌ | ⚠️ hạn chế | ✅ |
| Relevance score | ❌ | ✅ RANK | ✅ BM25 |
| Multi-field boost | ❌ | ⚠️ | ✅ |
| Highlight kết quả | ❌ | ❌ | ✅ |
| Autocomplete | ❌ | ❌ | ✅ |
| Aggregation/Facet | ⚠️ GROUP BY | ❌ | ✅ |
| Geo search | ❌ | ❌ | ✅ |
| Real-time index | ✅ | ⚠️ delay | ⚠️ ~1s delay |

---

### Kiến trúc & Vận hành

| | SQL FTS | Elasticsearch |
|---|---|---|
| Infrastructure | Cùng SQL Server | Server riêng |
| Sync data | Tự động (cùng DB) | Cần tự sync (Outbox...) |
| Scale | Vertical (nâng server) | Horizontal (thêm node) |
| Storage | Trong SQL Server | Riêng biệt |
| Backup | Chung với DB | Riêng |
| License | Kèm SQL Server | Free (open source) |

---

### Khi nào dùng cái nào

```
Dùng SQL LIKE khi:
    - Dataset nhỏ (< 100k records)
    - Search đơn giản, không cần relevance
    - Không muốn phức tạp thêm

Dùng SQL Full-Text Search khi:
    - Đã có SQL Server, không muốn thêm infra
    - Dataset trung bình (100k - 5M records)
    - Search đủ dùng, không cần fuzzy phức tạp
    - Real-time index quan trọng (FTS tự sync)

Dùng Elasticsearch khi:
    - Dataset lớn (> 1M records)
    - Cần fuzzy, autocomplete, highlight
    - Cần relevance scoring tốt
    - Nhiều loại nội dung khác nhau
    - Cần scale horizontal
    - Search là core feature của sản phẩm
```

---

## 4. Tại sao LIKE '%keyword%' chậm?

```
B-Tree Index của SQL hoạt động như từ điển:
    Tìm "laptop%" (prefix) → dùng được index, nhảy thẳng đến 'l'
    Tìm "%laptop%" (có % đầu) → không dùng được index
                                → phải đọc từng trang từ đầu đến cuối

Với 1M records, mỗi row ~500 bytes → tổng ~500MB data cần scan
→ I/O và CPU đều bị đẩy lên max
→ 2000-5000ms là bình thường

Inverted Index (FTS và ES) giải quyết bằng cách:
    Không scan rows → tra cứu trực tiếp token
    "laptop" → [id1, id5, id100...] → fetch đúng rows đó
    → không quan tâm % đầu hay cuối
```

---

## 5. Demo trong project này

Project ElasticDemo minh họa 3 tình huống:

```
1. SQL LIKE (ProductController):
   db.Products.Where(p => p.ProductName.Contains(keyword))
   → EF Core dịch thành: WHERE ProductName LIKE '%keyword%'
   → 1M records: ~2000-5000ms

2. Elasticsearch MultiMatch (HomeController):
   MultiMatch trên productName^3, brandName^2, categoryName^1
   → 1M records: ~5-20ms

3. Outbox Pattern (WorkerController + OutboxWorker):
   SQL là source of truth
   OutboxEvents → Worker → ES sync
   → Đảm bảo ES luôn có data mới nhất từ SQL
```
