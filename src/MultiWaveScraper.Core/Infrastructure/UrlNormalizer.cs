namespace MultiWaveScraper.Core.Infrastructure;

/// <summary>Нормализация и дедупликация URL (plan2, шаг 3).</summary>
public static class UrlNormalizer
{
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id", "utm_name",
        "fbclid", "gclid", "yclid", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid", "twclid",
        "ref", "referrer", "referer", "source", "spm", "_ga", "_gl", "vero_id", "oly_anon_id",
        "oly_enc_id", "trk", "trkCampaign", "sc_cid", "wickedid", "yclid"
    };

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".zip", ".rar", ".7z", ".tar", ".gz", ".exe", ".msi", ".dmg", ".iso",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".mp3", ".mp4", ".avi",
        ".mov", ".wav", ".woff", ".woff2", ".ttf", ".otf", ".css", ".js", ".json", ".xml",
        ".rss", ".atom", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    /// <summary>Возвращает канонический URL без трекинговых параметров, якоря и лишних слэшей.</summary>
    public static string Normalize(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return "";
        }

        rawUrl = rawUrl.Trim();
        if (rawUrl.StartsWith("//", StringComparison.Ordinal))
        {
            rawUrl = "https:" + rawUrl;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            return rawUrl;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Host = uri.Host.ToLowerInvariant(),
            Scheme = uri.Scheme.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };

        string query = string.Join("&", uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                string name = pair.Split('=', 2)[0];
                return !TrackingParameters.Contains(Uri.UnescapeDataString(name));
            }));

        builder.Query = query;
        string path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;

        return builder.Uri.ToString();
    }

    public static Uri? TryNormalizeUri(string rawUrl)
    {
        string normalized = Normalize(rawUrl);
        return Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    public static string GetHostKey(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host.ToLowerInvariant() : url;

    public static bool LooksLikeSkippableResource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return true;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return true;
        }

        string extension = Path.GetExtension(uri.AbsolutePath);
        return !string.IsNullOrEmpty(extension) && SkippedExtensions.Contains(extension);
    }

    /// <summary>Строит абсолютный URL относительно self-base (нужно для demo-страниц).</summary>
    public static string ToAbsolute(string url, string selfBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        if (url.StartsWith('/') && Uri.TryCreate(selfBaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return new Uri(baseUri, url).ToString();
        }

        return Normalize(url);
    }
}
