using System.Diagnostics;
using System.Text.Json;

namespace MultiWaveScraper.Core.Search;

/// <summary>SearXNG JSON API — self-hosted агрегатор выдачи (рекомендация plan2).</summary>
public sealed class SearxngSearchProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ScraperOptions> options,
    ILogger<SearxngSearchProvider> logger) : ISearchProvider
{
    public string Name => "searxng";
    public int Order => 10;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.CurrentValue.SearxngBaseUrl);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string baseUrl = options.CurrentValue.SearxngBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new SearchProviderException(Name, "SearxngBaseUrl не настроен");
        }

        string url = $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json&language=auto&safesearch=0";
        using HttpClient client = httpClientFactory.CreateClient("search");
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SearchProviderException(Name, $"HTTP {(int)response.StatusCode}");
        }

        string trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            throw new SearchProviderException(Name, "SearXNG вернул HTML вместо JSON (включите format=json в settings.yml или сработал anti-bot)");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("results", out JsonElement results))
            {
                return [];
            }

            var hits = new List<SearchHit>();
            int rank = 0;
            foreach (JsonElement item in results.EnumerateArray())
            {
                string? link = item.TryGetProperty("url", out JsonElement urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                hits.Add(new SearchHit
                {
                    Url = link,
                    Title = item.TryGetProperty("title", out JsonElement title) ? title.GetString() ?? "" : "",
                    Snippet = item.TryGetProperty("content", out JsonElement content) ? content.GetString() ?? "" : "",
                    Provider = Name,
                    Rank = rank++
                });

                if (hits.Count >= maxResults)
                {
                    break;
                }
            }

            logger.LogDebug("SearXNG: {Count} результатов по запросу {Query}", hits.Count, query);
            return hits;
        }
        catch (JsonException exception)
        {
            throw new SearchProviderException(Name, "Некорректный JSON от SearXNG", exception);
        }
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await SearchAsync("dotnet", 1, cancellationToken).ConfigureAwait(false);
            return new ProviderStatus
            {
                Name = Name,
                Enabled = !string.IsNullOrWhiteSpace(options.CurrentValue.SearxngBaseUrl),
                Available = true,
                Message = "OK",
                LatencyMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (Exception exception)
        {
            return new ProviderStatus
            {
                Name = Name,
                Enabled = !string.IsNullOrWhiteSpace(options.CurrentValue.SearxngBaseUrl),
                Available = false,
                Message = exception.Message,
                LatencyMs = stopwatch.ElapsedMilliseconds
            };
        }
    }
}
