namespace MultiWaveScraper.Core.Search;

public sealed class SearchProviderException(string provider, string message, Exception? inner = null)
    : Exception($"[{provider}] {message}", inner)
{
    public string Provider { get; } = provider;
}

/// <summary>Один поисковый провайдер (SERP discovery, plan2 шаг 3).</summary>
public interface ISearchProvider
{
    string Name { get; }
    int Order { get; }

    /// <summary>Настроен ли провайдер (например, задан URL self-hosted SearXNG).</summary>
    bool IsConfigured => true;

    Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);
}

/// <summary>Маршрутизатор по провайдерам с fallback и дедупликацией.</summary>
public interface ISearchEngineClient
{
    Task<SearchResponse> SearchAsync(
        string query,
        int maxResults,
        IReadOnlyCollection<string>? providers,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderStatus>> GetProviderStatusAsync(CancellationToken cancellationToken);
}
