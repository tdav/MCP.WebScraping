using System.Diagnostics;

namespace MultiWaveScraper.Core.Search;

/// <summary>
/// Статический список URL из конфигурации (plan1, шаг "Сбор списка URL").
/// Относительные пути разрешаются относительно Scraper:SelfBaseUrl (demo-страницы сервера).
/// </summary>
public sealed class StaticUrlSearchProvider(
    IOptionsMonitor<ScraperOptions> options,
    ILogger<StaticUrlSearchProvider> logger) : ISearchProvider
{
    public string Name => "static";
    public int Order => 50;

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string baseUrl = options.CurrentValue.SelfBaseUrl;
        IReadOnlyList<SearchHit> hits = options.CurrentValue.StaticUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => UrlNormalizer.ToAbsolute(url, baseUrl))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((url, index) => new SearchHit
            {
                Url = url,
                Title = url,
                Snippet = "Статический URL из конфигурации",
                Provider = Name,
                Rank = index
            })
            .Take(Math.Max(1, maxResults))
            .ToList();

        logger.LogDebug("Static: {Count} URL, base={Base}", hits.Count, baseUrl);
        return Task.FromResult(hits);
    }

    public Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int count = options.CurrentValue.StaticUrls.Length;
        return Task.FromResult(new ProviderStatus
        {
            Name = Name,
            Enabled = count > 0,
            Available = count > 0,
            Message = count > 0 ? $"{count} статических URL" : "StaticUrls пуст",
            LatencyMs = stopwatch.ElapsedMilliseconds
        });
    }
}
