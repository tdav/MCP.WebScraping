using Microsoft.Extensions.Options;
using MultiWaveScraper.Core.Demo;
using MultiWaveScraper.Core.Jobs;
using MultiWaveScraper.Core.Models;
using MultiWaveScraper.Core.Search;

namespace MultiWaveScraper.McpServer.Api;

/// <summary>
/// REST-слой из plan1: /scrape, /waves, /urls, /providers, /jobs, /health.
/// Возвращает те же отчёты, что и MCP-инструменты.
/// </summary>
public static class RestEndpoints
{
    public static WebApplication MapRestEndpoints(this WebApplication app)
    {
        app.MapGet("/", (IConfiguration configuration) => Results.Json(new
        {
            name = "MultiWaveScraper MCP server",
            version = "1.0.0",
            description = "4-волновой веб-скрейпинг + MCP (stdio и Streamable HTTP) + Microsoft Agent Framework",
            endpoints = new
            {
                scrape = "/scrape?query=vector%20database&maxPagesPerQuery=3&maxWaves=4",
                scrapeWaves = "/waves?query=vector%20database&wave=2",
                urls = "/urls?query=vector%20database&maxResults=10",
                providers = "/providers",
                jobs = new { start = "POST /jobs", status = "GET /jobs/{id}?includeReport=true", cancel = "POST /jobs/{id}/cancel", list = "GET /jobs" },
                mcp = "/mcp (Streamable HTTP)",
                demo = "/demo"
            },
            llm = new
            {
                provider = "LM Studio",
                baseUrl = configuration["Llm:BaseUrl"],
                model = configuration["Llm:Model"]
            }
        }));

        app.MapGet("/health", async (
            ISearchEngineClient searchEngine,
            IOptionsMonitor<ScraperOptions> scraperOptions,
            IOptionsMonitor<LlmOptions> llmOptions,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<ProviderStatus> providers = await searchEngine
                .GetProviderStatusAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(new
            {
                status = "ok",
                time = DateTimeOffset.UtcNow,
                llm = new
                {
                    baseUrl = llmOptions.CurrentValue.BaseUrl,
                    model = llmOptions.CurrentValue.Model,
                    expansionModel = llmOptions.CurrentValue.EffectiveExpansionModel,
                    embeddings = llmOptions.CurrentValue.EnableEmbeddings
                },
                scraper = new
                {
                    waves = scraperOptions.CurrentValue.MaxWaves,
                    maxPagesPerQuery = scraperOptions.CurrentValue.DefaultMaxPagesPerQuery,
                    providers = scraperOptions.CurrentValue.EnabledProviders,
                    robots = scraperOptions.CurrentValue.RespectRobotsTxt,
                    browserFallback = scraperOptions.CurrentValue.UseBrowserFallback
                },
                searchProviders = providers
            });
        });

        app.MapGet("/providers", async (ISearchEngineClient searchEngine, CancellationToken cancellationToken) =>
            Results.Ok(await searchEngine.GetProviderStatusAsync(cancellationToken).ConfigureAwait(false)));

        app.MapGet("/scrape", async (
            string query,
            int? maxPagesPerQuery,
            int? maxWaves,
            int? maxContentChars,
            bool? includeContent,
            bool? useLlm,
            bool? semantic,
            string? providers,
            string? provider,
            ScrapeCoordinator coordinator,
            IOptionsMonitor<ScraperOptions> configuration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Results.BadRequest(new { error = "Параметр query обязателен." });
            }

            ScrapeOptions options = configuration.CurrentValue.CreateDefaultOptions();
            if (maxPagesPerQuery is > 0) options.MaxPagesPerQuery = Math.Clamp(maxPagesPerQuery.Value, 1, 10);
            if (maxWaves is > 0) options.MaxWaves = Math.Clamp(maxWaves.Value, 1, 4);
            if (maxContentChars is > 0) options.MaxContentChars = Math.Clamp(maxContentChars.Value, 500, 50_000);
            if (includeContent.HasValue) options.IncludeContent = includeContent.Value;
            if (useLlm.HasValue) options.EnableLlmExpansion = useLlm.Value;
            if (semantic.HasValue) options.EnableSemanticScoring = semantic.Value;
            options.Providers = ParseProviders(providers ?? provider);

            ScrapeReport report = await coordinator
                .RunAsync(query, options, progress: null, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                report.Query,
                report.Id,
                report.StartedAt,
                report.CompletedAt,
                report.DurationMs,
                waves = new
                {
                    wave1 = report.Waves.FirstOrDefault(w => w.Wave == WaveType.ExactMatch)?.Pages ?? [],
                    wave2 = report.Waves.FirstOrDefault(w => w.Wave == WaveType.Alternatives)?.Pages ?? [],
                    wave3 = report.Waves.FirstOrDefault(w => w.Wave == WaveType.Synonyms)?.Pages ?? [],
                    wave4 = report.Waves.FirstOrDefault(w => w.Wave == WaveType.RelatedTopics)?.Pages ?? []
                },
                waveQueries = report.Waves.ToDictionary(
                    w => w.WaveName,
                    w => w.GeneratedQueries,
                    StringComparer.OrdinalIgnoreCase),
                report.Classification,
                report.ProvidersUsed,
                report.Warnings,
                report.LlmModel,
                report.LlmSourceSummary,
                report.ReportFile,
                report.TotalPages
            });
        });

        app.MapGet("/waves", async (
            string query,
            string wave,
            int? maxPagesPerQuery,
            string? providers,
            ScrapeCoordinator coordinator,
            IOptionsMonitor<ScraperOptions> configuration,
            CancellationToken cancellationToken) =>
        {
            if (!WaveNames.TryParse(wave, out WaveType targetWave))
            {
                return Results.BadRequest(new { error = "Параметр wave должен быть 1..4 или именем волны." });
            }

            ScrapeOptions options = configuration.CurrentValue.CreateDefaultOptions();
            options.MaxWaves = (int)targetWave;
            if (maxPagesPerQuery is > 0) options.MaxPagesPerQuery = Math.Clamp(maxPagesPerQuery.Value, 1, 10);
            options.Providers = ParseProviders(providers);

            ScrapeReport report = await coordinator
                .RunAsync(query, options, progress: null, cancellationToken)
                .ConfigureAwait(false);

            WaveResult? result = report.Waves.FirstOrDefault(w => w.Wave == targetWave);
            return result is null
                ? Results.NotFound(new { error = $"Волна {wave} отсутствует в отчёте", report.Warnings })
                : Results.Ok(result);
        });

        app.MapGet("/urls", async (
            string query,
            int? maxResults,
            string? providers,
            ISearchEngineClient searchEngine,
            CancellationToken cancellationToken) =>
        {
            SearchResponse response = await searchEngine
                .SearchAsync(query, Math.Clamp(maxResults ?? 10, 1, 30), ParseProviders(providers), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(response);
        });

        app.MapPost("/jobs", (ScrapeRequest request, IScrapeJobService jobs) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest(new { error = "Параметр query обязателен." });
            }

            return Results.Ok(jobs.Start(request));
        });

        app.MapGet("/jobs", (IScrapeJobService jobs) => Results.Ok(jobs.List()));

        app.MapGet("/jobs/{id}", (string id, bool? includeReport, IScrapeJobService jobs) =>
        {
            ScrapeJobSnapshot? snapshot = jobs.Get(id, includeReport ?? false);
            return snapshot is null ? Results.NotFound(new { error = $"Задание '{id}' не найдено" }) : Results.Ok(snapshot);
        });

        app.MapPost("/jobs/{id}/cancel", (string id, IScrapeJobService jobs) =>
            jobs.Cancel(id)
                ? Results.Ok(new { id, cancelled = true })
                : Results.NotFound(new { error = $"Задание '{id}' не найдено" }));

        return app;
    }

    public static WebApplication MapDemoEndpoints(this WebApplication app)
    {
        app.MapGet("/demo", () => Results.Ok(DemoPageCatalog.Pages.Select(page => new
        {
            page.Slug,
            page.Title,
            page.Description,
            url = $"/demo/{page.Slug}"
        })));

        app.MapGet("/demo/{slug}", (string slug) =>
        {
            DemoPage? page = DemoPageCatalog.Find(slug);
            return page is null
                ? Results.NotFound(new { error = $"Demo-страница '{slug}' не найдена" })
                : Results.Content(DemoPageCatalog.RenderHtml(page), "text/html; charset=utf-8");
        });

        return app;
    }

    private static List<string>? ParseProviders(string? providers)
        => string.IsNullOrWhiteSpace(providers)
            ? null
            : providers.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
