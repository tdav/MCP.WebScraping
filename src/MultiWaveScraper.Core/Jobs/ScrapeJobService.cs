namespace MultiWaveScraper.Core.Jobs;

public interface IScrapeJobService
{
    ScrapeJobSnapshot Start(ScrapeRequest request);
    ScrapeJobSnapshot? Get(string id, bool includeReport = false);
    IReadOnlyList<ScrapeJobSnapshot> List();
    bool Cancel(string id);
}

/// <summary>
/// Фоновые задания с поллингом статуса — аналог long-running tasks из plan2
/// (MCP-инструменты start_scrape_job / get_scrape_job / cancel_scrape_job).
/// </summary>
public sealed class ScrapeJobService(
    ScrapeCoordinator coordinator,
    IOptionsMonitor<ScraperOptions> configuration,
    ILogger<ScrapeJobService> logger) : IScrapeJobService
{
    private readonly ConcurrentDictionary<string, ScrapeJob> _jobs = new(StringComparer.Ordinal);

    public ScrapeJobSnapshot Start(ScrapeRequest request)
    {
        string query = (request?.Query ?? "").Trim();
        if (query.Length == 0)
        {
            throw new ArgumentException("Параметр query обязателен.", nameof(request));
        }

        ScrapeOptions defaults = configuration.CurrentValue.CreateDefaultOptions();
        ScrapeOptions options = request!.ToOptions(defaults);

        var job = new ScrapeJob
        {
            Id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..6]}",
            Query = query,
            Options = options
        };

        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job), CancellationToken.None);
        logger.LogInformation("Задание {JobId} поставлено в очередь: {Query}", job.Id, query);
        return job.ToSnapshot();
    }

    public ScrapeJobSnapshot? Get(string id, bool includeReport = false)
        => _jobs.TryGetValue(id, out ScrapeJob? job) ? job.ToSnapshot(includeReport) : null;

    public IReadOnlyList<ScrapeJobSnapshot> List()
        => _jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Take(100)
            .Select(job => job.ToSnapshot())
            .ToList();

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out ScrapeJob? job))
        {
            return false;
        }

        job.Cancel();
        return true;
    }

    private async Task RunAsync(ScrapeJob job)
    {
        job.MarkRunning();
        try
        {
            var progress = new InlineProgress(job.ReportProgress);
            ScrapeReport report = await coordinator
                .RunAsync(job.Query, job.Options, progress, job.Cancellation.Token)
                .ConfigureAwait(false);

            job.Complete(report);
            logger.LogInformation("Задание {JobId} завершено: {Pages} страниц", job.Id, report.TotalPages);
        }
        catch (OperationCanceledException)
        {
            job.Cancel();
            logger.LogInformation("Задание {JobId} отменено", job.Id);
        }
        catch (Exception exception)
        {
            job.Fail(exception);
            logger.LogError(exception, "Задание {JobId} завершилось ошибкой", job.Id);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (_jobs.Count <= 50)
        {
            return;
        }

        foreach (ScrapeJob old in _jobs.Values
                     .Where(job => job.Status is ScrapeJobStatus.Completed or ScrapeJobStatus.Failed or ScrapeJobStatus.Cancelled)
                     .OrderBy(job => job.CreatedAt)
                     .Take(_jobs.Count - 50))
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }

    private sealed class InlineProgress(Action<ScrapeProgress> onReport) : IProgress<ScrapeProgress>
    {
        public void Report(ScrapeProgress value) => onReport(value);
    }
}
