namespace MultiWaveScraper.Core.Expansion;

/// <summary>
/// Детерминированный fallback-генератор волн (работает без LLM):
/// альтернативы по шаблонам, синонимы по словарю, смежные темы по графу понятий
/// и верхним терминам найденных заголовков.
/// </summary>
public sealed class RuleBasedQueryExpander
{
    private readonly Dictionary<string, string[]> _phraseSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web scraping"] = ["web crawling", "data extraction", "html parsing", "scraping websites"],
        ["веб-скрапинг"] = ["парсинг сайтов", "сбор данных с сайтов", "извлечение данных из html", "веб-краулер"],
        ["парсинг"] = ["web scraping", "crawling", "data extraction"],
        ["vector database"] = ["vector store", "embedding database", "similarity search engine", "векторная база данных"],
        ["векторная база данных"] = ["vector database", "vector store", "поиск по эмбеддингам"],
        ["model context protocol"] = ["mcp server", "mcp tools", "model context protocol sdk"],
        ["mcp"] = ["model context protocol", "mcp server", "mcp client sdk"],
        ["large language model"] = ["llm", "языковая модель", "foundation model"],
        ["llm"] = ["large language model", "языковая модель", "нейросетевая модель"],
        ["agent framework"] = ["ai agent", "агентный фреймворк", "tool calling"],
        ["dotnet"] = [".net", "c#", "dotnet sdk"],
        [".net"] = ["dotnet", "c#", "aspnet core"],
        ["postgresql"] = ["postgres", "pgvector"],
        ["docker"] = ["container", "контейнеризация"],
        ["rag"] = ["retrieval augmented generation", "поиск с дополненной генерацией", "hybrid retrieval"]
    };

    private readonly Dictionary<string, string[]> _tokenSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scraping"] = ["crawling", "parsing"],
        ["crawling"] = ["scraping", "spider"],
        ["парсинг"] = ["scraping", "crawling"],
        ["database"] = ["db", "store"],
        ["база"] = ["database", "store"],
        ["search"] = ["retrieval", "поиск"],
        ["поиск"] = ["search", "retrieval"],
        ["ai"] = ["artificial intelligence", "ии"],
        ["ии"] = ["ai", "artificial intelligence"],
        ["best"] = ["top", "лучший"],
        ["лучший"] = ["best", "top"],
        ["tutorial"] = ["guide", "руководство"],
        ["guide"] = ["tutorial", "руководство"],
        ["fast"] = ["performance", "быстрый"],
        ["быстрый"] = ["fast", "performance"],
        ["agent"] = ["assistant", "агент"],
        ["агент"] = ["agent", "assistant"]
    };

    private readonly Dictionary<string, string[]> _relatedTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vector database"] =
        [
            "retrieval augmented generation", "embedding index hnsw", "semantic search architecture",
            "pgvector postgres", "vector similarity metrics"
        ],
        ["векторн"] =
        [
            "retrieval augmented generation", "hnsw индекс", "семантический поиск", "pgvector postgres"
        ],
        ["mcp"] =
        [
            "model context protocol server", "llm tool calling", "mcp client sdk", "ai agent integration"
        ],
        ["web scraping"] =
        [
            "headless browser automation", "anti bot techniques", "html parsing library", "crawler rate limiting"
        ],
        ["парсинг"] =
        [
            "headless браузер автоматизация", "обход антибота", "библиотека парсинга html", "rate limiting краулера"
        ],
        ["agent"] =
        [
            "multi agent orchestration", "agent memory", "llm tool use", "agent evaluation"
        ],
        ["rag"] =
        [
            "chunking strategies", "reranking", "hybrid search", "vector embeddings"
        ],
        ["dotnet"] =
        [
            "aspnet core minimal api", "c# async await", "dotnet performance", "net dependency injection"
        ],
        ["lm studio"] =
        [
            "local llm inference", "openai compatible api", "gguf quantization", "local embeddings"
        ]
    };

    private readonly ILogger<RuleBasedQueryExpander> _logger;
    private readonly Dictionary<string, string[]> _customPhraseSynonyms;

    public RuleBasedQueryExpander(
        IOptionsMonitor<ScraperOptions> options,
        ILogger<RuleBasedQueryExpander> logger)
    {
        _logger = logger;
        _customPhraseSynonyms = LoadSynonymsFile(options.CurrentValue.SynonymsFile);
    }

    public IReadOnlyList<string> Expand(
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxQueries)
    {
        string query = baseQuery.Trim();
        bool russian = TextTools.ContainsCyrillic(query);
        var results = new List<string>();

        switch (wave)
        {
            case WaveType.ExactMatch:
                return [query];

            case WaveType.Alternatives:
                results.AddRange(russian
                    ? new[]
                    {
                        $"{query} руководство",
                        $"как работает {query}",
                        $"{query} примеры",
                        $"{query} лучшие практики",
                        $"{query} сравнение"
                    }
                    : new[]
                    {
                        $"{query} tutorial",
                        $"how does {query} work",
                        $"{query} examples",
                        $"{query} best practices",
                        $"{query} comparison"
                    });
                break;

            case WaveType.Synonyms:
                results.AddRange(BuildSynonymQueries(query, russian));
                break;

            case WaveType.RelatedTopics:
                results.AddRange(BuildRelatedQueries(query, discoveredTitles, russian));
                break;
        }

        return Normalize(results, query, maxQueries);
    }

    private IEnumerable<string> BuildSynonymQueries(string query, bool russian)
    {
        var generated = new List<string>();
        foreach ((string phrase, string[] synonyms) in _phraseSynonyms.Concat(_customPhraseSynonyms))
        {
            if (!query.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string synonym in synonyms.Take(2))
            {
                generated.Add(ReplacePhrase(query, phrase, synonym));
            }
        }

        foreach (string token in TextTools.Tokenize(query, removeStopWords: false))
        {
            if (!_tokenSynonyms.TryGetValue(token, out string[]? synonyms))
            {
                continue;
            }

            foreach (string synonym in synonyms.Take(1))
            {
                generated.Add(Regex.Replace(
                    query,
                    $@"\b{Regex.Escape(token)}\b",
                    synonym,
                    RegexOptions.IgnoreCase));
            }
        }

        if (generated.Count == 0)
        {
            generated.Add(russian ? $"{query} определение" : $"{query} definition");
            generated.Add(russian ? $"{query} терминология" : $"{query} terminology");
        }

        return generated;
    }

    private IEnumerable<string> BuildRelatedQueries(
        string query,
        IReadOnlyList<string> discoveredTitles,
        bool russian)
    {
        var generated = new List<string>();
        foreach ((string topic, string[] related) in _relatedTopics)
        {
            if (query.Contains(topic, StringComparison.OrdinalIgnoreCase))
            {
                generated.AddRange(related.Take(3));
            }
        }

        IReadOnlyList<string> topTerms = TextTools.TopTerms(discoveredTitles, 5);
        foreach (string term in topTerms.Take(2))
        {
            generated.Add(russian ? $"{term} обзор" : $"{term} overview");
            generated.Add(russian ? $"{term} архитектура" : $"{term} architecture");
        }

        if (generated.Count == 0)
        {
            generated.Add(russian ? $"{query} смежные технологии" : $"{query} related technologies");
            generated.Add(russian ? $"{query} альтернативы" : $"{query} alternatives");
        }

        return generated;
    }

    private static string ReplacePhrase(string query, string phrase, string replacement)
        => Regex.Replace(query, Regex.Escape(phrase), replacement, RegexOptions.IgnoreCase);

    private static IReadOnlyList<string> Normalize(IEnumerable<string> queries, string baseQuery, int maxQueries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseQuery.Trim() };
        var result = new List<string>();
        foreach (string raw in queries)
        {
            string query = TextTools.NormalizeWhitespace(raw).Trim('"', '\'', ' ');
            if (query.Length < 3 || query.Length > 180 || !seen.Add(query))
            {
                continue;
            }

            result.Add(query);
            if (result.Count >= Math.Max(1, maxQueries))
            {
                break;
            }
        }

        return result;
    }

    private Dictionary<string, string[]> LoadSynonymsFile(string path)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return result;
        }

        try
        {
            string json = File.ReadAllText(path);
            Dictionary<string, string[]>? loaded = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (loaded is not null)
            {
                foreach ((string key, string[] value) in loaded)
                {
                    result[key] = value;
                }

                _logger.LogInformation("Загружено {Count} пользовательских синонимов из {Path}", result.Count, path);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось прочитать файл синонимов {Path}", path);
        }

        return result;
    }
}
