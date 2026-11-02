# MultiWaveScraper — MCP-сервер многоволнового веб-скрейпинга

Сервер на **.NET 11** (+ Microsoft Agent Framework), который по одному поисковому ключу:

1. генерирует несколько «волн» поисковых запросов (точный ключ → альтернативы → синонимы → смежные темы);
2. находит URL через SERP-провайдеры;
3. скачивает страницы, чистит HTML и превращает его в Markdown;
4. дедуплицирует URL и контент, оценивает релевантность;
5. возвращает структурированный отчёт через **MCP** (stdio / Streamable HTTP), **REST** или фоновые задания.

Проект объединяет оба исходных плана: `plan1.md` (волны + REST + классификация) и `plan2.md` (query expansion через LLM, SERP discovery, дедупликация, Markdown, асинхронные задачи, MCP-инструменты).

---

## 1. Что взято из каждого плана

| Возможность | plan1 | plan2 | Реализация |
|---|---|---|---|
| Волна 1 — точный ключ | ✅ | ✅ | `WaveType.ExactMatch`, без LLM |
| Волна 2 — синонимы | ✅ | — | `WaveType.Alternatives` + `Synonyms`, словарь + LLM |
| Волна 3 — похожие темы | ✅ | — | `WaveType.RelatedTopics`, заголовки предыдущих волн |
| Волна 4 — альтернативные формулировки | — | ✅ | `WaveType.Alternatives` |
| Три массива результатов plan1 | ✅ | — | `ScrapeReport.Classification` (`Exact`, `Alternatives`, `Synonyms`, `Related`, `Unclassified`) |
| REST: `/scrape`, `/waves`, `/urls`, `/health` | ✅ | — | `RestEndpoints.cs` |
| Query Expansion через LLM (structured output) | — | ✅ | `LlmQueryExpansionService` + `Microsoft.Extensions.AI` |
| Поиск URL (SearXNG / DDG / Bing / Wikipedia) | частично | ✅ | 5 провайдеров с fallback |
| Нормализация и дедупликация URL | — | ✅ | `UrlNormalizer` + `ConcurrentDictionary` |
| Дедупликация контента (SimHash) | — | ✅ | `ContentFingerprint` |
| HTML → Markdown, очистка шума | — | ✅ | AngleSharp + ReverseMarkdown |
| Playwright fallback для SPA | — | ✅ | `PlaywrightPageFetcher` (по умолчанию выключен) |
| Асинхронные задания с прогрессом | — | ✅ | `start_scrape_job` / `get_scrape_job` / `cancel_scrape_job` |
| MCP-инструменты | — | ✅ | 8 инструментов, stdio + HTTP |
| Microsoft Agent Framework | — | ✅ | консольный агент с MCP-инструментами |

---

## 2. Архитектура

```
                    ┌─────────────────────────────┐
  MCP client  ─────▶│  MCP server (stdio / HTTP)  │
  REST client ─────▶│  /scrape /jobs /health ...  │
  Agent       ─────▶│  /mcp                       │
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │      ScrapeCoordinator      │
                    │  (отчёты, сохранение JSON)  │
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │   MultiWaveScrapeEngine     │
                    │  волна 1..4, дедупликация   │
                    └───┬──────────┬──────────┬───┘
                        │          │          │
              ┌─────────▼──┐ ┌─────▼─────┐ ┌──▼────────────┐
              │ Expansion  │ │  Search   │ │   Scraping    │
              │ LLM+правила│ │ 5 провайд.│ │ HTTP/Playwright│
              └────────────┘ └───────────┘ └──────┬────────┘
                                                   │
                                    ┌──────────────▼──────────────┐
                                    │ AngleSharp + ReverseMarkdown│
                                    │  Markdown + метаданные      │
                                    └─────────────────────────────┘
```

Порядок обработки одной волны:

```
generate queries → search (SERP) → normalize URL → dedup URL
→ fetch (HTTP, при необходимости Playwright) → robots.txt + rate limit
→ clean HTML → Markdown → SimHash dedup → relevance score
→ накопление заголовков для следующей волны → WaveResult
```

---

## 3. Структура решения

```
MCP.WebScraping.slnx
Directory.Build.props
plan1.md / plan2.md
src/
  MultiWaveScraper.Core/          библиотека пайплайна
    Expansion/                    LLM + rule-based генерация волн
    Extraction/                   HTML → Markdown
    Infrastructure/               URL, текст, SimHash, rate limit
    Jobs/                         координатор, фоновые задания, отчёты
    Llm/                          LM Studio chat/embeddings, semantic similarity
    Models/                       опции, отчёты, задания, перечисления
    Scraping/                     HTTP/Playwright fetchers, robots.txt, scraper
    Search/                       SearXNG, DuckDuckGo, Bing, Wikipedia, static
    Waves/                        движок 4 волн, relevance scoring
  MultiWaveScraper.McpServer/     MCP-сервер + REST + demo-страницы
  MultiWaveScraper.Tester/        консольный тест-раннер
  MultiWaveScraper.McpClient/     консольный MCP-клиент
  MultiWaveScraper.Agent/         агент Microsoft Agent Framework + LM Studio
data/                             отчёты (создаётся во время работы)
```

### Стек

| Компонент | Версия |
|---|---|
| .NET | `net11.0` (SDK `11.0.100-rc.1.*`) |
| ModelContextProtocol / AspNetCore | 2.3.0 |
| Microsoft.Agents.AI | 1.27.0 |
| Microsoft.Extensions.AI / .AI.OpenAI | 10.11.0 |
| AngleSharp | 1.9.0 |
| ReverseMarkdown | 6.3.0 |
| Microsoft.Playwright | 1.64.1 |
| Microsoft.Extensions.Http.Resilience | 10.11.0 |

---

## 4. Требования

- .NET 11 SDK (preview).
- LM Studio с OpenAI-совместимым API: `http://127.0.0.1:21234/v1`.
- Модель `lfm3-vl-2b` (можно переопределить).
- Для семантической оценки — embedding-модель `text-embedding-nomic-embed-text-v1.5` (опционально).
- Для Playwright fallback — установленные браузеры (опционально).

Проверка LM Studio:

```powershell
curl http://127.0.0.1:21234/v1/models
```

---

## 5. Быстрый старт

```powershell
# сборка
dotnet build MCP.WebScraping.slnx

# 1. Автономная проверка пайплайна (без сети, на встроенных demo-страницах)
dotnet run --project src/MultiWaveScraper.Tester -- offline

# 2. Проверка окружения (LM Studio + поисковые провайдеры)
dotnet run --project src/MultiWaveScraper.Tester -- env

# 3. Запуск MCP/REST-сервера (HTTP, порт 5199)
dotnet run --project src/MultiWaveScraper.McpServer --no-launch-profile
```

После запуска:

- REST: <http://127.0.0.1:5199/>
- MCP (Streamable HTTP): `http://127.0.0.1:5199/mcp`
- Demo-страницы: <http://127.0.0.1:5199/demo>

Пример запроса:

```powershell
curl "http://127.0.0.1:5199/scrape?query=vector%20database&maxWaves=4&maxPagesPerQuery=2"
```

Другой порт:

```powershell
dotnet run --project src/MultiWaveScraper.McpServer --no-launch-profile -- --port 6000
```

### Подключение MCP-клиента

**stdio** (сервер запускается клиентом сам):

```json
{
  "mcpServers": {
    "multi-wave-scraper": {
      "command": "dotnet",
      "args": [
        "C:\\Works_AI\\MCP.WebScraping\\src\\MultiWaveScraper.McpServer\\bin\\Debug\\net11.0\\MultiWaveScraper.McpServer.dll",
        "--stdio"
      ]
    }
  }
}
```

**Streamable HTTP** (сервер уже запущен):

```json
{
  "mcpServers": {
    "multi-wave-scraper": {
      "url": "http://127.0.0.1:5199/mcp"
    }
  }
}
```

---

## 6. MCP-инструменты (8)

| Инструмент | Назначение |
|---|---|
| `multi_wave_scrape` | Полный проход 1–4 волн: генерация запросов, поиск, скрейпинг, Markdown, классификация. Может работать долго. |
| `start_scrape_job` | Запуск того же пайплайна в фоне; возвращает `id` задания. |
| `get_scrape_job` | Статус/прогресс задания; `includeReport=true` — полный отчёт. |
| `cancel_scrape_job` | Отмена фонового задания. |
| `list_scrape_providers` | Доступность провайдеров поиска (SearXNG, DuckDuckGo, Bing, Wikipedia, static). |
| `discover_urls` | Только SERP discovery: ключ → список URL, без скачивания. |
| `generate_wave_queries` | Только генерация запросов волн 2–4 (LLM или правила). |
| `scrape_single_url` | Одна страница → очищенный Markdown + метаданные. |

Параметры `multi_wave_scrape`: `searchKey`, `maxPagesPerQuery` (1–10), `maxWaves` (1–4), `includeContent`, `providers`, `useLlm`, `semanticScoring`, `useBrowserFallback`.

---

## 7. REST API

| Метод | Путь | Описание |
|---|---|---|
| GET | `/` | Информация о сервере и список эндпоинтов |
| GET | `/health` | Состояние LLM, конфигурации и провайдеров |
| GET | `/providers` | Проверка поисковых провайдеров |
| GET | `/scrape?query=...` | Полный отчёт; параметры `maxPagesPerQuery`, `maxWaves`, `maxContentChars`, `includeContent`, `useLlm`, `semantic`, `providers` |
| GET | `/waves?query=...&wave=2` | Результат одной волны (`1..4` или имя) |
| GET | `/urls?query=...&maxResults=10` | Только список URL |
| POST | `/jobs` | Создать фоновое задание (тело — `ScrapeRequest`) |
| GET | `/jobs` | Список заданий |
| GET | `/jobs/{id}?includeReport=true` | Статус задания и отчёт |
| POST | `/jobs/{id}/cancel` | Отменить задание |
| GET | `/demo` | Каталог demo-страниц |
| GET | `/demo/{slug}` | Demo-страница (HTML) |

Пример фонового задания:

```powershell
$job = curl -s -X POST http://127.0.0.1:5199/jobs `
  -H "Content-Type: application/json" `
  -d '{"query":"MCP server","maxWaves":3,"maxPagesPerQuery":2,"includeContent":false}' |
  ConvertFrom-Json

curl "http://127.0.0.1:5199/jobs/$($job.id)?includeReport=true"
```

Тело `ScrapeRequest`:

```json
{
  "query": "vector database",
  "maxPagesPerQuery": 2,
  "maxWaves": 4,
  "maxContentChars": 6000,
  "includeContent": true,
  "enableLlmExpansion": true,
  "enableSemanticScoring": false,
  "useBrowserFallback": false,
  "providers": ["wikipedia", "duckduckgo"]
}
```

---

## 8. Консольные приложения

### 8.1. Tester

```powershell
dotnet run --project src/MultiWaveScraper.Tester -- [сценарий] [параметры]
```

| Сценарий | Что проверяет |
|---|---|
| `env` | .NET, LM Studio (`/v1/models`), доступность провайдеров |
| `llm` | Генерацию запросов волн 2–4 через LLM + fallback |
| `search` | Только SERP discovery |
| `scrape` | Извлечение Markdown одной страницы |
| `waves` | Полный проход с внешними провайдерами |
| `offline` | Полный пайплайн на встроенных demo-страницах, без сети |
| `interactive` | Интерактивный режим: вводите свои запросы в цикле |
| `all` | `env + llm + offline + scrape + search + waves` |

Параметры: `--query`, `--provider`, `--max-pages 1..10`, `--waves 1..4`, `--url`, `--report <json>`, `--semantic`, `--use-llm <true|false>`, `--verbose`, `--help`.

Код возврата: `0` — успех, `1` — есть провалы, `2` — ошибка аргументов, `130` — отмена.

Примеры:

```powershell
dotnet run --project src/MultiWaveScraper.Tester -- waves --query "MCP сервер" --waves 2 --max-pages 1
dotnet run --project src/MultiWaveScraper.Tester -- scrape --url https://example.com
dotnet run --project src/MultiWaveScraper.Tester -- offline --report data/offline.json
```

#### Интерактивный режим

Позволяет отправить несколько своих запросов за один запуск:

```powershell
dotnet run --project src/MultiWaveScraper.Tester -- interactive
```

```
Запрос> vector database
Запрос> MCP сервер
Запрос> /exit
```

Параметры запуска берутся из CLI/`appsettings.json` и меняются командами:

| Команда | Действие |
|---|---|
| `/help` | Справка |
| `/settings` | Текущие параметры |
| `/waves 1..4` | Количество волн |
| `/pages 1..10` | Страниц на запрос |
| `/llm [on\|off]` | LLM для волн 2–4 (без аргумента — переключить) |
| `/semantic [on\|off]` | Embedding-оценка |
| `/providers wikipedia,duckduckgo` | Список провайдеров; `/providers config` — сброс |
| `/last` | Сводка последнего отчёта |
| `/exit` | Выход |

Пример с ослабленными настройками:

```powershell
dotnet run --project src/MultiWaveScraper.Tester -- interactive --waves 2 --max-pages 1 --use-llm false --provider wikipedia
```

`--report` в интерактивном режиме не перезаписывает файл: к имени добавляется id отчёта (`report-<id>.json`).

### 8.2. McpClient

```powershell
# HTTP (сервер должен быть запущен)
dotnet run --project src/MultiWaveScraper.McpClient -- --list-tools

# stdio (клиент сам запускает сервер)
dotnet run --project src/MultiWaveScraper.McpClient -- --transport stdio --list-tools

# произвольный инструмент
dotnet run --project src/MultiWaveScraper.McpClient -- `
  --tool discover_urls --args '{\"searchKey\":\"vector database\",\"maxResults\":5}'
```

Параметры: `--transport <http|stdio>`, `--url`, `--server <dll>`, `--list-tools`, `--tool`, `--args`, `--query`, `--provider`, `--use-llm`, `--verbose`.

### 8.3. Agent (Microsoft Agent Framework)

Агент подключает LM Studio как `IChatClient` и MCP-инструменты как `AITool`.

```powershell
# HTTP-транспорт (сервер уже запущен)
dotnet run --project src/MultiWaveScraper.Agent -- --query "MCP сервер"

# stdio + потоковый вывод
dotnet run --project src/MultiWaveScraper.Agent -- --transport stdio --query "vector database" --stream

# только показать инструменты
dotnet run --project src/MultiWaveScraper.Agent -- --list-tools
```

Параметры: `--model`, `--base-url`, `--transport`, `--url`, `--server`, `--tools <scrape|core|all|none|имена>`, `--no-tools`, `--prompt`, `--query`, `--stream`, `--timeout`, `--list-tools`, `--verbose`.

По умолчанию агенту передаются `multi_wave_scrape`, `discover_urls`, `scrape_single_url`.

---

## 9. Конфигурация

Секции `Scraper` и `Llm` в `appsettings.json` каждого проекта (`McpServer`, `Tester`, `Agent`).

### Scraper

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `DefaultMaxPagesPerQuery` | 3 | Страниц на один запрос |
| `MaxQueriesPerWave` | 3 | Запросов на волну |
| `MaxConcurrency` | 3 | Параллельных запросов (1–8) |
| `MaxWaves` | 4 | Количество волн (1–4) |
| `MaxTotalPages` | 40 | Верхний лимит страниц |
| `MaxContentChars` | 6000 | Обрезка Markdown на страницу |
| `IncludeContent` | true | Возвращать контент в отчёте |
| `EnableLlmExpansion` | true | LLM для волн 2–4 |
| `EnableSemanticScoring` | false | Embedding-оценка близости |
| `RespectRobotsTxt` | true | Соблюдать robots.txt |
| `UseBrowserFallback` | false | Playwright для SPA/403 |
| `PerHostDelayMs` | 700 | Пауза между запросами к хосту |
| `RequestTimeoutSeconds` | 30 | Тайм-аут страницы |
| `SearchTimeoutSeconds` | 20 | Тайм-аут поиска на провайдера |
| `MaxSearchResultsPerProvider` | 8 | Результатов с провайдера |
| `SelfBaseUrl` | `http://127.0.0.1:5199` | База для относительных `StaticUrls` |
| `EnabledProviders` | `["searxng","duckduckgo","bing","wikipedia","static"]` | Порядок fallback |
| `SearxngBaseUrl` | `""` | Адрес SearXNG; пусто — провайдер отключён |
| `WikipediaLanguage` | `""` | Язык Wikipedia (`ru`, `en`); пусто — авто |
| `StaticUrls` | demo-страницы | Фиксированные URL |
| `SynonymsFile` | `""` | JSON-словарь синонимов |
| `PersistReports` | true | Сохранять отчёты |
| `ReportDirectory` | `data/reports` | Каталог отчётов |

### Llm

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `BaseUrl` | `http://127.0.0.1:21234/v1` | OpenAI-совместимый endpoint |
| `Model` | `lfm3-vl-2b` | Основная модель |
| `ExpansionModel` | `""` | Модель для волн (пусто — `Model`) |
| `FallbackExpansionModel` | `""` | Резервная модель |
| `EmbeddingModel` | `text-embedding-nomic-embed-text-v1.5` | Embeddings |
| `TimeoutSeconds` | 90 | Общий тайм-аут LLM |
| `ExpansionTimeoutSeconds` | 60 | Тайм-аут генерации волны |
| `MaxOutputTokens` | 700 | Лимит ответа |
| `Temperature` | 0.1 | Температура |
| `EnableLlmExpansion` | true | Глобальный выключатель LLM |
| `EnableEmbeddings` | true | Глобальный выключатель embeddings |

Переопределение через окружение:

- `McpServer`: стандартные ключи, например `Scraper__PerHostDelayMs=0`, `Llm__BaseUrl=...`, `Server__Port=6000`.
- `Tester` и `Agent`: префикс `MWS_`, например `MWS_Scraper__PerHostDelayMs=0`.

---

## 10. Как работает пайплайн

### Волны

| Волна | Тип | Источник запросов |
|---|---|---|
| 1 | `ExactMatch` | Исходный ключ, без LLM |
| 2 | `Alternatives` | LLM-перефразирование; fallback — шаблоны |
| 3 | `Synonyms` | LLM + словарь терминов (RU/EN) |
| 4 | `RelatedTopics` | LLM + заголовки/сниппеты волн 1–3; fallback — граф понятий |

LLM вызывается со строгой JSON-схемой (`ChatResponseFormat.ForJsonSchema<QueryExpansionPayload>`). При ошибке, тайм-ауте или пустом ответе автоматически включаются детерминированные правила — пайплайн не падает без модели. Источник виден в `WaveResult.ExpansionSource`: `exact`, `llm`, `rules`, `rules-fallback`.

### Поиск

Провайдеры обходятся последовательно по `Order`:

1. `searxng` (10) — если задан `SearxngBaseUrl`;
2. `duckduckgo` (20) — HTML Lite;
3. `bing` (30) — HTML;
4. `wikipedia` (40) — API;
5. `static` (50) — фиксированные URL.

Обход останавливается, когда набрано достаточно результатов. Ошибка/тайм-аут одного провайдера не прерывает остальные.

### Дедупликация

- URL: удаление `utm_*`, `fbclid`, `yclid`, якорей, нормализация регистра/слешей; реестр посещённых URL на весь запуск.
- Контент: SimHash очищенного текста; зеркала и копии на разных доменах отбрасываются.

### Скачивание и извлечение

- `HttpClient` + retry/timeout/circuit breaker (`Microsoft.Extensions.Http.Resilience`).
- `robots.txt` кэшируется; соблюдается per-host задержка.
- При пустом DOM/403 и `UseBrowserFallback=true` — Playwright.
- AngleSharp удаляет `script/style/nav/footer/header/aside` и извлекает `article`/`main`.
- ReverseMarkdown сохраняет заголовки, списки, ссылки, таблицы и переводы строк.
- Контент обрезается по `MaxContentChars`.

### Релевантность и классификация

Оценка складывается из совпадения токенов в заголовке/тексте, покрытия запроса, Jaccard и (опционально) косинусной близости embeddings. Результат попадает в `MatchKind`:

- `Exact` — точное совпадение;
- `Alternative` — совпадение с перефразированием;
- `Synonym` — совпадение по синониму;
- `Related` — смежная тема;
- `None` — не классифицировано.

`ScrapeReport.Classification` содержит пять массивов `PageRef` — это прямая совместимость с трёхволновой схемой plan1.

### Отчёты и задания

- Каждый отчёт сохраняется в `data/reports/<id>.json` (если `PersistReports=true`).
- Фоновые задания хранятся в памяти процесса, отдают прогресс в процентах и поддерживают отмену.
- MCP HTTP работает в stateless-режиме: прогресс между вызовами не передаётся, для длинных запусков используйте задания.

---

## 11. Результаты проверок

Проверено на .NET `11.0.100-rc.1.26425.128`, LM Studio `http://127.0.0.1:21234`, модель `lfm3-vl-2b`.

| Проверка | Результат |
|---|---|
| `Tester offline` | PASS — 4 волны, 3 страницы, exact-классификация |
| `Tester env` | PASS — LM Studio доступен, 5 моделей; DuckDuckGo, Wikipedia, static доступны |
| `Tester llm` | PASS — 9 запросов, 2/3 волн через LLM, одна волна ушла в rule-fallback по тайм-ауту |
| `Tester search` | PASS — 8 результатов |
| `Tester scrape` | PASS — заголовки, списки и ссылки сохранены в Markdown |
| `Tester waves` | PASS — живой скрейпинг 2 страниц за ~18 с |
| `Tester interactive` | PASS — команды `/settings`, `/waves`, `/pages`, `/providers`, `/last`; живой запрос `vector database` через Wikipedia — 1 страница, exact=1 |
| MCP HTTP | PASS — 8 инструментов, `generate_wave_queries` и `discover_urls` работают |
| MCP stdio | PASS — подключение и список 8 инструментов |
| Agent HTTP | PASS — вызван `multi_wave_scrape`, получен ответ со ссылками |
| Agent stdio | PASS — подключение и список инструментов |
| REST | PASS — `/scrape`, `/jobs` (создание/опрос), `/health` |

Сборка: `0 errors`, `0 warnings`. Проверка после финальных правок: `Tester offline` — PASS, MCP HTTP — 8 инструментов, MCP stdio — 8 инструментов, REST `/scrape` — 1 страница, exact=1, предупреждений нет.

---

## 12. Ограничения и особенности

- **Публичные поисковики нестабильны.** DuckDuckGo HTML часто отвечает `202` (anti-bot), рабочий вариант — Lite. Bing может отдавать captcha или нерелевантную выдачу. Для продакшена лучше поднять свой SearXNG и задать `SearxngBaseUrl`.
- **Wikipedia** ищет только статьи и хорошо подходит для демо/энциклопедических запросов, но не заменяет веб-поиск.
- **`lfm3-vl-2b`** — рассуждающая модель и иногда не укладывается в тайм-аут волны. Это не ошибка: срабатывает rule-fallback; можно увеличить `ExpansionTimeoutSeconds` или указать более быструю `FallbackExpansionModel`.
- **Playwright** выключен по умолчанию. Для включения нужны браузеры: `pwsh src/MultiWaveScraper.McpServer/bin/Debug/net11.0/playwright.ps1 install`.
- **Задания в памяти** теряются при перезапуске сервера; отчёты на диске сохраняются.
- **Нет аутентификации** — сервер рассчитан на локальный запуск (`127.0.0.1`).
- **Ограничения этики/права:** соблюдается `robots.txt`, используется вежливая задержка на хост; не предназначено для обхода платных/закрытых ресурсов.
- **Markdown обрезается** по `MaxContentChars`; для полного текста поднимите лимит (до 50 000 в REST/MCP-параметрах).
- **HTTP MCP stateless** — нет server-initiated progress notifications; для длительных операций используйте `start_scrape_job`.

---

## 13. Диагностика

| Симптом | Что проверить |
|---|---|
| `Tester env` не видит LM Studio | Запущен ли LM Studio, включён ли локальный сервер, совпадает ли порт `21234` |
| Волны 2–4 идут через `rules-fallback` | Тайм-аут/ошибка LLM; смотрите `--verbose`, увеличьте `ExpansionTimeoutSeconds` |
| Поиск возвращает 0 результатов | Провайдеры блокируют запросы; задайте `SearxngBaseUrl` или `--provider wikipedia` |
| `scrape_single_url` возвращает ошибку | robots.txt, Content-Type не HTML, 403/404; попробуйте `useBrowserFallback` |
| Порт 5199 занят | `--port 6000` или завершите старый процесс |
| Отчёты не появляются | `PersistReports=true`; путь `ReportDirectory` считается от текущего каталога процесса |

---

## 14. Лицензия и назначение

Учебно-демонстрационный проект: показывает, как объединить MCP-сервер, локальную LLM, многоволновой поиск и очистку контента в одном .NET-решении. Перед использованием на публичных сайтах проверьте применимые правила и законодательство.
