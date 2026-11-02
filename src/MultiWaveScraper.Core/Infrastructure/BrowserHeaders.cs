using System.Text.RegularExpressions;

namespace MultiWaveScraper.Core.Infrastructure;

/// <summary>
/// Заголовки обычного браузера Chrome для HTTP-запросов и Playwright.
/// Cookie и дополнительные заголовки берутся из <see cref="ScraperOptions"/>.
/// </summary>
public static class BrowserHeaders
{
    public const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static string EffectiveUserAgent(ScraperOptions options)
        => string.IsNullOrWhiteSpace(options.UserAgent) ? ChromeUserAgent : options.UserAgent.Trim();

    public static string EffectiveAcceptLanguage(ScraperOptions options)
        => string.IsNullOrWhiteSpace(options.AcceptLanguage)
            ? "en-US,en;q=0.9,ru;q=0.8"
            : options.AcceptLanguage.Trim();

    /// <summary>Заполняет заголовки запроса значениями из конфигурации.</summary>
    public static void Apply(HttpRequestMessage request, ScraperOptions options)
    {
        string userAgent = EffectiveUserAgent(options);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", EffectiveAcceptLanguage(options));
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");

        ApplyChromeClientHints(request, userAgent);

        if (!string.IsNullOrWhiteSpace(options.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", options.CookieHeader.Trim());
        }

        foreach ((string name, string value) in options.ExtraHeaders)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>Заголовки по умолчанию для именованного HttpClient (без Cookie).</summary>
    public static void ApplyDefaults(HttpClient client, ScraperOptions options)
    {
        string userAgent = EffectiveUserAgent(options);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", EffectiveAcceptLanguage(options));
    }

    /// <summary>Заголовки по умолчанию для клиента, скачивающего целевые страницы.</summary>
    public static void ApplyScraperDefaults(HttpClient client, ScraperOptions options)
    {
        ApplyDefaults(client, options);
        if (!string.IsNullOrWhiteSpace(options.CookieHeader))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", options.CookieHeader.Trim());
        }

        foreach ((string name, string value) in options.ExtraHeaders)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }
    }

    public static IReadOnlyList<(string Name, string Value)> ParseCookieHeader(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return [];
        }

        var cookies = new List<(string, string)>();
        foreach (string pair in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string name = pair[..separator].Trim();
            string value = pair[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                cookies.Add((name, value));
            }
        }

        return cookies;
    }

    private static void ApplyChromeClientHints(HttpRequestMessage request, string userAgent)
    {
        Match match = Regex.Match(userAgent, @"Chrome/(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return;
        }

        string major = match.Groups[1].Value;
        request.Headers.TryAddWithoutValidation(
            "Sec-Ch-Ua",
            $"\"Chromium\";v=\"{major}\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"{major}\"");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
    }
}
