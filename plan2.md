Архитектура многоволнового MCP-сервера для веб-скрейпинга на платформе **.NET 11** строится вокруг конвейера расширения поисковых запросов (Query Expansion Pipeline), модуля поиска URL (SERP Discovery), асинхронного парсера и слоя дедупликации.

---

### Шаг 1. Инициализация проекта и стек технологий (.NET 11)

1. **Создание проекта:**
Используется шаблон Worker Service или Web API (при работе через HTTP/SSE транспорт) с целевым фреймворком `net11.0`.
2. **Необходимые NuGet-пакеты:**
* `ModelContextProtocol` (версия 2.3.0+) и `ModelContextProtocol.AspNetCore` — официальный C# SDK для реализации MCP-сервера.
* `ModelContextProtocol.Extensions.Tasks` — расширение для выполнения длительных асинхронных задач с поллингом статуса (необходимо, так как 4 волны скрейпинга занимают много времени).
* `Microsoft.Extensions.AI` — единая абстракция (`IChatClient`) для подключения вашей локальной или внутренней LLM (через Ollama, vLLM, llama.cpp), которая будет генерировать ключи для волн 2–4.
* `AngleSharp` и `ReverseMarkdown` — быстрый парсинг DOM-дерева и перевод очищенного HTML в компактный Markdown.
* `Microsoft.Playwright` — headless-браузер для обхода SPA-сайтов с клиентским рендерингом (используется как fallback).
* `Microsoft.Extensions.Http.Resilience` — политики повторных попыток (Retry), Тайм-ауты и Circuit Breaker.



---

### Шаг 2. Проектирование генератора волн (Query Expansion Engine)

Внутренняя модель через `IChatClient` вызывается со строгой типизацией ответа (Structured Output / JSON Schema) для формирования поисковых ключей на каждом этапе:

| Волна | Тип расширения | Задача внутренней модели | Источник контекста |
| --- | --- | --- | --- |
| **Волна 1** | **Точный ключ (Exact)** | Без участия LLM (или базовая нормализация строки). | Исходный запрос пользователя. |
| **Волна 2** | **Альтернативы (Alternatives)** | Перефразирование запроса (3–5 вариантов) с сохранением исходного интента, но другой структурой фразы. | Исходный ключ. |
| **Волна 3** | **Синонимы (Synonyms)** | Замена ключевых слов на узкопрофессиональные, лексические и кросс-языковые синонимы (например, RU + EN термины). | Исходный ключ + словарь терминов из Волны 1. |
| **Волна 4** | **Похожие темы (Related Topics)** | Латеральное расширение: генерация запросов по смежным технологиям, проблемам или концепциям. | Заголовки (`<title>`, `<h1>`) и сниппеты, собранные на волнах 1–3. |

---

### Шаг 3. Модуль поиска (SERP Discovery) и дедупликации

Чтобы скрейпить сайты по ключу, сервер сначала должен преобразовать текстовые ключи каждой волны в список целевых URL:

1. **Получение списка URL:**
* Интеграция с локальным инстансом **SearXNG** (через JSON API) — оптимальный self-hosted вариант без лимитов, агрегирующий выдачу Google, Bing, DuckDuckGo.
* Альтернатива: прямое обращение к API поисковиков (Brave Search API, Google Custom Search) или парсинг HTML-выдачи DuckDuckGo Lite.


2. **Нормализация и дедупликация (URL & Content Filter):**
* Очистка URL от трекинговых параметров (`utm_*`, `fbclid`, `yclid`) и якорей (`#section`).
* Потокобезопасный реестр `ConcurrentDictionary<string, byte>` для хранения уже обработанных URL: если Волна 3 (синонимы) выдает те же ссылки, что и Волна 1, они мгновенно пропускаются.
* Вычисление `SHA256` (или `SimHash`) от очищенного текста страницы для отсечения зеркал и дубликатов контента на разных доменах.



---

### Шаг 4. Движок Web Scraping и очистки данных

1. **Двухступенчатое скачивание:**
* **Уровень 1 (Быстрый):** `HttpClient` со встроенной в .NET 11 поддержкой сжатия `Zstandard` (`System.IO.Compression`) и быстрым разрешением имен через `DnsResolver`.
* **Уровень 2 (Headless Browser):** Если ответ вернул пустой DOM (признак React/Vue/Angular SPA) или код `403`, запрос передается в пул контекстов `Playwright`.


2. **Очистка контента (Sanitization):**
* Удаление шумовых узлов: `<script>`, `<style>`, `<noscript>`, `<svg>`, `<nav>`, `<footer>`, `<header>`, `<aside>`, рекламных блоков.
* Извлечение основного текста из тегов `<article>`, `<main>` или блока с максимальной плотностью текста.
* Конвертация результата в Markdown и обрезка по лимиту токенов.



---

### Шаг 5. Архитектура кода и регистрация MCP-инструмента (C# / .NET 11)

Ниже представлена структура классов и MCP-инструмента (без использования подчеркиваний в именах полей и свойств):

```csharp
using System.Collections.Concurrent;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace MultiWaveScraperMcp;

public enum WaveType
{
    ExactMatch = 1,
    Alternatives = 2,
    Synonyms = 3,
    RelatedTopics = 4
}

public sealed record ScrapedPage(
    string Url,
    string Title,
    string MarkdownContent,
    WaveType DiscoveredInWave,
    string MatchedQuery);

public sealed record WaveResult(
    WaveType Wave,
    IReadOnlyList<string> GeneratedQueries,
    IReadOnlyList<ScrapedPage> Pages);

public sealed class QueryExpansionService(IChatClient chatClient)
{
    public async Task<IReadOnlyList<string>> GenerateQueriesAsync(
        string baseQuery,
        WaveType waveType,
        IEnumerable<string> discoveredTitles,
        CancellationToken cancellationToken)
    {
        if (waveType == WaveType.ExactMatch)
        {
            return [baseQuery];
        }

        string systemPrompt = waveType switch
        {
            WaveType.Alternatives => 
                "Сгенерируй 3 альтернативные поисковые формулировки для запроса. Верни только JSON-массив строк.",
            WaveType.Synonyms => 
                "Сгенерируй 3 поисковых запроса, заменив ключевые слова на точные синонимы и профессиональные термины. Верни только JSON-массив строк.",
            WaveType.RelatedTopics => 
                $"На основе запроса и найденных тем ({string.Join(", ", discoveredTitles.Take(10))}) сгенерируй 3 запроса по смежным темам. Верни только JSON-массив строк.",
            _ => throw new ArgumentOutOfRangeException(nameof(waveType))
        };

        var response = await chatClient.GetResponseAsync<string[]>(
            [
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, baseQuery)
            ],
            cancellationToken: cancellationToken);

        return response.Result ?? [];
    }
}

[McpServerToolType]
public sealed class MultiWaveScraperTool(
    QueryExpansionService expansionService,
    ISearchEngineClient searchClient,
    IWebPageScraper pageScraper)
{
    [McpServerTool(Name = "multi_wave_scrape")]
    [Description("Выполняет многоволновой поиск и веб-скрейпинг по ключевому слову (4 волны: точный ключ, альтернативы, синонимы, похожие темы).")]
    public async Task<IReadOnlyList<WaveResult>> ExecuteMultiWaveScrapeAsync(
        [Description("Исходный поисковый ключ")] string searchKey,
        [Description("Максимальное число страниц на один запрос")] int maxPagesPerQuery = 3,
        CancellationToken cancellationToken = default)
    {
        var visitedUrls = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var collectedTitles = new ConcurrentBag<string>();
        var allWaveResults = new List<WaveResult>(capacity: 4);

        WaveType[] waves = 
        [
            WaveType.ExactMatch, 
            WaveType.Alternatives, 
            WaveType.Synonyms, 
            WaveType.RelatedTopics
        ];

        foreach (WaveType currentWave in waves)
        {
            IReadOnlyList<string> queries = await expansionService.GenerateQueriesAsync(
                searchKey, 
                currentWave, 
                collectedTitles, 
                cancellationToken);

            var wavePages = new ConcurrentBag<ScrapedPage>();

            await Parallel.ForEachAsync(queries, new ParallelOptions
            {
                MaxDegreeOfParallelism = 3,
                CancellationToken = cancellationToken
            }, async (query, ct) =>
            {
                IReadOnlyList<string> urls = await searchClient.SearchUrlsAsync(query, maxPagesPerQuery, ct);

                foreach (string url in urls)
                {
                    string normalizedUrl = NormalizeUrl(url);
                    if (!visitedUrls.TryAdd(normalizedUrl, 0))
                    {
                        continue; // Пропуск дубликатов из предыдущих волн
                    }

                    ScrapedPage? page = await pageScraper.ScrapeAsync(normalizedUrl, currentWave, query, ct);
                    if (page is not null)
                    {
                        wavePages.Add(page);
                        collectedTitles.Add(page.Title);
                    }
                }
            });

            allWaveResults.Add(new WaveResult(currentWave, queries, wavePages.ToList()));
        }

        return allWaveResults;
    }

    private static string NormalizeUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri))
        {
            return rawUrl.Trim();
        }
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".TrimEnd('/');
    }
}

```

---

### Шаг 6. Регистрация в `Program.cs` и управление нагрузкой

1. **Настройка DI и транспорта MCP:**
```csharp
var builder = Host.CreateApplicationBuilder(args);

// Подключение локальной модели (например, Ollama) для генерации волн
builder.Services.AddChatClient(new OllamaChatClient(new Uri("http://localhost:11434"), "qwen2.5:14b"));

builder.Services.AddSingleton<QueryExpansionService>();
builder.Services.AddHttpClient<ISearchEngineClient, SearxngSearchClient>();
builder.Services.AddHttpClient<IWebPageScraper, AngleSharpPageScraper>()
    .AddStandardResilienceHandler();

// Регистрация MCP-сервера
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

```


2. **Контроль лимитов и тайм-аутов:**
* **Ограничение на домен (Per-Host Rate Limiting):** Внутри `AngleSharpPageScraper` использовать `PartitionedRateLimiter` по `Uri.Host` (не более 1–2 запросов в секунду на один сайт), чтобы избежать блокировок `429 Too Many Requests`.
* **Ограничение объема ответа:** Для предотвращения переполнения контекстного окна LLM-клиента, возвращаемый `MarkdownContent` каждой страницы рекомендуется лимитировать (например, до 4000–6000 символов наиболее релевантных абзацев) или сохранять полные дампы в БД (PostgreSQL + `pgvector`), возвращая через MCP только структурированные выжимки и ссылки.