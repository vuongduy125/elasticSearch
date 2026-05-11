namespace ElasticDemo.Services;

/// <summary>
/// Singleton lưu trạng thái seed đang chạy — dùng để UI poll tiến độ.
/// </summary>
public class SeedProgressService
{
    public bool IsRunning { get; private set; }
    public int IndexedCount { get; private set; }
    public int TotalCount { get; private set; } = 1_000_000;
    public string? LastError { get; private set; }
    public bool IsDone { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }

    public int ProgressPercent =>
        TotalCount == 0 ? 0 : (int)Math.Round(IndexedCount * 100.0 / TotalCount);

    public void Start(int total)
    {
        TotalCount   = total;
        IsRunning    = true;
        IsDone       = false;
        IndexedCount = 0;
        LastError    = null;
        StartedAt    = DateTime.Now;
        FinishedAt   = null;
    }

    public void Report(int indexed) => IndexedCount = indexed;

    public void Complete()
    {
        IsRunning = false;
        IsDone = true;
        IndexedCount = TotalCount;
        FinishedAt = DateTime.Now;
    }

    public void Fail(string error)
    {
        IsRunning = false;
        IsDone = false;
        LastError = error;
        FinishedAt = DateTime.Now;
    }
}
