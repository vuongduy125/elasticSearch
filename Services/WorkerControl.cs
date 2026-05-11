namespace ElasticDemo.Services;

/// <summary>
/// Singleton điều khiển OutboxWorker — pause/resume và theo dõi stats.
/// </summary>
public class WorkerControl
{
    private volatile bool _paused = false;

    public bool IsPaused => _paused;
    public bool IsRunning => !_paused;

    public int ProcessedTotal { get; private set; }
    public int FailedTotal    { get; private set; }
    public DateTime? LastRunAt { get; private set; }
    public string? LastError   { get; private set; }

    // Cache stats — trả về ngay khi SQL bận
    public int    CachedSqlCount     { get; set; }
    public long   CachedEsCount      { get; set; }
    public int    CachedPendingCount { get; set; }
    public int    CachedFailedCount  { get; set; }
    public int    CachedDoneCount    { get; set; }
    public object CachedRecentEvents { get; set; } = new List<object>();
    public bool   StatsFromCache     { get; set; }

    public void Pause()  => _paused = true;
    public void Resume() => _paused = false;

    public void RecordSuccess(int count)
    {
        ProcessedTotal += count;
        LastRunAt       = DateTime.Now;
        LastError       = null;
    }

    public void RecordFailure(string error)
    {
        FailedTotal++;
        LastError = error;
        LastRunAt = DateTime.Now;
    }
}
