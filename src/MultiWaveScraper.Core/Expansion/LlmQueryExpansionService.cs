using System.Diagnostics;
using Microsoft.Extensions.AI;
using MultiWaveScraper.Core.Llm;

namespace MultiWaveScraper.Core.Expansion;

/// <summary>Схема структурированного ответа LLM.</summary>
public sealed class QueryExpansionPayload
{
    public string[] Queries { get; set; } = [];
}

/// <summary>
/// Генерация ключей волн 2–4 через LM Studio (OpenAI-совместимый API)
/// со structured output (JSON Schema) и текстовым fallback.
/// </summary>
public sealed class LlmQueryExpansionService(
    IOptionsMonitor<LlmOptions> options,
    ILogger<LlmQueryExpansionService> logger)
{
    public async Task<ExpansionResult> ExpandAsync(
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxQueries,
        CancellationToken cancellationToken)
    {
        if (wave == WaveType.ExactMatch)
        {
            return new ExpansionResult { Queries = [baseQuery], Source = "exact", Model = "none" };
        }

        LlmOptions settings = options.CurrentValue;
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;

        var models = new[] { settings.EffectiveExpansionModel, settings.FallbackExpansionModel }
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string model in models)
        {
            try
            {
                IReadOnlyList<string> queries = await CallModelAsync(
                        model, settings, baseQuery, wave, discoveredTitles, maxQueries, cancellationToken)
                    .ConfigureAwait(false);

                if (queries.Count > 0)
                {
                    logger.LogInformation(
                        "LLM ({Model}) сгенерировал {Count} ключей для волны {Wave} за {Ms} мс",
                        model, queries.Count, wave, stopwatch.ElapsedMilliseconds);

                    return new ExpansionResult
                    {
                        Queries = queries,
                        Source = "llm",
                        Model = model,
                        DurationMs = stopwatch.ElapsedMilliseconds
                    };
                }

                lastError = $"Модель {model} вернула пустой список";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = $"Тайм-аут LLM ({model}, {settings.ExpansionTimeoutSeconds}s)";
                logger.LogWarning("Тайм-аут генерации волны {Wave} моделью {Model}", wave, model);
            }
            catch (Exception exception)
            {
                lastError = $"LLM ({model}): {exception.Message}";
                logger.LogWarning(exception, "Ошибка LLM-генерации волны {Wave} моделью {Model}", wave, model);
            }
        }

        return new ExpansionResult
        {
            Queries = [],
            Source = "llm-failed",
            Model = settings.EffectiveExpansionModel,
            Error = lastError ?? "LLM недоступна",
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<IReadOnlyList<string>> CallModelAsync(
        string model,
        LlmOptions settings,
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxQueries,
        CancellationToken cancellationToken)
    {
        IChatClient client = LmStudioClientFactory.CreateChatClient(settings, model);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.ExpansionTimeoutSeconds)));

        string systemPrompt = BuildSystemPrompt(wave, maxQueries);
        string userPrompt = BuildUserPrompt(baseQuery, wave, discoveredTitles, settings.MaxRelatedTitles);
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt)
        };

        var chatOptions = new ChatOptions
        {
            Temperature = settings.Temperature,
            MaxOutputTokens = Math.Max(200, settings.MaxOutputTokens),
            ResponseFormat = ChatResponseFormat.ForJsonSchema<QueryExpansionPayload>(
                schemaName: "query_expansion",
                schemaDescription: "Список поисковых запросов для одной волны расширения")
        };

        string? rawText = null;
        try
        {
            ChatResponse<QueryExpansionPayload> structured = await client
                .GetResponseAsync<QueryExpansionPayload>(messages, chatOptions, cancellationToken: cts.Token)
                .ConfigureAwait(false);

            rawText = structured.Text;
            IReadOnlyList<string> parsed = Normalize(structured.Result?.Queries, baseQuery, maxQueries);
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Structured output недоступен для {Model}, пробуем обычный текст", model);
        }

        // Fallback: обычный текстовый ответ и ручной разбор JSON.
        try
        {
            var plainOptions = new ChatOptions
            {
                Temperature = settings.Temperature,
                MaxOutputTokens = Math.Max(200, settings.MaxOutputTokens)
            };
            ChatResponse plain = await client
                .GetResponseAsync(messages, plainOptions, cts.Token)
                .ConfigureAwait(false);

            rawText = string.IsNullOrWhiteSpace(plain.Text) ? rawText : plain.Text;
            return Normalize(QueryTextParser.Parse(rawText), baseQuery, maxQueries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Текстовый fallback LLM не сработал");
        }

        return Normalize(QueryTextParser.Parse(rawText), baseQuery, maxQueries);
    }

    private static string BuildSystemPrompt(WaveType wave, int maxQueries) => wave switch
    {
        WaveType.Alternatives =>
            $"Ты — генератор поисковых запросов. Перефразируй запрос пользователя, сохраняя интент, но меняя структуру фразы. " +
            $"Верни ровно {maxQueries} варианта на том же языке, что и запрос. Только JSON по схеме, без пояснений.",
        WaveType.Synonyms =>
            $"Ты — генератор поисковых запросов. Замени ключевые слова на точные синонимы, профессиональные и кросс-языковые термины (например, RU+EN). " +
            $"Верни ровно {maxQueries} запроса на языке исходного запроса. Только JSON по схеме, без пояснений.",
        WaveType.RelatedTopics =>
            $"Ты — исследовательский ассистент. Сгенерируй {maxQueries} поисковых запроса по смежным, но не повторяющим исходный запрос темам " +
            $"(латеральное расширение: смежные технологии, проблемы, концепции). Учитывай заголовки уже найденных страниц. " +
            $"Только JSON по схеме, без пояснений.",
        _ => "Верни исходный запрос. Только JSON по схеме, без пояснений."
    };

    private static string BuildUserPrompt(
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxTitles)
    {
        string languageHint = TextTools.ContainsCyrillic(baseQuery)
            ? "Язык ответа: русский."
            : "Answer language: match the query language (English by default).";

        var builder = new StringBuilder();
        builder.AppendLine($"Исходный запрос: {baseQuery}");
        builder.AppendLine(languageHint);
        builder.AppendLine($"Тип расширения: {WaveNames.Describe(wave)}");
        builder.AppendLine("Не повторяй исходный запрос дословно. Запросы должны быть короткими (2–8 слов).");

        if (wave == WaveType.RelatedTopics && discoveredTitles.Count > 0)
        {
            builder.AppendLine("Заголовки уже найденных страниц:");
            foreach (string title in discoveredTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Take(maxTitles))
            {
                builder.AppendLine("- " + TextTools.TruncateAtBoundary(title, 120));
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? queries, string baseQuery, int maxQueries)
    {
        if (queries is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseQuery.Trim() };
        var result = new List<string>();
        foreach (string raw in queries)
        {
            string query = CleanQuery(raw);
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

    private static string CleanQuery(string raw)
    {
        string query = TextTools.NormalizeWhitespace(raw ?? "");
        query = Regex.Replace(query, @"^\s*(\d+[\.\)]|[-*•])\s*", "");
        return query.Trim('"', '\'', '`', ' ');
    }
}

/// <summary>Ручной разбор JSON-массива строк из ответа модели.</summary>
public static class QueryTextParser
{
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string value = text.Trim();
        int fence = value.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = value.IndexOf('\n', fence);
            int end = value.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                value = value[(start + 1)..end];
            }
        }

        foreach (string candidate in ExtractJsonCandidates(value))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                JsonElement root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                list.Add(s);
                            }
                        }
                    }

                    if (list.Count > 0)
                    {
                        return list;
                    }
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (string propertyName in new[] { "queries", "Queries", "items", "results" })
                    {
                        if (root.TryGetProperty(propertyName, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                        {
                            var list = array.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString() ?? "")
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();
                            if (list.Count > 0)
                            {
                                return list;
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Пробуем следующий фрагмент.
            }
        }

        // Последний шанс: строки в кавычках или нумерованный список.
        var matches = Regex.Matches(value, "\"([^\"]{3,160})\"")
            .Select(match => match.Groups[1].Value)
            .Where(s => !s.Contains("query", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 0)
        {
            return matches;
        }

        return value.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length is >= 3 and <= 160)
            .Where(line => !line.StartsWith('{') && !line.StartsWith('[') && !line.StartsWith("```"))
            .ToList();
    }

    private static IEnumerable<string> ExtractJsonCandidates(string text)
    {
        int arrayStart = text.IndexOf('[');
        int arrayEnd = text.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            yield return text[arrayStart..(arrayEnd + 1)];
        }

        int objectStart = text.IndexOf('{');
        int objectEnd = text.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            yield return text[objectStart..(objectEnd + 1)];
        }
    }
}
