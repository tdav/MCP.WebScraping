using System.Diagnostics;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MultiWaveScraper.Core.Search;

/// <summary>
/// DuckDuckGo (HTML + Lite) без API-ключа.
/// Сначала пробуем html.duckduckgo.com, при anti-bot ответе — lite.duckduckgo.com.
/// </summary>
public sealed class DuckDuckGoSearchProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<DuckDuckGoSearchProvider> logger) : ISearchProvider
{
    public string Name => "duckduckgo";
    public int Order => 20;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        Exception? firstError = null;
        try
        {
            IReadOnlyList<SearchHit> hits = await SearchHtmlAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                return hits;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            firstError = exception;
            logger.LogDebug(exception, "DuckDuckGo HTML endpoint failed, trying Lite");
        }

        try
        {
            IReadOnlyList<SearchHit> hits = await SearchLiteAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                return hits;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new SearchProviderException(Name, $"HTML: {firstError?.Message ?? "нет результатов"}; Lite: {exception.Message}", exception);
        }

        if (firstError is not null)
        {
            throw new SearchProviderException(Name, firstError.Message, firstError);
        }

        throw new SearchProviderException(Name, "DuckDuckGo не вернул результатов (возможен anti-bot)");
    }

    private async Task<IReadOnlyList<SearchHit>> SearchHtmlAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("search");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = query,
            ["kl"] = "wt-wt"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/") { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ru;q=0.8");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 202)
        {
            throw new SearchProviderException(Name, "HTTP 202 — anti-bot challenge");
        }

        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parser = new HtmlParser();
        IDocument document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var hits = new List<SearchHit>();
        foreach (IElement result in document.QuerySelectorAll("div.result"))
        {
            IElement? link = result.QuerySelector("a.result__a");
            string? href = link?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string? snippet = result.QuerySelector("a.result__snippet")?.TextContent
                              ?? result.QuerySelector("div.result__snippet")?.TextContent;

            hits.Add(new SearchHit
            {
                Url = ResolveRedirect(href, "uddg"),
                Title = TextTools.NormalizeWhitespace(link?.TextContent ?? ""),
                Snippet = TextTools.NormalizeWhitespace(snippet ?? ""),
                Provider = Name,
                Rank = hits.Count
            });

            if (hits.Count >= maxResults)
            {
                break;
            }
        }

        return hits;
    }

    private async Task<IReadOnlyList<SearchHit>> SearchLiteAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("search");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://lite.duckduckgo.com/lite/") { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parser = new HtmlParser();
        IDocument document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var hits = new List<SearchHit>();
        foreach (IElement link in document.QuerySelectorAll("a.result-link"))
        {
            string? href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            IElement? row = link.Closest("tr");
            IElement? snippetCell = row?.QuerySelector("td.result-snippet");
            hits.Add(new SearchHit
            {
                Url = ResolveRedirect(href, "uddg"),
                Title = TextTools.NormalizeWhitespace(link.TextContent),
                Snippet = TextTools.NormalizeWhitespace(snippetCell?.TextContent ?? ""),
                Provider = Name,
                Rank = hits.Count
            });

            if (hits.Count >= maxResults)
            {
                break;
            }
        }

        return hits;
    }

    private static string ResolveRedirect(string href, string queryParameter)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (!href.Contains("duckduckgo.com/l/", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(href, UriKind.Absolute, out Uri? uri))
        {
            return href;
        }

        string? value = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Equals(queryParameter, StringComparison.OrdinalIgnoreCase))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(value) ? href : value;
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await SearchAsync("dotnet", 1, cancellationToken).ConfigureAwait(false);
            return new ProviderStatus { Name = Name, Enabled = true, Available = true, Message = "OK", LatencyMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception exception)
        {
            return new ProviderStatus { Name = Name, Enabled = true, Available = false, Message = exception.Message, LatencyMs = stopwatch.ElapsedMilliseconds };
        }
    }
}
