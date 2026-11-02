namespace MultiWaveScraper.Core.Demo;

public sealed record DemoPage
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string BodyHtml { get; init; }
}

/// <summary>
/// Встроенные demo-страницы: позволяют прогнать весь пайплайн
/// (поиск → скрейпинг → волны) без внешней сети.
/// </summary>
public static class DemoPageCatalog
{
    public static IReadOnlyList<DemoPage> Pages { get; } =
    [
        new DemoPage
        {
            Slug = "dotnet",
            Title = ".NET 11 и C#: обзор платформы",
            Description = "Что нового в .NET 11, C# 14 и ASP.NET Core: производительность, minimal API, Native AOT.",
            BodyHtml = """
                <h1>.NET 11 и C#</h1>
                <p>.NET 11 — кроссплатформенная платформа для разработки приложений на C#. В релизе улучшены
                производительность JIT, работа с памятью и инструменты диагностики. ASP.NET Core предлагает
                minimal API, встроенную валидацию и OpenAPI.</p>
                <h2>Ключевые возможности</h2>
                <ul>
                  <li>Native AOT и уменьшение времени старта</li>
                  <li>Новые API System.Text.Json и System.IO.Compression (Zstandard)</li>
                  <li>Улучшенная поддержка контейнеров и Linux</li>
                </ul>
                <p>Для веб-приложений используйте ASP.NET Core, для фоновых задач — Worker Service,
                для десктопа — .NET MAUI или WPF.</p>
                """
        },
        new DemoPage
        {
            Slug = "mcp",
            Title = "Model Context Protocol (MCP)",
            Description = "Открытый протокол для подключения инструментов и данных к LLM-приложениям.",
            BodyHtml = """
                <h1>Model Context Protocol</h1>
                <p>MCP — это открытый протокол, который стандартизирует обмен контекстом между
                AI-приложениями и внешними источниками данных. Сервер MCP публикует инструменты (tools),
                ресурсы (resources) и промпты (prompts), а клиент вызывает их по JSON-RPC.</p>
                <h2>Транспорты</h2>
                <p>Поддерживаются stdio для локальных процессов и Streamable HTTP для удалённых серверов.
                Официальный C# SDK — ModelContextProtocol — позволяет объявить инструмент атрибутом
                McpServerTool и подключить его к агенту.</p>
                <h2>Сценарии</h2>
                <ul>
                  <li>Доступ агента к базам данных и файлам</li>
                  <li>Интеграция с веб-скрейпингом и поиском</li>
                  <li>Единый интерфейс инструментов для разных LLM</li>
                </ul>
                """
        },
        new DemoPage
        {
            Slug = "vector-db",
            Title = "Векторные базы данных (Vector Database) и поиск по эмбеддингам",
            Description = "Как работают vector database, HNSW-индексы и семантический поиск для RAG.",
            BodyHtml = """
                <h1>Векторные базы данных</h1>
                <p>Векторная база данных хранит эмбеддинги и выполняет поиск ближайших соседей.
                Она используется в RAG (retrieval augmented generation), рекомендательных системах
                и семантическом поиске. Популярные решения: pgvector, Qdrant, Milvus, Weaviate.</p>
                <h2>Индексы</h2>
                <p>HNSW обеспечивает быстрый approximate nearest neighbor search, IVFFlat — компромисс
                между памятью и скоростью. Метрики близости: косинус, скалярное произведение, евклидово расстояние.</p>
                <h2>RAG</h2>
                <p>Типичный пайплайн: chunking документов, построение эмбеддингов, гибридный поиск
                (BM25 + vector), reranking и генерация ответа языковой моделью.</p>
                """
        },
        new DemoPage
        {
            Slug = "web-scraping",
            Title = "Веб-скрапинг: архитектура и ограничения",
            Description = "Парсинг HTML, headless-браузеры, rate limiting и уважение к robots.txt.",
            BodyHtml = """
                <h1>Веб-скрапинг</h1>
                <p>Веб-скрапинг — автоматическое извлечение данных с сайтов. Быстрый путь —
                HttpClient и парсер HTML (AngleSharp), медленный, но надёжный — headless-браузер
                (Playwright) для SPA-приложений.</p>
                <h2>Практики</h2>
                <ul>
                  <li>Соблюдайте robots.txt и условия использования сайта</li>
                  <li>Ограничивайте частоту запросов на домен</li>
                  <li>Дедуплицируйте URL и контент (SHA256, SimHash)</li>
                  <li>Очищайте DOM от script/style/nav и конвертируйте в Markdown</li>
                </ul>
                <p>Для больших объёмов используйте очередь заданий, кэш и горизонтальное масштабирование.</p>
                """
        },
        new DemoPage
        {
            Slug = "lm-studio",
            Title = "LM Studio: локальный OpenAI-совместимый сервер",
            Description = "Запуск локальных LLM и embedding-моделей через LM Studio и OpenAI API.",
            BodyHtml = """
                <h1>LM Studio</h1>
                <p>LM Studio запускает GGUF-модели локально и предоставляет OpenAI-совместимый API
                по адресу http://127.0.0.1:1234/v1. Поддерживаются chat completions, tool calling,
                structured output (JSON Schema) и embeddings.</p>
                <h2>Возможности</h2>
                <ul>
                  <li>Модели для чата и эмбеддингов в одном процессе</li>
                  <li>Tool calling и JSON-схемы для надёжного парсинга ответов</li>
                  <li>Настройка контекста и квантования под доступную память</li>
                </ul>
                """
        },
        new DemoPage
        {
            Slug = "agents",
            Title = "Microsoft Agent Framework: агенты и инструменты",
            Description = "AIAgent, ChatClientAgent, tool calling и интеграция с MCP-серверами.",
            BodyHtml = """
                <h1>Microsoft Agent Framework</h1>
                <p>Microsoft Agent Framework предоставляет AIAgent и ChatClientAgent для построения
                агентов поверх IChatClient. Агент получает инструкции, историю диалога и набор
                инструментов (AITool). Вызов инструментов выполняется автоматически
                через function calling.</p>
                <h2>MCP-инструменты</h2>
                <p>ModelContextProtocol.Client возвращает список McpClientTool, которые наследуются
                от AIFunction, поэтому их можно передать агенту напрямую. Это связывает
                MCP-сервер с локальной или облачной моделью.</p>
                """
        }
    ];

    public static DemoPage? Find(string slug)
        => Pages.FirstOrDefault(page => page.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    public static string RenderHtml(DemoPage page)
    {
        const string template = """
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="description" content="__DESCRIPTION__">
              <meta property="og:title" content="__TITLE__">
              <meta property="og:description" content="__DESCRIPTION__">
              <title>__TITLE__</title>
              <style>body{font-family:system-ui;max-width:760px;margin:2rem auto;padding:0 1rem;line-height:1.6}nav,footer{color:#666}</style>
            </head>
            <body>
              <nav><a href="/demo">Demo index</a></nav>
              <main>__BODY__</main>
              <footer>Generated by MultiWaveScraper demo catalog</footer>
            </body>
            </html>
            """;

        return template
            .Replace("__TITLE__", WebUtility.HtmlEncode(page.Title), StringComparison.Ordinal)
            .Replace("__DESCRIPTION__", WebUtility.HtmlEncode(page.Description), StringComparison.Ordinal)
            .Replace("__BODY__", page.BodyHtml, StringComparison.Ordinal);
    }
}
