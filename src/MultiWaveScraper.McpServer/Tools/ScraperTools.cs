using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using MultiWaveScraper.Core.Models;
using MultiWaveScraper.Core.Expansion;
using MultiWaveScraper.Core.Infrastructure;
using MultiWaveScraper.Core.Jobs;
using MultiWaveScraper.Core.Scraping;
using MultiWaveScraper.Core.Search;

namespace MultiWaveScraper.McpServer.Tools;

public sealed record WaveQueryPreview
{
    public WaveType Wave { get; init; }
    public string WaveName { get; init; } = "";
    public IReadOnlyList<string> Queries { get; init; } = [];
    public string Source { get; init; } = "";
    public string Model { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// MCP-инструменты сервера. Все методы доступны и через MCP (stdio/HTTP),
/// и через REST-эндпоинты (см. RestEndpoints).
/// </summary>
[McpServerToolType]
public sealed class ScraperTools(
    ISearchEngineClient searchEngine,
    IQueryExpansionService expansion,
    IPageScraper pageScraper,
    ScrapeCoordinator coordinator,
    IScrapeJobService jobs,
    IOptionsMonitor<ScraperOptions> configuration,
    ILogger<ScraperTools> logger)
{
    [McpServerTool(Name = "multi_wave_scrape", Title = "Multi-wave web scraping", ReadOnly = true, OpenWorld = true, Idempotent = false)]
    [Description(
        "Выполняет многоволновой поиск и веб-скрейпинг по ключевому слову. " +
        "Волна 1 — точный ключ, волна 2 — альтернативные формулировки, волна 3 — синонимы, волна 4 — смежные темы. " +
        "Возвращает отчёт с запросами каждой волны, страницами (Markdown), оценкой релевантности и классификацией. " +
        "Может работать долго; для больших запусков используйте start_scrape_job.")]
    public async Task<ScrapeReport> MultiWaveScrapeAsync(
        [Description("Исходный поисковый ключ, например 'vector database' или 'MCP сервер'")] string searchKey,
        [Description("Максимум страниц на один сгенерированный запрос (1..10)")] int maxPagesPerQuery = 3,
        [Description("Количество волн: 1 — только точный ключ, 4 — все волны (1..4)")] int maxWaves = 4,
        [Description("Включать Markdown-контент страниц в ответ (true) или только метаданные (false)")] bool includeContent = true,
        [Description("Провайдеры через запятую: searxng,duckduckgo,bing,wikipedia,static. Пусто — из конфигурации")] string? providers = null,
        [Description("Использовать LLM (LM Studio) для генерации волн 2–4; false — только правила")] bool useLlm = true,
        [Description("Считать семантическую близость через embedding-модель")] bool semanticScoring = false,
        [Description("Включить headless-браузер (Playwright) для SPA-страниц")] bool useBrowserFallback = false,
        CancellationToken cancellationToken = default)
    {
        ScrapeOptions options = BuildOptions(
            maxPagesPerQuery, maxWaves, includeContent, providers, useLlm, semanticScoring, useBrowserFallback);

        logger.LogInformation("MCP multi_wave_scrape: {Query}", searchKey);
        return await coordinator.RunAsync(searchKey, options, progress: null, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "start_scrape_job", Title = "Start scrape job", ReadOnly = false, OpenWorld = true)]
    [Description("Запускает многоволновой скрейпинг в фоне и возвращает id задания. Опрашивайте get_scrape_job.")]
    public ScrapeJobSnapshot StartScrapeJob(
        [Description("Исходный поисковый ключ")] string searchKey,
        [Description("Максимум страниц на запрос (1..10)")] int maxPagesPerQuery = 3,
        [Description("Количество волн (1..4)")] int maxWaves = 4,
        [Description("Включать Markdown-контент в итоговый отчёт")] bool includeContent = true,
        [Description("Провайдеры через запятую; пусто — из конфигурации")] string? providers = null,
        [Description("Использовать LLM для волн 2–4")] bool useLlm = true)
    {
        var request = new ScrapeRequest
        {
            Query = searchKey,
            MaxPagesPerQuery = maxPagesPerQuery,
            MaxWaves = maxWaves,
            IncludeContent = includeContent,
            EnableLlmExpansion = useLlm,
            Providers = ParseProviders(providers)
        };

        return jobs.Start(request);
    }

    [McpServerTool(Name = "get_scrape_job", Title = "Get scrape job status", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description("Возвращает статус, прогресс и (опционально) полный отчёт фонового задания.")]
    public ScrapeJobSnapshot GetScrapeJob(
        [Description("id задания, полученный из start_scrape_job")] string jobId,
        [Description("Включить полный отчёт, если задание завершено")] bool includeReport = false)
    {
        return jobs.Get(jobId, includeReport)
               ?? throw new McpException($"Задание '{jobId}' не найдено.");
    }

    [McpServerTool(Name = "cancel_scrape_job", Title = "Cancel scrape job", ReadOnly = false, OpenWorld = false, Idempotent = true)]
    [Description("Отменяет фоновое задание по id.")]
    public object CancelScrapeJob([Description("id задания")] string jobId)
    {
        bool cancelled = jobs.Cancel(jobId);
        return new { jobId, cancelled };
    }

    [McpServerTool(Name = "list_scrape_providers", Title = "List search providers", ReadOnly = true, OpenWorld = true, Idempotent = true)]
    [Description("Проверяет доступность поисковых провайдеров (SearXNG, DuckDuckGo, Bing, Wikipedia, static).")]
    public Task<IReadOnlyList<ProviderStatus>> ListScrapeProvidersAsync(CancellationToken cancellationToken)
        => searchEngine.GetProviderStatusAsync(cancellationToken);

    [McpServerTool(Name = "discover_urls", Title = "Discover URLs", ReadOnly = true, OpenWorld = true, Idempotent = true)]
    [Description("Только поиск URL по ключу (SERP discovery), без скачивания страниц.")]
    public async Task<SearchResponse> DiscoverUrlsAsync(
        [Description("Поисковый ключ")] string searchKey,
        [Description("Максимум результатов (1..20)")] int maxResults = 10,
        [Description("Провайдеры через запятую; пусто — из конфигурации")] string? providers = null,
        CancellationToken cancellationToken = default)
    {
        return await searchEngine
            .SearchAsync(searchKey, Math.Clamp(maxResults, 1, 20), ParseProviders(providers), cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "generate_wave_queries", Title = "Generate wave queries", ReadOnly = true, OpenWorld = false, Idempotent = true)]
    [Description("Генерирует поисковые ключи для волн 2–4 (альтернативы, синонимы, смежные темы) без выполнения поиска.")]
    public async Task<IReadOnlyList<WaveQueryPreview>> GenerateWaveQueriesAsync(
        [Description("Исходный поисковый ключ")] string searchKey,
        [Description("Количество волн (2..4)")] int maxWaves = 4,
        [Description("Использовать LLM (LM Studio); false — только правила")] bool useLlm = true,
        CancellationToken cancellationToken = default)
    {
        ScraperOptions defaults = configuration.CurrentValue;
        var results = new List<WaveQueryPreview>();
        var titles = new List<string>();

        for (int number = 1; number <= Math.Clamp(maxWaves, 2, 4); number++)
        {
            var wave = (WaveType)number;
            ExpansionResult result = await expansion
                .GenerateAsync(searchKey, wave, titles, defaults.MaxQueriesPerWave, useLlm, cancellationToken)
                .ConfigureAwait(false);

            results.Add(new WaveQueryPreview
            {
                Wave = wave,
                WaveName = WaveNames.ShortName(wave),
                Queries = result.Queries,
                Source = result.Source,
                Model = result.Model,
                Error = result.Error
            });

            titles.AddRange(result.Queries);
        }

        return results;
    }

    [McpServerTool(Name = "scrape_single_url", Title = "Scrape single URL", ReadOnly = true, OpenWorld = true)]
    [Description("Скачивает одну страницу и возвращает очищенный Markdown с метаданными.")]
    public async Task<ScrapedPage> ScrapeSingleUrlAsync(
        [Description("Абсолютный URL страницы")] string url,
        [Description("Максимум символов Markdown")] int maxContentChars = 6000,
        [Description("Включить полный контент")] bool includeContent = true,
        CancellationToken cancellationToken = default)
    {
        ScrapeOptions options = BuildOptions(1, 1, includeContent, providers: null, useLlm: false,
            semanticScoring: false, useBrowserFallback: false);
        options.MaxContentChars = Math.Clamp(maxContentChars, 500, 50_000);

        ScrapedPage? page = await pageScraper
            .ScrapeAsync(new SearchHit { Url = UrlNormalizer.Normalize(url), Title = "" },
                WaveType.ExactMatch, url, options, cancellationToken)
            .ConfigureAwait(false);

        return page ?? throw new McpException($"Не удалось извлечь контент из '{url}' (проверьте URL, robots.txt или Content-Type).");
    }

    private ScrapeOptions BuildOptions(
        int maxPagesPerQuery,
        int maxWaves,
        bool includeContent,
        string? providers,
        bool useLlm,
        bool semanticScoring,
        bool useBrowserFallback)
    {
        ScrapeOptions options = configuration.CurrentValue.CreateDefaultOptions();
        options.MaxPagesPerQuery = Math.Clamp(maxPagesPerQuery, 1, 10);
        options.MaxWaves = Math.Clamp(maxWaves, 1, 4);
        options.IncludeContent = includeContent;
        options.EnableLlmExpansion = useLlm;
        options.EnableSemanticScoring = semanticScoring;
        options.UseBrowserFallback = useBrowserFallback;
        options.Providers = ParseProviders(providers);
        return options;
    }

    private static List<string>? ParseProviders(string? providers)
        => string.IsNullOrWhiteSpace(providers)
            ? null
            : providers.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
