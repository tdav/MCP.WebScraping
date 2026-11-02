using Microsoft.Playwright;

namespace MultiWaveScraper.Core.Scraping;

/// <summary>
/// Уровень 2: headless Chromium через Playwright для SPA и страниц с 403/антиботом.
/// По умолчанию выключен (Scraper:UseBrowserFallback = false), т.к. требует
/// установленных браузеров: <c>pwsh bin/Debug/net11.0/playwright.ps1 install chromium</c>.
/// </summary>
public sealed class PlaywrightPageFetcher(
    IOptionsMonitor<ScraperOptions> options,
    ILogger<PlaywrightPageFetcher> logger) : IBrowserPageFetcher, IAsyncDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disabled;

    public async Task<FetchedPage?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.UseBrowserFallback || _disabled)
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            IBrowser browser = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);

            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = BrowserHeaders.EffectiveUserAgent(options.CurrentValue),
                Locale = "en-US"
            };

            var extraHeaders = new Dictionary<string, string>(
                options.CurrentValue.ExtraHeaders,
                StringComparer.OrdinalIgnoreCase);
            extraHeaders.Remove("Cookie");
            extraHeaders.Remove("User-Agent");
            if (extraHeaders.Count > 0)
            {
                contextOptions.ExtraHTTPHeaders = extraHeaders;
            }

            await using IBrowserContext context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);

            var cookies = BrowserHeaders.ParseCookieHeader(options.CurrentValue.CookieHeader)
                .Select(cookie => new Microsoft.Playwright.Cookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Url = $"{uri.Scheme}://{uri.Authority}"
                })
                .ToList();
            if (cookies.Count > 0)
            {
                await context.AddCookiesAsync(cookies).ConfigureAwait(false);
            }

            IPage page = await context.NewPageAsync().ConfigureAwait(false);
            IResponse? response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            }).ConfigureAwait(false);

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            string html = await page.ContentAsync().ConfigureAwait(false);
            string finalUrl = page.Url;

            return new FetchedPage
            {
                RequestedUrl = url,
                FinalUrl = finalUrl,
                StatusCode = response?.Status ?? 200,
                Html = html,
                Via = "playwright"
            };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Playwright fallback не сработал для {Url}", url);
            return new FetchedPage
            {
                RequestedUrl = url,
                Error = "Playwright: " + exception.Message
            };
        }
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return _browser;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                return _browser;
            }

            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-blink-features=AutomationControlled"]
            }).ConfigureAwait(false);

            return _browser;
        }
        catch (Exception exception)
        {
            _disabled = true;
            logger.LogWarning(
                exception,
                "Браузер Chromium недоступен. Установите его: bin/Debug/net11.0/playwright.ps1 install chromium");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();
        _initLock.Dispose();
    }
}
