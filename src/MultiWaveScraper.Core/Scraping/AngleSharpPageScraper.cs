using MultiWaveScraper.Core.Extraction;

namespace MultiWaveScraper.Core.Scraping;

/// <summary>
/// Двухступенчатый скрейпер: HttpClient → (при необходимости) Playwright,
/// затем очистка DOM и конвертация в Markdown.
/// </summary>
public sealed class AngleSharpPageScraper(
    IWebPageFetcher httpFetcher,
    IBrowserPageFetcher browserFetcher,
    HtmlContentExtractor extractor,
    ILogger<AngleSharpPageScraper> logger) : IPageScraper
{
    public async Task<ScrapedPage?> ScrapeAsync(
        SearchHit hit,
        WaveType wave,
        string matchedQuery,
        ScrapeOptions options,
        CancellationToken cancellationToken)
    {
        FetchedPage? fetched = await httpFetcher.FetchAsync(hit.Url, cancellationToken).ConfigureAwait(false);
        ExtractedContent? extracted = null;

        if (fetched?.IsSuccessful == true)
        {
            extracted = await extractor
                .ExtractAsync(fetched.Html, fetched.FinalUrl, options.MaxContentChars, cancellationToken)
                .ConfigureAwait(false);
        }

        bool needsBrowser = fetched is null
                            || !fetched.IsSuccessful
                            || extracted is null
                            || extracted.LooksLikeSpa;

        if (options.UseBrowserFallback && needsBrowser)
        {
            FetchedPage? browserPage = await browserFetcher
                .FetchAsync(fetched?.FinalUrl ?? hit.Url, cancellationToken)
                .ConfigureAwait(false);

            if (browserPage?.IsSuccessful == true)
            {
                ExtractedContent browserExtracted = await extractor
                    .ExtractAsync(browserPage.Html, browserPage.FinalUrl, options.MaxContentChars, cancellationToken)
                    .ConfigureAwait(false);

                if (browserExtracted.MainTextLength > (extracted?.MainTextLength ?? 0))
                {
                    fetched = browserPage;
                    extracted = browserExtracted;
                }
            }
        }

        if (extracted is null && fetched is not null && !string.IsNullOrWhiteSpace(fetched.Error))
        {
            logger.LogDebug("Пропуск {Url}: {Error}", hit.Url, fetched.Error);
            return null;
        }

        if (extracted is null)
        {
            return null;
        }

        string title = extracted.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = hit.Title;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = Uri.TryCreate(hit.Url, UriKind.Absolute, out Uri? uri) ? uri.Host : hit.Url;
        }

        string markdown = extracted.Markdown;
        if (string.IsNullOrWhiteSpace(markdown) && string.IsNullOrWhiteSpace(extracted.Description) && string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string scoredText = $"{title}\n{extracted.Description}\n{markdown}";
        if (scoredText.Length > 30_000)
        {
            scoredText = scoredText[..30_000];
        }

        int fullChars = markdown.Length;
        string outputContent = options.IncludeContent
            ? markdown
            : TextTools.TruncateAtBoundary(markdown, 500);

        return new ScrapedPage
        {
            Url = UrlNormalizer.Normalize(hit.Url),
            FinalUrl = fetched?.FinalUrl ?? hit.Url,
            Title = title,
            Description = extracted.Description,
            SiteName = extracted.SiteName,
            MarkdownContent = outputContent,
            ContentHash = ContentFingerprint.Sha256(string.IsNullOrWhiteSpace(scoredText) ? hit.Url : scoredText),
            WordCount = extracted.WordCount,
            ContentChars = fullChars,
            StatusCode = fetched?.StatusCode ?? 0,
            FetchedVia = fetched?.Via ?? "http",
            DiscoveredInWave = wave,
            MatchedQuery = matchedQuery,
            FetchedAt = DateTimeOffset.UtcNow,
            ScoredText = scoredText
        };
    }
}
