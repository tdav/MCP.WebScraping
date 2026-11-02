namespace MultiWaveScraper.Core.Models;

/// <summary>Состояние фоновой задачи скрейпинга (аналог long-running tasks из plan2).</summary>
public sealed class ScrapeJob
{
    private readonly object _sync = new();
    private ScrapeJobStatus _status = ScrapeJobStatus.Queued;
    private int _percent;
    private string _message = "В очереди";
    private ScrapeReport? _report;
    private string? _error;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;

    public required string Id { get; init; }
    public required string Query { get; init; }
    public required ScrapeOptions Options { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();

    public ScrapeJobStatus Status { get { lock (_sync) return _status; } }
    public int Percent { get { lock (_sync) return _percent; } }
    public string Message { get { lock (_sync) return _message; } }
    public ScrapeReport? Report { get { lock (_sync) return _report; } }
    public string? Error { get { lock (_sync) return _error; } }
    public DateTimeOffset? StartedAt { get { lock (_sync) return _startedAt; } }
    public DateTimeOffset? CompletedAt { get { lock (_sync) return _completedAt; } }

    public void MarkRunning()
    {
        lock (_sync)
        {
            _status = ScrapeJobStatus.Running;
            _startedAt = DateTimeOffset.UtcNow;
            _message = "Выполняется";
        }
    }

    public void ReportProgress(ScrapeProgress progress)
    {
        lock (_sync)
        {
            _percent = Math.Clamp(progress.Percent, 0, 100);
            _message = progress.Message;
        }
    }

    public void Complete(ScrapeReport report)
    {
        lock (_sync)
        {
            _report = report;
            _status = ScrapeJobStatus.Completed;
            _percent = 100;
            _message = "Готово";
            _completedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(Exception exception)
    {
        lock (_sync)
        {
            _status = ScrapeJobStatus.Failed;
            _error = exception.ToString();
            _message = "Ошибка: " + exception.Message;
            _completedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_status is ScrapeJobStatus.Completed or ScrapeJobStatus.Failed or ScrapeJobStatus.Cancelled)
            {
                return;
            }

            _status = ScrapeJobStatus.Cancelled;
            _message = "Отменено";
            _completedAt = DateTimeOffset.UtcNow;
        }

        Cancellation.Cancel();
    }

    public ScrapeJobSnapshot ToSnapshot(bool includeReport = false) => new()
    {
        Id = Id,
        Query = Query,
        Status = Status,
        Percent = Percent,
        Message = Message,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        Error = Error,
        Report = includeReport ? Report : null,
        ReportFile = Report?.ReportFile
    };
}

public sealed record ScrapeJobSnapshot
{
    public required string Id { get; init; }
    public required string Query { get; init; }
    public ScrapeJobStatus Status { get; init; }
    public int Percent { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Error { get; init; }
    public string? ReportFile { get; init; }
    public ScrapeReport? Report { get; init; }
}
