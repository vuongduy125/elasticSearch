# Setup Guide

## Yêu cầu

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- SQL Server (bất kỳ edition nào, kể cả Express)
- Docker (để chạy Elasticsearch)

---

## 1. Cấu hình SQL Server

Mở `appsettings.json`, sửa connection string cho đúng môi trường:

```json
"ConnectionStrings": {
  "Default": "Server=TEN_MAY;Database=ElasticDemo;User Id=sa;Password=MAT_KHAU;TrustServerCertificate=True"
}
```

| Tham số | Mô tả |
|---|---|
| `Server` | Tên máy hoặc IP chạy SQL Server (ví dụ: `localhost`, `.\SQLEXPRESS`) |
| `Database` | Tên DB — app tự tạo nếu chưa có |
| `User Id` | Tài khoản SQL Server (SQL Authentication) |
| `Password` | Mật khẩu tương ứng |

> App tự động tạo database, bảng, index khi khởi động lần đầu — không cần chạy script SQL thủ công.

---

## 2. Chạy Elasticsearch bằng Docker

```bash
docker run -d \
  --name elasticsearch \
  -p 9200:9200 \
  -e "discovery.type=single-node" \
  -e "xpack.security.enabled=false" \
  -e "ES_JAVA_OPTS=-Xms512m -Xmx512m" \
  elasticsearch:8.6.1
```

Kiểm tra ES đã chạy:

```bash
curl http://localhost:9200
```

Trả về JSON là OK.

> Nếu muốn dùng port khác, sửa trong `appsettings.json`:
> ```json
> "Elasticsearch": {
>   "Url": "http://localhost:PORT_KHAC"
> }
> ```

---

## 3. Chạy ứng dụng

```bash
dotnet run
```

Truy cập `https://localhost:5032` (hoặc port hiển thị trong terminal).

---

## 4. Lần đầu chạy

1. Vào **Worker Dashboard** (`/Worker`)
2. Chọn số lượng bản ghi → nhấn **Seed thêm**
3. Chờ seed xong → worker tự động sync sang Elasticsearch
4. Vào trang chủ để tìm kiếm

---

## Lưu ý

- Nếu ES bị tắt giữa chừng, các event sync sẽ chuyển sang `Failed` — nhấn **Retry Failed** để sync lại
- Nếu lệch dữ liệu nhiều, nhấn **Reindex ES từ SQL** để đồng bộ lại toàn bộ
- **Xóa hết SQL + ES** sẽ xóa toàn bộ data để bắt đầu lại từ đầu
