namespace MultiWaveScraper.Core.Scraping;

public sealed record FetchedPage
{
    public required string RequestedUrl { get; init; }
    public string FinalUrl { get; init; } = "";
    public int StatusCode { get; init; }
    public string Html { get; init; } = "";
    public string Via { get; init; } = "http";
    public string? Error { get; init; }
    public bool BlockedByRobots { get; init; }
    public bool IsSuccessful => string.IsNullOrEmpty(Error) && !string.IsNullOrEmpty(Html);
}

public interface IWebPageFetcher
{
    Task<FetchedPage?> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Headless-браузер как fallback для SPA (plan2, шаг 4).</summary>
public interface IBrowserPageFetcher
{
    Task<FetchedPage?> FetchAsync(string url, CancellationToken cancellationToken);
}

public interface IPageScraper
{
    Task<ScrapedPage?> ScrapeAsync(
        SearchHit hit,
        WaveType wave,
        string matchedQuery,
        ScrapeOptions options,
        CancellationToken cancellationToken);
}
