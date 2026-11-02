namespace MultiWaveScraper.Core.Models;

/// <summary>Запрос на многоволновой скрейпинг (REST / MCP / jobs).</summary>
public sealed class ScrapeRequest
{
    public string Query { get; set; } = "";
    public int? MaxPagesPerQuery { get; set; }
    public int? MaxWaves { get; set; }
    public int? MaxContentChars { get; set; }
    public bool? IncludeContent { get; set; }
    public bool? EnableLlmExpansion { get; set; }
    public bool? EnableSemanticScoring { get; set; }
    public bool? UseBrowserFallback { get; set; }
    public List<string>? Providers { get; set; }

    public ScrapeOptions ToOptions(ScrapeOptions defaults)
    {
        var options = defaults.Clone();
        if (MaxPagesPerQuery is > 0) options.MaxPagesPerQuery = MaxPagesPerQuery.Value;
        if (MaxWaves is > 0) options.MaxWaves = MaxWaves.Value;
        if (MaxContentChars is > 0) options.MaxContentChars = MaxContentChars.Value;
        if (IncludeContent.HasValue) options.IncludeContent = IncludeContent.Value;
        if (EnableLlmExpansion.HasValue) options.EnableLlmExpansion = EnableLlmExpansion.Value;
        if (EnableSemanticScoring.HasValue) options.EnableSemanticScoring = EnableSemanticScoring.Value;
        if (UseBrowserFallback.HasValue) options.UseBrowserFallback = UseBrowserFallback.Value;
        if (Providers is { Count: > 0 }) options.Providers = [.. Providers];
        return options;
    }
}
