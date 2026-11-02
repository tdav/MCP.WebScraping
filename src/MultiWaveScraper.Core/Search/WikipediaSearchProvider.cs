using System.Diagnostics;
using System.Text.Json;

namespace MultiWaveScraper.Core.Search;

/// <summary>
/// MediaWiki Search API — надёжный источник без ключей и anti-bot.
/// Используется как fallback и для офлайн-тестов.
/// </summary>
public sealed class WikipediaSearchProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ScraperOptions> options,
    ILogger<WikipediaSearchProvider> logger) : ISearchProvider
{
    public string Name => "wikipedia";
    public int Order => 40;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string language = ResolveLanguage(query);
        string url = $"https://{language}.wikipedia.org/w/api.php?action=query&list=search&format=json&utf8=1" +
                     $"&srsearch={Uri.EscapeDataString(query)}&srlimit={Math.Clamp(maxResults, 1, 50)}&srprop=snippet";

        using HttpClient client = httpClientFactory.CreateClient("search");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", options.CurrentValue.UserAgent);

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SearchProviderException(Name, $"HTTP {(int)response.StatusCode}");
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("query", out JsonElement queryElement)
            || !queryElement.TryGetProperty("search", out JsonElement search))
        {
            return [];
        }

        var hits = new List<SearchHit>();
        foreach (JsonElement item in search.EnumerateArray())
        {
            string title = item.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            string snippet = item.TryGetProperty("snippet", out JsonElement snippetElement)
                ? TextTools.StripHtml(snippetElement.GetString() ?? "")
                : "";

            string pageUrl = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
            hits.Add(new SearchHit
            {
                Url = pageUrl,
                Title = title,
                Snippet = snippet,
                Provider = Name,
                Rank = hits.Count
            });
        }

        logger.LogDebug("Wikipedia ({Language}): {Count} результатов по {Query}", language, hits.Count, query);
        return hits;
    }

    private string ResolveLanguage(string query)
    {
        string configured = options.CurrentValue.WikipediaLanguage;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return TextTools.ContainsCyrillic(query) ? "ru" : "en";
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await SearchAsync("dotnet", 1, cancellationToken).ConfigureAwait(false);
            return new ProviderStatus { Name = Name, Enabled = true, Available = true, Message = "OK", LatencyMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception exception)
        {
            return new ProviderStatus { Name = Name, Enabled = true, Available = false, Message = exception.Message, LatencyMs = stopwatch.ElapsedMilliseconds };
        }
    }
}
