namespace MultiWaveScraper.Core.Scraping;

/// <summary>
/// Уровень 1: быстрый HttpClient-загрузчик с лимитом размера,
/// ограничением частоты на хост и проверкой robots.txt.
/// </summary>
public sealed class HttpWebPageFetcher(
    IHttpClientFactory httpClientFactory,
    RobotsTxtCache robots,
    HostRateLimiter rateLimiter,
    IOptionsMonitor<ScraperOptions> options,
    ILogger<HttpWebPageFetcher> logger) : IWebPageFetcher
{
    public async Task<FetchedPage?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return new FetchedPage { RequestedUrl = url, Error = "Некорректный или неподдерживаемый URL" };
        }

        if (!await robots.IsAllowedAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            return new FetchedPage { RequestedUrl = url, Error = "Запрещено robots.txt", BlockedByRobots = true };
        }

        await rateLimiter.WaitAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        try
        {
            using HttpClient client = httpClientFactory.CreateClient("scraper");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            BrowserHeaders.Apply(request, options.CurrentValue);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            int status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new FetchedPage
                {
                    RequestedUrl = url,
                    FinalUrl = finalUrl,
                    StatusCode = status,
                    Error = $"HTTP {status} {response.ReasonPhrase}"
                };
            }

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(mediaType))
            {
                return new FetchedPage
                {
                    RequestedUrl = url,
                    FinalUrl = finalUrl,
                    StatusCode = status,
                    Error = $"Неподдерживаемый Content-Type: {mediaType}"
                };
            }

            string html = await ReadLimitedAsync(response, options.CurrentValue.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                return new FetchedPage { RequestedUrl = url, FinalUrl = finalUrl, StatusCode = status, Error = "Пустой ответ" };
            }

            return new FetchedPage
            {
                RequestedUrl = url,
                FinalUrl = finalUrl,
                StatusCode = status,
                Html = html,
                Via = "http"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Тайм-аут загрузки {Url}", url);
            return new FetchedPage { RequestedUrl = url, Error = "Тайм-аут загрузки" };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Ошибка загрузки {Url}", url);
            return new FetchedPage { RequestedUrl = url, Error = exception.Message };
        }
    }

    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: Math.Min(256 * 1024, Math.Max(0, maxBytes)));
        byte[] chunk = new byte[64 * 1024];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (total + read > maxBytes)
            {
                buffer.Write(chunk, 0, Math.Max(0, maxBytes - total));
                break;
            }

            buffer.Write(chunk, 0, read);
            total += read;
        }

        byte[] bytes = buffer.ToArray();
        string charset = response.Content.Headers.ContentType?.CharSet ?? "utf-8";
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        string text = encoding.GetString(bytes);
        if (!charset.Contains("utf", StringComparison.OrdinalIgnoreCase)
            && text.Contains("charset=utf-8", StringComparison.OrdinalIgnoreCase)
            && text.Contains('\uFFFD'))
        {
            text = Encoding.UTF8.GetString(bytes);
        }

        return text;
    }
}
