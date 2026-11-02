using MultiWaveScraper.Core.Waves;

namespace MultiWaveScraper.Core.Jobs;

/// <summary>Единая точка запуска: движок + сохранение отчёта.</summary>
public sealed class ScrapeCoordinator(
    MultiWaveScrapeEngine engine,
    IReportStore reportStore)
{
    public async Task<ScrapeReport> RunAsync(
        string query,
        ScrapeOptions options,
        IProgress<ScrapeProgress>? progress,
        CancellationToken cancellationToken)
    {
        ScrapeReport report = await engine.RunAsync(query, options, progress, cancellationToken).ConfigureAwait(false);
        string? file = await reportStore.SaveAsync(report, cancellationToken).ConfigureAwait(false);
        return file is null ? report : report with { ReportFile = file };
    }
}
