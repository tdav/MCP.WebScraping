using System.Diagnostics;
using MultiWaveScraper.Core.Expansion;
using MultiWaveScraper.Core.Llm;
using MultiWaveScraper.Core.Scraping;
using MultiWaveScraper.Core.Search;

namespace MultiWaveScraper.Core.Waves;

/// <summary>
/// Оркестратор 4 волн: expansion → SERP discovery → dedup → scraping →
/// очистка/классификация → отчёт.
/// </summary>
public sealed class MultiWaveScrapeEngine(
    ISearchEngineClient searchEngine,
    IQueryExpansionService expansion,
    IPageScraper pageScraper,
    ISemanticSimilarityService semantic,
    IOptionsMonitor<ScraperOptions> configuration,
    ILogger<MultiWaveScrapeEngine> logger)
{
    public async Task<ScrapeReport> RunAsync(
        string query,
        ScrapeOptions options,
        IProgress<ScrapeProgress>? progress,
        CancellationToken cancellationToken)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            throw new ArgumentException("Поисковый запрос не может быть пустым.", nameof(query));
        }

        options = NormalizeOptions(options);
        var overall = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string reportId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..8]}";

        var visitedUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var seenHashes = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var collectedTitles = new ConcurrentBag<string>();
        var warnings = new ConcurrentQueue<string>();
        var providersUsed = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var queryRefs = new ConcurrentBag<(WaveType Wave, string Query)> { (WaveType.ExactMatch, query) };
        var waveResults = new List<WaveResult>(options.EffectiveWaves);
        int totalPages = 0;

        logger.LogInformation(
            "Старт многоволнового скрейпинга: query='{Query}', волн={Waves}, страниц/запрос={Pages}, параллелизм={Concurrency}",
            query, options.EffectiveWaves, options.MaxPagesPerQuery, options.MaxConcurrency);

        for (int waveNumber = 1; waveNumber <= options.EffectiveWaves; waveNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = (WaveType)waveNumber;
            var waveStopwatch = Stopwatch.StartNew();

            progress?.Report(new ScrapeProgress
            {
                Query = query,
                Wave = wave,
                Message = $"Волна {waveNumber}/{options.EffectiveWaves}: генерация ключей ({WaveNames.Describe(wave)})",
                Percent = (int)((waveNumber - 1) * 100.0 / options.EffectiveWaves),
                PagesCollected = totalPages
            });

            ExpansionResult expanded = await expansion.GenerateAsync(
                    query,
                    wave,
                    collectedTitles.ToList(),
                    options.MaxQueriesPerWave,
                    options.EnableLlmExpansion,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(expanded.Error))
            {
                AddWarning(warnings, $"Волна {waveNumber}: {expanded.Error}");
            }

            IReadOnlyList<string> waveQueries = expanded.Queries.Count > 0
                ? expanded.Queries
                : wave == WaveType.ExactMatch ? [query] : [];

            if (waveQueries.Count == 0)
            {
                AddWarning(warnings, $"Волна {waveNumber}: не удалось сгенерировать ключи, волна пропущена");
                waveResults.Add(new WaveResult
                {
                    Wave = wave,
                    GeneratedQueries = [],
                    ExpansionSource = expanded.Source,
                    ExpansionModel = expanded.Model,
                    ExpansionError = expanded.Error,
                    DurationMs = waveStopwatch.ElapsedMilliseconds
                });
                continue;
            }

            foreach (string waveQuery in waveQueries)
            {
                queryRefs.Add((wave, waveQuery));
            }

            var counters = new WaveCounters();
            var wavePages = new ConcurrentBag<ScrapedPage>();

            await Parallel.ForEachAsync(
                waveQueries,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxConcurrency,
                    CancellationToken = cancellationToken
                },
                async (waveQuery, token) =>
                {
                    int perQueryPages = 0;
                    try
                    {
                        int maxResults = Math.Min(
                            Math.Max(options.MaxPagesPerQuery * 2, options.MaxPagesPerQuery),
                            Math.Max(1, configuration.CurrentValue.MaxSearchResultsPerProvider));

                        SearchResponse searchResponse = await searchEngine
                            .SearchAsync(waveQuery, maxResults, options.Providers, token)
                            .ConfigureAwait(false);

                        Interlocked.Add(ref counters.Hits, searchResponse.Hits.Count);
                        foreach (string provider in searchResponse.ProvidersUsed)
                        {
                            providersUsed.TryAdd(provider, 0);
                        }

                        foreach (string warning in searchResponse.Warnings)
                        {
                            AddWarning(warnings, warning);
                        }

                        if (searchResponse.Hits.Count == 0)
                        {
                            Interlocked.Increment(ref counters.SearchErrors);
                        }

                        foreach (SearchHit hit in searchResponse.Hits)
                        {
                            token.ThrowIfCancellationRequested();

                            if (string.IsNullOrWhiteSpace(hit.Url) || UrlNormalizer.LooksLikeSkippableResource(hit.Url))
                            {
                                continue;
                            }

                            string normalizedUrl = UrlNormalizer.Normalize(hit.Url);
                            if (!visitedUrls.TryAdd(normalizedUrl, 0))
                            {
                                Interlocked.Increment(ref counters.DuplicateUrls);
                                continue;
                            }

                            if (++perQueryPages > options.MaxPagesPerQuery)
                            {
                                break;
                            }

                            Interlocked.Increment(ref counters.UniqueUrls);

                            int slot = Interlocked.Increment(ref totalPages);
                            if (slot > options.MaxTotalPages)
                            {
                                Interlocked.Increment(ref counters.Failed);
                                continue;
                            }

                            ScrapedPage? page = await pageScraper
                                .ScrapeAsync(hit with { Url = normalizedUrl }, wave, waveQuery, options, token)
                                .ConfigureAwait(false);

                            if (page is null)
                            {
                                Interlocked.Increment(ref counters.Failed);
                                continue;
                            }

                            if (!seenHashes.TryAdd(page.ContentHash, 0))
                            {
                                Interlocked.Increment(ref counters.ContentDuplicates);
                                continue;
                            }

                            RelevanceResult relevance = await RelevanceScorer
                                .ScoreAsync(query, queryRefs.ToArray(), page, semantic, options.EnableSemanticScoring, token)
                                .ConfigureAwait(false);

                            ScrapedPage scored = page with
                            {
                                MatchKind = relevance.Kind,
                                RelevanceScore = Math.Round(relevance.Score, 3),
                                MatchedTerms = relevance.Terms
                            };

                            wavePages.Add(scored);
                            Interlocked.Increment(ref counters.Scraped);
                            collectedTitles.Add(scored.Title);

                            if (scored.Error is not null)
                            {
                                AddWarning(warnings, $"{scored.Url}: {scored.Error}");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        AddWarning(warnings, $"Ошибка поиска '{waveQuery}': {exception.Message}");
                        logger.LogWarning(exception, "Ошибка обработки запроса {Query}", waveQuery);
                    }
                }).ConfigureAwait(false);

            List<ScrapedPage> ordered = wavePages
                .OrderByDescending(page => page.RelevanceScore)
                .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var waveResult = new WaveResult
            {
                Wave = wave,
                GeneratedQueries = waveQueries,
                ExpansionSource = expanded.Source,
                ExpansionModel = expanded.Model,
                ExpansionError = expanded.Error,
                Pages = ordered,
                Stats = new WaveStats
                {
                    HitsDiscovered = counters.Hits,
                    UniqueUrls = counters.UniqueUrls,
                    DuplicateUrlsSkipped = counters.DuplicateUrls,
                    PagesScraped = counters.Scraped,
                    PagesFailed = counters.Failed,
                    ContentDuplicatesSkipped = counters.ContentDuplicates,
                    SearchErrors = counters.SearchErrors
                },
                DurationMs = waveStopwatch.ElapsedMilliseconds
            };

            waveResults.Add(waveResult);

            progress?.Report(new ScrapeProgress
            {
                Query = query,
                Wave = wave,
                Message = $"Волна {waveNumber}/{options.EffectiveWaves}: +{ordered.Count} страниц (ключи: {string.Join(" | ", waveQueries)})",
                Percent = (int)(waveNumber * 100.0 / options.EffectiveWaves),
                PagesCollected = totalPages
            });

            logger.LogInformation(
                "Волна {Wave}: ключей={Queries}, хитов={Hits}, страниц={Pages}, дублей URL={DupUrls}, дублей контента={DupContent}",
                wave, waveQueries.Count, counters.Hits, ordered.Count, counters.DuplicateUrls, counters.ContentDuplicates);

            if (options.MaxPagesPerQuery <= 0 || totalPages > options.MaxTotalPages)
            {
                AddWarning(warnings, $"Достигнут лимит страниц ({options.MaxTotalPages}), дальнейшие волны могут быть неполными");
            }

            if (configuration.CurrentValue.WaveDelayMs > 0 && waveNumber < options.EffectiveWaves)
            {
                await Task.Delay(configuration.CurrentValue.WaveDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        var pages = waveResults.SelectMany(result => result.Pages).ToList();
        var classification = new ScrapeClassification
        {
            Exact = Classify(pages, MatchKind.Exact),
            Alternatives = Classify(pages, MatchKind.Alternative),
            Synonyms = Classify(pages, MatchKind.Synonym),
            Related = Classify(pages, MatchKind.Related),
            Unclassified = Classify(pages, MatchKind.None)
        };

        var report = new ScrapeReport
        {
            Id = reportId,
            Query = query,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = overall.ElapsedMilliseconds,
            Waves = waveResults,
            Classification = classification,
            Options = options,
            ProvidersUsed = providersUsed.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            LlmModel = waveResults.Select(result => result.ExpansionModel)
                           .FirstOrDefault(model => !string.IsNullOrWhiteSpace(model)) ?? "",
            LlmSourceSummary = string.Join("; ", waveResults.Select(result =>
                $"wave{(int)result.Wave}={result.ExpansionSource}" + (string.IsNullOrEmpty(result.ExpansionModel) ? "" : $"({result.ExpansionModel})")))
        };

        logger.LogInformation(
            "Готово: {Pages} страниц за {Seconds:F1}s, провайдеры: {Providers}",
            report.TotalPages, overall.Elapsed.TotalSeconds, string.Join(", ", report.ProvidersUsed));

        return report;
    }

    private static IReadOnlyList<PageRef> Classify(IEnumerable<ScrapedPage> pages, MatchKind kind)
        => pages.Where(page => page.MatchKind == kind)
            .Select(page => new PageRef
            {
                Url = page.Url,
                Title = page.Title,
                MatchedQuery = page.MatchedQuery,
                DiscoveredInWave = page.DiscoveredInWave,
                MatchKind = page.MatchKind,
                RelevanceScore = page.RelevanceScore,
                WordCount = page.WordCount
            })
            .ToList();

    private static ScrapeOptions NormalizeOptions(ScrapeOptions options)
    {
        ScrapeOptions result = options.Clone();
        result.MaxPagesPerQuery = Math.Clamp(result.MaxPagesPerQuery, 1, 10);
        result.MaxQueriesPerWave = Math.Clamp(result.MaxQueriesPerWave, 1, 5);
        result.MaxConcurrency = Math.Clamp(result.MaxConcurrency, 1, 8);
        result.MaxWaves = Math.Clamp(result.MaxWaves, 1, 4);
        result.MaxTotalPages = Math.Clamp(result.MaxTotalPages, 1, 200);
        result.MaxContentChars = Math.Clamp(result.MaxContentChars, 500, 50_000);
        return result;
    }

    private static void AddWarning(ConcurrentQueue<string> warnings, string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            warnings.Enqueue(message);
        }
    }

    private sealed class WaveCounters
    {
        public int Hits;
        public int UniqueUrls;
        public int DuplicateUrls;
        public int Scraped;
        public int Failed;
        public int ContentDuplicates;
        public int SearchErrors;
    }
}
