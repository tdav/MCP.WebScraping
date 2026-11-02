using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using MultiWaveScraper.Core.Expansion;
using MultiWaveScraper.Core.Extraction;
using MultiWaveScraper.Core.Jobs;
using MultiWaveScraper.Core.Llm;
using MultiWaveScraper.Core.Scraping;
using MultiWaveScraper.Core.Search;
using MultiWaveScraper.Core.Waves;

namespace MultiWaveScraper.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Регистрация всего пайплайна: поиск, скрапинг, LLM, волны, задания.</summary>
    public static IServiceCollection AddMultiWaveScraper(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScraperOptions>()
            .Bind(configuration.GetSection(ScraperOptions.SectionName))
            .Validate(options => options.MaxConcurrency is >= 1 and <= 8, "Scraper:MaxConcurrency должен быть 1..8")
            .ValidateOnStart();

        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Llm:BaseUrl обязателен")
            .ValidateOnStart();

        services.AddHttpClient("search", (sp, client) =>
        {
            ScraperOptions options = sp.GetRequiredService<IOptionsMonitor<ScraperOptions>>().CurrentValue;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(15, options.SearchTimeoutSeconds + 10));
            BrowserHeaders.ApplyDefaults(client, options);
        });

        services.AddHttpClient("scraper", (sp, client) =>
        {
            ScraperOptions options = sp.GetRequiredService<IOptionsMonitor<ScraperOptions>>().CurrentValue;
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = options.MaxResponseBytes;
            BrowserHeaders.ApplyScraperDefaults(client, options);
        })
        .AddStandardResilienceHandler(resilience =>
        {
            resilience.Retry.MaxRetryAttempts = 2;
            resilience.Retry.Delay = TimeSpan.FromMilliseconds(400);
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(45);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(180);
        });

        services.AddSingleton<HostRateLimiter>();
        services.AddSingleton<RobotsTxtCache>();
        services.AddSingleton<HtmlContentExtractor>();
        services.AddSingleton<IWebPageFetcher, HttpWebPageFetcher>();
        services.AddSingleton<IBrowserPageFetcher, PlaywrightPageFetcher>();
        services.AddSingleton<IPageScraper, AngleSharpPageScraper>();

        services.AddSingleton<ISearchProvider, SearxngSearchProvider>();
        services.AddSingleton<ISearchProvider, DuckDuckGoSearchProvider>();
        services.AddSingleton<ISearchProvider, BingSearchProvider>();
        services.AddSingleton<ISearchProvider, WikipediaSearchProvider>();
        services.AddSingleton<ISearchProvider, StaticUrlSearchProvider>();
        services.AddSingleton<ISearchEngineClient, SearchEngineClient>();

        services.AddSingleton<RuleBasedQueryExpander>();
        services.AddSingleton<LlmQueryExpansionService>();
        services.AddSingleton<IQueryExpansionService, QueryExpansionService>();

        services.AddSingleton<ISemanticSimilarityService>(sp =>
        {
            ILogger<SemanticSimilarityService> logger = sp.GetRequiredService<ILogger<SemanticSimilarityService>>();
            LlmOptions llm = sp.GetRequiredService<IOptionsMonitor<LlmOptions>>().CurrentValue;
            if (!llm.EnableEmbeddings)
            {
                return new NullSemanticSimilarityService();
            }

            try
            {
                IEmbeddingGenerator<string, Embedding<float>> generator = LmStudioClientFactory.CreateEmbeddingGenerator(llm);
                return new SemanticSimilarityService(generator, logger);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Embedding-модель недоступна; семантическая оценка отключена");
                return new NullSemanticSimilarityService();
            }
        });

        services.AddSingleton<MultiWaveScrapeEngine>();
        services.AddSingleton<IReportStore, FileReportStore>();
        services.AddSingleton<ScrapeCoordinator>();
        services.AddSingleton<ScrapeJobService>();
        services.AddSingleton<IScrapeJobService>(sp => sp.GetRequiredService<ScrapeJobService>());

        return services;
    }
}
