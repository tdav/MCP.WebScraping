namespace MultiWaveScraper.Core.Models;

/// <summary>Одна успешно скачанная и очищенная страница.</summary>
public sealed record ScrapedPage
{
    public required string Url { get; init; }
    public string FinalUrl { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string MarkdownContent { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public int WordCount { get; init; }
    public int ContentChars { get; init; }
    public int StatusCode { get; init; }
    public string FetchedVia { get; init; } = "http";
    public WaveType DiscoveredInWave { get; init; } = WaveType.ExactMatch;
    public string MatchedQuery { get; init; } = "";
    public MatchKind MatchKind { get; init; }
    public double RelevanceScore { get; init; }
    public IReadOnlyList<string> MatchedTerms { get; init; } = [];
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Error { get; init; }

    /// <summary>Полный текст для оценки релевантности (не сериализуется в отчёт).</summary>
    [JsonIgnore]
    public string ScoredText { get; init; } = "";
}

public sealed record WaveStats
{
    public int HitsDiscovered { get; init; }
    public int UniqueUrls { get; init; }
    public int DuplicateUrlsSkipped { get; init; }
    public int PagesScraped { get; init; }
    public int PagesFailed { get; init; }
    public int ContentDuplicatesSkipped { get; init; }
    public int SearchErrors { get; init; }
}

public sealed record WaveResult
{
    public WaveType Wave { get; init; }
    public string WaveName => WaveNames.ShortName(Wave);
    public string WaveDescription => WaveNames.Describe(Wave);
    public IReadOnlyList<string> GeneratedQueries { get; init; } = [];
    public string ExpansionSource { get; init; } = "";
    public string ExpansionModel { get; init; } = "";
    public string? ExpansionError { get; init; }
    public IReadOnlyList<ScrapedPage> Pages { get; init; } = [];
    public WaveStats Stats { get; init; } = new();
    public long DurationMs { get; init; }
}

/// <summary>Компактная ссылка для классификации plan1 (три массива по типу совпадения).</summary>
public sealed record PageRef
{
    public required string Url { get; init; }
    public string Title { get; init; } = "";
    public string MatchedQuery { get; init; } = "";
    public WaveType DiscoveredInWave { get; init; }
    public MatchKind MatchKind { get; init; }
    public double RelevanceScore { get; init; }
    public int WordCount { get; init; }
}

public sealed record ScrapeClassification
{
    public IReadOnlyList<PageRef> Exact { get; init; } = [];
    public IReadOnlyList<PageRef> Alternatives { get; init; } = [];
    public IReadOnlyList<PageRef> Synonyms { get; init; } = [];
    public IReadOnlyList<PageRef> Related { get; init; } = [];
    public IReadOnlyList<PageRef> Unclassified { get; init; } = [];
}

/// <summary>Полный отчёт многоволнового скрейпинга.</summary>
public sealed record ScrapeReport
{
    public required string Id { get; init; }
    public required string Query { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public long DurationMs { get; init; }
    public IReadOnlyList<WaveResult> Waves { get; init; } = [];
    public ScrapeClassification Classification { get; init; } = new();
    public ScrapeOptions Options { get; init; } = new();
    public IReadOnlyList<string> ProvidersUsed { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string LlmModel { get; init; } = "";
    public string LlmSourceSummary { get; init; } = "";
    public int TotalPages => Waves.Sum(w => w.Pages.Count);
    public int TotalUniqueUrls => Waves.Sum(w => w.Stats.UniqueUrls);
    public string? ReportFile { get; init; }
}
