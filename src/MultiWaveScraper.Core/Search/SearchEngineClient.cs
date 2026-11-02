using System.Diagnostics;

namespace MultiWaveScraper.Core.Search;

/// <summary>
/// Последовательный обход провайдеров с fallback, тайм-аутом на провайдера
/// и дедупликацией URL. Останавливается, когда набрано достаточно результатов.
/// </summary>
public sealed class SearchEngineClient(
    IEnumerable<ISearchProvider> providers,
    IOptionsMonitor<ScraperOptions> options,
    ILogger<SearchEngineClient> logger) : ISearchEngineClient
{
    private readonly IReadOnlyList<ISearchProvider> _providers = providers.OrderBy(p => p.Order).ToList();
    private IReadOnlyList<ProviderStatus>? _cachedStatus;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public async Task<SearchResponse> SearchAsync(
        string query,
        int maxResults,
        IReadOnlyCollection<string>? providers,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var used = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<SearchHit>();
        maxResults = Math.Max(1, maxResults);

        foreach (ISearchProvider provider in Filter(providers))
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, options.CurrentValue.SearchTimeoutSeconds)));

            try
            {
                int remaining = maxResults - hits.Count;
                int perProvider = Math.Min(
                    remaining,
                    Math.Max(1, options.CurrentValue.MaxSearchResultsPerProvider));

                IReadOnlyList<SearchHit> providerHits = await provider
                    .SearchAsync(query, perProvider, providerCts.Token)
                    .ConfigureAwait(false);

                used.Add(provider.Name);
                int added = 0;
                foreach (SearchHit hit in providerHits)
                {
                    string normalized = UrlNormalizer.Normalize(hit.Url);
                    if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                    {
                        continue;
                    }

                    hits.Add(hit with { Url = normalized, Rank = hits.Count });
                    added++;
                    if (hits.Count >= maxResults)
                    {
                        break;
                    }
                }

                logger.LogDebug("Поиск '{Query}' через {Provider}: +{Added} (всего {Total})", query, provider.Name, added, hits.Count);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"{provider.Name}: тайм-аут поиска");
                logger.LogWarning("Провайдер {Provider} не ответил за {Timeout}s", provider.Name, options.CurrentValue.SearchTimeoutSeconds);
            }
            catch (Exception exception)
            {
                warnings.Add($"{provider.Name}: {exception.Message}");
                logger.LogWarning(exception, "Провайдер {Provider} завершился ошибкой", provider.Name);
            }
        }

        if (hits.Count == 0)
        {
            warnings.Add($"Ни один провайдер не дал результатов по запросу '{query}' (проверьте {string.Join(", ", Filter(providers).Select(p => p.Name))})");
        }

        return new SearchResponse
        {
            Query = query,
            Hits = hits,
            ProvidersUsed = used.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusAsync(CancellationToken cancellationToken)
    {
        if (_cachedStatus is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(1))
        {
            return _cachedStatus;
        }

        var results = new List<ProviderStatus>();
        var enabled = options.CurrentValue.EnabledProviders;
        foreach (ISearchProvider provider in _providers)
        {
            bool isEnabled = enabled.Length == 0
                             || enabled.Contains(provider.Name, StringComparer.OrdinalIgnoreCase)
                             || enabled.Contains("all", StringComparer.OrdinalIgnoreCase);

            if (!isEnabled)
            {
                results.Add(new ProviderStatus { Name = provider.Name, Enabled = false, Available = false, Message = "Отключён в конфигурации" });
                continue;
            }

            if (!provider.IsConfigured)
            {
                results.Add(new ProviderStatus { Name = provider.Name, Enabled = true, Available = false, Message = "Не настроен (например, SearxngBaseUrl пуст)" });
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                results.Add(await provider.CheckAsync(cts.Token).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                results.Add(new ProviderStatus { Name = provider.Name, Enabled = true, Available = false, Message = exception.Message });
            }
        }

        _cachedStatus = results;
        _cachedAt = DateTimeOffset.UtcNow;
        return results;
    }

    private IEnumerable<ISearchProvider> Filter(IReadOnlyCollection<string>? requested)
    {
        string[] enabled = options.CurrentValue.EnabledProviders;
        foreach (ISearchProvider provider in _providers)
        {
            bool providerEnabled = enabled.Length == 0
                                   || enabled.Contains(provider.Name, StringComparer.OrdinalIgnoreCase)
                                   || enabled.Contains("all", StringComparer.OrdinalIgnoreCase);
            if (!providerEnabled)
            {
                continue;
            }

            if (!provider.IsConfigured)
            {
                continue;
            }

            if (requested is { Count: > 0 }
                && !requested.Contains(provider.Name, StringComparer.OrdinalIgnoreCase)
                && !requested.Contains("all", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return provider;
        }
    }
}
