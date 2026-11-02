namespace MultiWaveScraper.Core.Models;

/// <summary>Результат одного поискового запроса на уровне SERP.</summary>
public sealed record SearchHit
{
    public required string Url { get; init; }
    public string Title { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Provider { get; init; } = "";
    public int Rank { get; init; }
}

public sealed record ProviderStatus
{
    public required string Name { get; init; }
    public bool Enabled { get; init; }
    public bool Available { get; init; }
    public string Message { get; init; } = "";
    public long LatencyMs { get; init; }
}

public sealed record SearchResponse
{
    public required string Query { get; init; }
    public IReadOnlyList<SearchHit> Hits { get; init; } = [];
    public IReadOnlyList<string> ProvidersUsed { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public long DurationMs { get; init; }
}

public sealed record ScrapeProgress
{
    public required string Query { get; init; }
    public WaveType Wave { get; init; }
    public string Message { get; init; } = "";
    public int PagesCollected { get; init; }
    public int Percent { get; init; }
}

/// <summary>Результат работы LLM/правил по генерации ключей одной волны.</summary>
public sealed record ExpansionResult
{
    public IReadOnlyList<string> Queries { get; init; } = [];
    public string Source { get; init; } = "rules";
    public string Model { get; init; } = "";
    public string? Error { get; init; }
    public long DurationMs { get; init; }
}
