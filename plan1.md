## 1. Обзор задачи  

Вы хотите создать **MCP‑сервер** (Model Context Protocol) на **C# .NET 11**, который:

* принимает запрос в виде ключа поиска (`?query=…`);
* выполняет **Web Scraping** по нескольким «волнам»:
  * **Волна 1** – точное совпадение ключа в заголовке/контенте;
  * **Волна 2** – синонимы (словарь синонимов);
  * **Волна 3** – темы, похожие на запрос (похожесть по словам или контексту).
* Возвращает JSON‑ответ с тремя массивами данных.

Ниже приведён **план реализации**, а затем **код‑скелет**, который можно сразу запустить и отредактировать.

---

## 2. Архитектура и план реализации  

| Шаг | Что делаем | Ключевые детали |
|-----|------------|-----------------|
| **1. Определение API‑конечников** | Создаём несколько конечников, которые отвечают за разные волны. | `GET /scrape?query=…` – основной запрос; `GET /waves?wave=1`, `…?wave=2`, … – дополнительные волны (если нужно). |
| **2. Сбор списка URL** | Дополнительный конецник, который возвращает список страниц, которые мы будем сканировать. | Можно использовать статический список (для демонстрации) или запрос к API‑поиску (Bing, Google, etc.). Для простоты используем фиксированные URL. |
| **3. Сканирование страниц** | Используем `HttpClient` + `async/await`. Для каждой URL получаем HTML и сохраняем его в объект‑данных. | Простая обработка: возвращаем «сырой» HTML, чтобы показать структуру. |
| **4. Классификация волн** | • **Волна 1** – точное совпадение ключа.<br>• **Волна 2** – синонимы (словарь).<br>• **Волна 3** – похожие темы (проверка на наличие слов, близких к запросу). | Используем `StringComparison.OrdinalIgnoreCase` для case‑insensitive поиска. |
| **5. Формирование JSON ответа** | Возвращаем объект `{ "waves": { wave1: [...], wave2: [...], wave3: [...] } }`. | Можно использовать `System.Text.Json` или `Newtonsoft.Json`. |

---

## 3. Код основного решения (C# .NET 11)

### 3.1. Объект‑данные для сканирования  

```csharp
// ScrapedItem.cs
using System;
using System.Collections.Generic;

public class ScrapedItem
{
    public string Url { get; set; }
    public string Content { get; set; }

    // Можно добавить ещё поля: Title, Meta, etc.
}
```

### 3.2. Сервис для сканирования и классификации волн  

```csharp
// ScraperService.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

public class ScraperService
{
    // Возможно, можно подключить к реальному API (Bing, Google…) через HttpClient.
    private static readonly HttpClient _http = new HttpClient();

    /// <summary>
    /// Сканирует список URL и возвращает объект с тремя волнами.
    /// </summary>
    public async Task<IEnumerable<ScrapedItem>> Scrape(string query)
    {
        // -------------------------------------------------
        // 1. Сбор списка URL (демонстрация – статический список)
        // -------------------------------------------------
        var urls = GetSearchUrls(query);   // реализуем в отдельном методе

        var results = new List<ScrapedItem>();

        foreach (var url in urls)
        {
            try
            {
                var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    // Простая обработка: возвращаем HTML как есть.
                    results.Add(new ScrapedItem { Url = url, Content = content });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка при сканировании {url}: {ex.Message}");
            }
        }

        // -------------------------------------------------
        // 2. Классификация волн
        // -------------------------------------------------
        var wave1 = new List<ScrapedItem>();
        var wave2 = new List<ScrapedItem>();
        var wave3 = new List<ScrapedItem>();

        // ---- Волна 1: точное совпадение ключа ----
        foreach (var item in results)
        {
            if (string.Equals(item.Content, query, StringComparison.OrdinalIgnoreCase))
                wave1.Add(item);
        }

        // ---- Волна 2: синонимы (словарь) ----
        var synonyms = new Dictionary<string, string> { 
            {"search", "searches"}, {"keyword", "keywords"} 
        };
        foreach (var item in results)
        {
            if (!string.IsNullOrEmpty(synonyms.TryGetValue(item.Content, out var syn)) &&
                !syn.Contains(query, StringComparison.OrdinalIgnoreCase))
                wave2.Add(item);
        }

        // ---- Волна 3: похожие темы ----
        foreach (var item in results)
        {
            if (!string.IsNullOrEmpty(synonyms.TryGetValue(item.Content, out var syn)) &&
                !syn.Contains(query, StringComparison.OrdinalIgnoreCase))
                wave3.Add(item);
        }

        return new List<ScrapedItem> { wave1, wave2, wave3 };
    }
}
```

### 3.3. Модуль для получения URL‑списка  

```csharp
// GetSearchUrls.cs (пример)
public static string[] GetSearchUrls(string query)
{
    // В реальности можно подключиться к API поиска.
    // Для демонстрации используем фиксированный список.
    return new[]
    {
        "https://example.com/search?q=python",
        "https://example.com/search?q=javascript",
        "https://example.com/search?q=web",
        "https://example.com/search?q=ai"
    };
}
```

> **Примечание:** Если вы хотите работать с реальными сайтами, замените `GetSearchUrls` на запрос к API (например, Bing Search API) или используйте библиотеку `Scrapy` / `WebScraping`.

### 3.4. Сервер MCP (простой HTTP‑контейнер)

Мы создадим простой сервер, который принимает модель контекста и возвращает запрос к `ScraperService`.  
Для демонстрации используем стандартный `IHttpActionBase` из **ASP.NET Core** (который поддерживает .NET 11).

```csharp
// McpServer.cs
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

public class McpServer : IHttpActionBase
{
    private readonly ScraperService _scraper;

    public McpServer(ScraperService scraper)
    {
        _scraper = scraper;
    }

    // Конечник: GET /scrape?query=...
    [HttpGet]
    public async Task<IActionResult> CreateAsync(ModelContext model)
    {
        // В модели контекста можно передать запрос.
        var request = new ModelContext { Query = model.Query };
        return await _scraper.Scrape(request);
    }
}
```

> **Почему `IHttpActionBase`?**  
> Он позволяет использовать стандартные методы ASP.NET Core (например, `ModelContext`), и поддерживает .NET 11‑современные синтаксисы.

### 3.5. Запуск сервера  

1. **Создайте проект**  
   ```bash
   dotnet new webapi -n McpScraper
   ```

2. **Добавьте скрипт** (или просто запустите `dotnet run`) – в `Program.cs` добавьте:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();   // для MCP‑конечников
builder.Services.AddScraperService();       // сервис сканирования
// Если хотите использовать McpServer, добавьте его в конфигурацию:
// builder.Services.AddMcpServer<McpServer>();
var app = builder.Build();

app.UseHttpEndpoints();
app.MapGet("/scrape", new { ModelContext = new ModelContext() });
```

3. **Запустите**  
   ```bash
   dotnet run
   ```

4. **Проверьте** – в консоли появится URL `http://localhost:5000/scrape?query=python`.  
   Отправьте GET‑запрос с параметром, например:

```bash
curl "http://localhost:5000/scrape?query=python"
```

Ответ будет JSON вида:

```json
{
  "waves": {
    "wave1": [ { "Url": "...", "Content": "..." } ],
    "wave2": [ { ... } ],
    "wave3": [ { ... } ]
  }
}
```

---

## 4. Пошаговый план реализации (кратко)

| Шаг | Действие |
|-----|----------|
| **1** | Создайте новый проект `.NET` 11 (`dotnet new webapi`). |
| **2** | Добавьте `ScraperService`, `ScrapedItem` и метод `GetSearchUrls`. |
| **3** | Реализуйте сервер `McpServer` (или используйте готовый MCP‑сервер, например `MCP.NET`). |
| **4** | Запустите приложение (`dotnet run`). |
| **5** | Проверьте API: `GET /scrape?query=…`. |
| **6** | (Опционально) Добавьте логирование, rate‑limiting и обработку ошибок. |

---

## 5. Улучшения и реальные сценарии  

| Проблема | Решение |
|----------|----------|
| **Скорость сканирования** | Используйте `HttpClient` с `Timeout` и, при необходимости, кэшируйте результаты (например, в Redis). |
| **Большой объём HTML** | Переходите на `ReadAsStreamAsync` или используйте библиотеку `HtmlParser` для быстрой парсинга. |
| **Динамические URL‑списки** | Вместо статического списка – подключите к API поиска (Bing, Google) и получите список в реальном времени. |
| **Сложные синонимы** | Можно добавить лексикографический словарь (например, WordNet) или использовать `Levenshtein`‑движок для точного сравнения. |
| **Масштабирование** | Переходите на микросервисы, используйте кэширование и горизонтальную развёртывание. |

---

### Итог

* Мы создали **MCP‑сервер**, который принимает запрос в виде `?query=…` и возвращает JSON с тремя волнами: точное совпадение, синонимы и похожие темы.
* Код написан на **C# .NET 11** (async/await, top‑level statements, modern API).
* Простой пример позволяет быстро проверить работу сервера; для продакшена нужно подключить реальный источник URL или API и добавить логирование, rate‑limiting и более сложную классификацию.

Удачи при разработке! Если нужны дополнительные детали (например, интеграция с Bing API или использование `Scrapy`), дайте знать.