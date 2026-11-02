using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ReverseMarkdown;

namespace MultiWaveScraper.Core.Extraction;

public sealed record ExtractedContent
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string Markdown { get; init; } = "";
    public int WordCount { get; init; }
    public bool LooksLikeSpa { get; init; }
    public int MainTextLength { get; init; }
}

/// <summary>
/// Очистка HTML и конвертация в компактный Markdown (plan2, шаг 4).
/// Удаляет шумовые узлы, выбирает блок с максимальной текстовой плотностью.
/// </summary>
public sealed class HtmlContentExtractor
{
    private const string NoiseSelectors =
        "script, style, noscript, svg, canvas, iframe, form, button, input, select, textarea, " +
        "nav, footer, header, aside, [role='navigation'], [role='banner'], [role='contentinfo'], " +
        "[aria-hidden='true'], .ad, .ads, .advert, .advertisement, .cookie, .cookies, .cookie-banner, " +
        ".newsletter, .subscribe, .share, .sharing, .social, .sidebar, .related, .comments, .comment, " +
        ".breadcrumb, .pagination, .menu, .navbar, .modal, .popup, #cookie, #onetrust-banner-sdk";

    private const string CandidateSelectors =
        "article, main, [role='main'], #content, #main, #article, .content, .post, .article, " +
        ".entry-content, .post-content, .markdown-body, .mw-parser-output, .article-body, .story-body";

    private readonly Converter _converter = CreateConverter();

    private static Converter CreateConverter()
    {
        var config = new Config { GithubFlavored = true };
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        return new Converter(config);
    }

    public async Task<ExtractedContent> ExtractAsync(string html, string url, int maxChars, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new ExtractedContent();
        }

        var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
        IDocument document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        string title = FirstNonEmpty(
            document.QuerySelector("meta[property='og:title']")?.GetAttribute("content"),
            document.Title,
            document.QuerySelector("h1")?.TextContent,
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url);

        string description = FirstNonEmpty(
            document.QuerySelector("meta[name='description']")?.GetAttribute("content"),
            document.QuerySelector("meta[property='og:description']")?.GetAttribute("content"),
            document.QuerySelector("meta[name='twitter:description']")?.GetAttribute("content"));

        string siteName = FirstNonEmpty(
            document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content"),
            Uri.TryCreate(url, UriKind.Absolute, out Uri? siteUri) ? siteUri.Host : "");

        bool hasSpaMarkers = html.Contains("__NEXT_DATA__", StringComparison.Ordinal)
                              || html.Contains("id=\"root\"", StringComparison.OrdinalIgnoreCase)
                              || html.Contains("id=\"app\"", StringComparison.OrdinalIgnoreCase)
                              || html.Contains("ng-version", StringComparison.OrdinalIgnoreCase)
                              || html.Contains("data-reactroot", StringComparison.OrdinalIgnoreCase);

        foreach (IElement element in document.QuerySelectorAll(NoiseSelectors))
        {
            element.Remove();
        }

        IElement? best = null;
        double bestScore = double.MinValue;
        foreach (IElement candidate in document.QuerySelectorAll(CandidateSelectors))
        {
            string text = candidate.TextContent;
            if (text.Length < 80)
            {
                continue;
            }

            int linkText = candidate.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
            double score = text.Length - (linkText * 1.2);
            if (candidate.LocalName is "article" or "main")
            {
                score += 250;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        IElement root = best ?? document.Body ?? document.DocumentElement;
        string rootText = TextTools.NormalizeWhitespace(root.TextContent);
        if (rootText.Length < 120 && document.Body is not null)
        {
            rootText = TextTools.NormalizeWhitespace(document.Body.TextContent);
        }

        string markdown = "";
        if (rootText.Length >= 40)
        {
            try
            {
                markdown = _converter.Convert(root.InnerHtml);
            }
            catch (Exception)
            {
                markdown = rootText;
            }
        }
        else
        {
            markdown = rootText;
        }

        markdown = TextTools.NormalizeMarkdown(markdown);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = TextTools.TruncateAtBoundary(markdown, 240);
        }

        if (maxChars > 0)
        {
            markdown = TextTools.TruncateAtBoundary(markdown, maxChars);
        }

        bool looksLikeSpa = (rootText.Length < 300 && hasSpaMarkers)
                            || (rootText.Length < 150 && document.QuerySelectorAll("script").Length > 4);

        return new ExtractedContent
        {
            Title = TextTools.NormalizeWhitespace(title),
            Description = TextTools.NormalizeWhitespace(description),
            SiteName = TextTools.NormalizeWhitespace(siteName),
            Markdown = markdown,
            WordCount = TextTools.CountWords(markdown),
            LooksLikeSpa = looksLikeSpa,
            MainTextLength = rootText.Length
        };
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}
