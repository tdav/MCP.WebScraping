using System.Text.Encodings.Web;

namespace MultiWaveScraper.Core.Jobs;

public interface IReportStore
{
    Task<string?> SaveAsync(ScrapeReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Сохраняет полные отчёты (включая Markdown) в JSON-файлы,
/// чтобы MCP-клиент мог получать компактные выжимки, а полные дампы оставались на диске.
/// </summary>
public sealed class FileReportStore(
    IOptionsMonitor<ScraperOptions> options,
    ILogger<FileReportStore> logger) : IReportStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string?> SaveAsync(ScrapeReport report, CancellationToken cancellationToken)
    {
        ScraperOptions settings = options.CurrentValue;
        if (!settings.PersistReports)
        {
            return null;
        }

        try
        {
            string directory = Path.IsPathRooted(settings.ReportDirectory)
                ? settings.ReportDirectory
                : Path.Combine(Directory.GetCurrentDirectory(), settings.ReportDirectory);

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"scrape-{report.Id}.json");
            string json = JsonSerializer.Serialize(report, SerializerOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Отчёт сохранён: {Path}", path);
            return path;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Не удалось сохранить отчёт");
            return null;
        }
    }
}
