using System.Diagnostics;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MultiWaveScraper.Core.Search;

/// <summary>Bing (HTML) без API-ключа. Декодирует редиректы bing.com/ck/a.</summary>
public sealed class BingSearchProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<BingSearchProvider> logger) : ISearchProvider
{
    public string Name => "bing";
    public int Order => 30;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        string url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(maxResults, 1, 20)}&setlang=en";
        using HttpClient client = httpClientFactory.CreateClient("search");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ru;q=0.8");

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SearchProviderException(Name, $"HTTP {(int)response.StatusCode}");
        }

        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parser = new HtmlParser();
        IDocument document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var hits = new List<SearchHit>();
        foreach (IElement item in document.QuerySelectorAll("li.b_algo"))
        {
            IElement? link = item.QuerySelector("h2 a");
            string? href = link?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string snippet = item.QuerySelector(".b_caption p")?.TextContent
                             ?? item.QuerySelector("p")?.TextContent
                             ?? "";

            hits.Add(new SearchHit
            {
                Url = ResolveBingRedirect(href),
                Title = TextTools.NormalizeWhitespace(link?.TextContent ?? ""),
                Snippet = TextTools.NormalizeWhitespace(snippet),
                Provider = Name,
                Rank = hits.Count
            });

            if (hits.Count >= maxResults)
            {
                break;
            }
        }

        if (hits.Count == 0)
        {
            logger.LogDebug("Bing не вернул результатов (возможна captcha/consent-страница)");
            throw new SearchProviderException(Name, "Bing не вернул результатов (captcha/consent)");
        }

        return hits;
    }

    private static string ResolveBingRedirect(string href)
    {
        if (!href.Contains("bing.com/ck/a", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(href, UriKind.Absolute, out Uri? uri))
        {
            return href;
        }

        string? encoded = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Equals("u", StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[1])
            .FirstOrDefault();

        if (encoded is null || !encoded.StartsWith("a1", StringComparison.Ordinal))
        {
            return href;
        }

        try
        {
            string base64 = encoded[2..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return href;
        }
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
