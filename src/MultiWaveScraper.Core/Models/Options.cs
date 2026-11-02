namespace MultiWaveScraper.Core.Models;

/// <summary>Настройки всего пайплайна (секция "Scraper" в appsettings.json).</summary>
public sealed class ScraperOptions
{
    public const string SectionName = "Scraper";

    public int DefaultMaxPagesPerQuery { get; set; } = 3;
    public int MaxQueriesPerWave { get; set; } = 3;
    public int MaxConcurrency { get; set; } = 3;
    public int MaxWaves { get; set; } = 4;
    public int MaxTotalPages { get; set; } = 40;
    public int MaxContentChars { get; set; } = 6000;
    public bool IncludeContent { get; set; } = true;
    public bool EnableLlmExpansion { get; set; } = true;
    public bool EnableSemanticScoring { get; set; } = false;
    public bool RespectRobotsTxt { get; set; } = true;
    public bool UseBrowserFallback { get; set; } = false;
    public int PerHostDelayMs { get; set; } = 800;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int SearchTimeoutSeconds { get; set; } = 20;
    public int MaxResponseBytes { get; set; } = 4_000_000;
    public int MaxSearchResultsPerProvider { get; set; } = 8;
    public int WaveDelayMs { get; set; } = 0;
    public string UserAgent { get; set; } = BrowserHeaders.ChromeUserAgent;
    public string AcceptLanguage { get; set; } = "en-US,en;q=0.9,ru;q=0.8";

    /// <summary>
    /// Значение заголовка Cookie для скачиваемых страниц, например "session=...; consent=1".
    /// Применяется к scraper-клиенту и Playwright, но не к поисковым провайдерам.
    /// </summary>
    public string CookieHeader { get; set; } = "";

    /// <summary>Дополнительные/переопределяющие заголовки для скачиваемых страниц.</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];

    public string SelfBaseUrl { get; set; } = "http://127.0.0.1:5199";
    public string[] EnabledProviders { get; set; } = [];
    public string SearxngBaseUrl { get; set; } = "";
    public string WikipediaLanguage { get; set; } = "";
    public string[] StaticUrls { get; set; } = [];
    public string SynonymsFile { get; set; } = "";
    public bool PersistReports { get; set; } = true;
    public string ReportDirectory { get; set; } = "data/reports";

    public ScrapeOptions CreateDefaultOptions() => new()
    {
        MaxPagesPerQuery = DefaultMaxPagesPerQuery,
        MaxQueriesPerWave = MaxQueriesPerWave,
        MaxConcurrency = MaxConcurrency,
        MaxWaves = MaxWaves,
        MaxTotalPages = MaxTotalPages,
        MaxContentChars = MaxContentChars,
        IncludeContent = IncludeContent,
        EnableLlmExpansion = EnableLlmExpansion,
        EnableSemanticScoring = EnableSemanticScoring,
        RespectRobotsTxt = RespectRobotsTxt,
        UseBrowserFallback = UseBrowserFallback,
        Providers = null
    };
}

/// <summary>Параметры одного запуска (могут переопределять настройки по умолчанию).</summary>
public sealed class ScrapeOptions
{
    public int MaxPagesPerQuery { get; set; } = 3;
    public int MaxQueriesPerWave { get; set; } = 3;
    public int MaxConcurrency { get; set; } = 3;
    public int MaxWaves { get; set; } = 4;
    public int MaxTotalPages { get; set; } = 40;
    public int MaxContentChars { get; set; } = 6000;
    public bool IncludeContent { get; set; } = true;
    public bool EnableLlmExpansion { get; set; } = true;
    public bool EnableSemanticScoring { get; set; } = false;
    public bool RespectRobotsTxt { get; set; } = true;
    public bool UseBrowserFallback { get; set; } = false;

    /// <summary>Список провайдеров поиска; null — все включённые в конфигурации.</summary>
    public List<string>? Providers { get; set; }

    public int EffectiveWaves => Math.Clamp(MaxWaves, 1, 4);

    public ScrapeOptions Clone() => new()
    {
        MaxPagesPerQuery = MaxPagesPerQuery,
        MaxQueriesPerWave = MaxQueriesPerWave,
        MaxConcurrency = MaxConcurrency,
        MaxWaves = MaxWaves,
        MaxTotalPages = MaxTotalPages,
        MaxContentChars = MaxContentChars,
        IncludeContent = IncludeContent,
        EnableLlmExpansion = EnableLlmExpansion,
        EnableSemanticScoring = EnableSemanticScoring,
        RespectRobotsTxt = RespectRobotsTxt,
        UseBrowserFallback = UseBrowserFallback,
        Providers = Providers is null ? null : [.. Providers]
    };
}

/// <summary>Настройки LM Studio (секция "Llm").</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string BaseUrl { get; set; } = "http://127.0.0.1:21234/v1";
    public string Model { get; set; } = "lfm3-vl-2b";

    /// <summary>Модель для генерации волн. Пусто — используется <see cref="Model"/>.</summary>
    public string ExpansionModel { get; set; } = "";

    /// <summary>Резервная текстовая модель, если основная не справилась (пусто — не использовать).</summary>
    public string FallbackExpansionModel { get; set; } = "";

    public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
    public string ApiKey { get; set; } = "lm-studio";
    public int TimeoutSeconds { get; set; } = 90;
    public int ExpansionTimeoutSeconds { get; set; } = 45;
    public int MaxOutputTokens { get; set; } = 700;
    public float Temperature { get; set; } = 0.1f;
    public bool EnableLlmExpansion { get; set; } = true;
    public bool EnableEmbeddings { get; set; } = true;
    public int MaxRelatedTitles { get; set; } = 10;

    public string EffectiveExpansionModel => string.IsNullOrWhiteSpace(ExpansionModel) ? Model : ExpansionModel;
}
