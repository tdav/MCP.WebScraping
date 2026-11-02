namespace MultiWaveScraper.Core.Scraping;

/// <summary>Потокобезопасный кэш robots.txt с простым парсером групп User-agent: *.</summary>
public sealed class RobotsTxtCache(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ScraperOptions> options,
    ILogger<RobotsTxtCache> logger)
{
    private sealed record Rule(string Path, bool Allow);

    private sealed class Rules
    {
        public List<Rule> Items { get; } = [];
        public DateTimeOffset LoadedAt { get; set; }

        public bool IsAllowed(string pathAndQuery)
        {
            Rule? match = Items
                .Where(rule => pathAndQuery.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(rule => rule.Path.Length)
                .ThenByDescending(rule => rule.Allow)
                .FirstOrDefault();

            return match?.Allow ?? true;
        }
    }

    private readonly ConcurrentDictionary<string, Rules> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.RespectRobotsTxt)
        {
            return true;
        }

        string origin = $"{uri.Scheme}://{uri.Authority}";
        if (!_cache.TryGetValue(origin, out Rules? rules) || DateTimeOffset.UtcNow - rules.LoadedAt > TimeSpan.FromMinutes(15))
        {
            rules = await LoadAsync(origin, cancellationToken).ConfigureAwait(false);
            _cache[origin] = rules;
        }

        string pathAndQuery = uri.PathAndQuery;
        bool allowed = rules.IsAllowed(pathAndQuery);
        if (!allowed)
        {
            logger.LogInformation("robots.txt запрещает {Url}", uri);
        }

        return allowed;
    }

    private async Task<Rules> LoadAsync(string origin, CancellationToken cancellationToken)
    {
        var rules = new Rules { LoadedAt = DateTimeOffset.UtcNow };
        try
        {
            using HttpClient client = httpClientFactory.CreateClient("search");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            string body = await client.GetStringAsync($"{origin}/robots.txt", cts.Token).ConfigureAwait(false);
            Parse(body, rules.Items);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "robots.txt недоступен для {Origin}; считаем, что всё разрешено", origin);
        }

        return rules;
    }

    private static void Parse(string body, List<Rule> target)
    {
        var agents = new List<string>();
        bool collecting = false;
        var buffered = new List<Rule>();

        void Flush()
        {
            if (agents.Contains("*"))
            {
                target.AddRange(buffered);
            }

            agents.Clear();
            buffered.Clear();
            collecting = false;
        }

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (collecting)
                {
                    Flush();
                }

                agents.Add(value);
                continue;
            }

            if (!key.Equals("Disallow", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (agents.Count == 0)
            {
                continue;
            }

            collecting = true;
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            string path = value.Split('#')[0].Trim();
            if (!path.StartsWith('/'))
            {
                continue;
            }

            if (path.Contains('*') || path.Contains('$'))
            {
                // Упрощение: wildcard-правила конвертируем в префикс до '*'.
                path = path.Split('*')[0];
            }

            buffered.Add(new Rule(path, key.Equals("Allow", StringComparison.OrdinalIgnoreCase)));
        }

        Flush();
    }
}
