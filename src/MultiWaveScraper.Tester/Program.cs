using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiWaveScraper.Core;
using MultiWaveScraper.Core.Infrastructure;
using MultiWaveScraper.Core.Demo;
using MultiWaveScraper.Core.Expansion;
using MultiWaveScraper.Core.Jobs;
using MultiWaveScraper.Core.Llm;
using MultiWaveScraper.Core.Models;
using MultiWaveScraper.Core.Scraping;
using MultiWaveScraper.Core.Search;

namespace MultiWaveScraper.Tester;

/// <summary>
/// Консольный тест-раннер пайплайна. Сценарии: env, llm, search, scrape, waves, offline, all.
/// Код возврата: 0 — все проверки прошли, 1 — есть провалы, 2 — ошибка аргументов.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Ошибка аргументов: {exception.Message}");
            Console.Error.WriteLine();
            CliOptions.PrintUsage();
            return 2;
        }

        if (cli.Help)
        {
            CliOptions.PrintUsage();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine("MultiWaveScraper Tester");
        Console.WriteLine($"Сценарий: {cli.Scenario}");
        Console.WriteLine();

        var outcomes = new List<Outcome>();
        try
        {
            foreach (string scenario in ResolveScenarios(cli.Scenario))
            {
                Outcome outcome = await RunScenarioAsync(scenario, cli, cancellation.Token);
                outcomes.Add(outcome);
                PrintOutcome(outcome);

                if (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено пользователем.");
            return 130;
        }

        Console.WriteLine();
        Console.WriteLine("Итог:");
        foreach (Outcome outcome in outcomes)
        {
            Console.WriteLine($"  {(outcome.Success ? "PASS" : "FAIL")}  {outcome.Name,-8}  {outcome.Detail}");
        }

        int failed = outcomes.Count(outcome => !outcome.Success);
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"Все проверки пройдены ({outcomes.Count})."
            : $"Провалено проверок: {failed} из {outcomes.Count}.");

        return failed == 0 ? 0 : 1;
    }

    private static IEnumerable<string> ResolveScenarios(string scenario) => scenario.ToLowerInvariant() switch
    {
        "all" => ["env", "llm", "offline", "scrape", "search", "waves"],
        "interactive" or "chat" or "repl" or "ask" => ["interactive"],
        _ => [scenario.ToLowerInvariant()]
    };

    private static async Task<Outcome> RunScenarioAsync(string scenario, CliOptions cli, CancellationToken cancellationToken)
    {
        try
        {
            return scenario switch
            {
                "env" => await RunEnvironmentAsync(cli, cancellationToken),
                "llm" => await RunLlmAsync(cli, cancellationToken),
                "search" => await RunSearchAsync(cli, cancellationToken),
                "scrape" => await RunScrapeAsync(cli, cancellationToken),
                "waves" => await RunWavesAsync(cli, cancellationToken),
                "offline" => await RunOfflineAsync(cli, cancellationToken),
                "interactive" => await RunInteractiveAsync(cli, cancellationToken),
                _ => throw new ArgumentException($"Неизвестный сценарий '{scenario}'. Допустимо: env, llm, search, scrape, waves, offline, interactive, all.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new Outcome(scenario, false, exception.Message);
        }
    }

    private static async Task<Outcome> RunEnvironmentAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine("[env] Проверка окружения");

        LlmOptions llm = LoadLlmOptions();
        Console.WriteLine($"  .NET:       {Environment.Version}");
        Console.WriteLine($"  OS:         {Environment.OSVersion}");
        Console.WriteLine($"  LM Studio:  {llm.BaseUrl} ({llm.Model})");

        bool modelsOk;
        string modelDetail;
        try
        {
            IReadOnlyList<string> models = await LmStudioClientFactory.ListModelsAsync(llm, cancellationToken);
            modelsOk = models.Count > 0;
            modelDetail = modelsOk ? string.Join(", ", models) : "список моделей пуст";
            Console.WriteLine($"  Модели:     {modelDetail}");
        }
        catch (Exception exception)
        {
            modelsOk = false;
            modelDetail = exception.Message;
            Console.WriteLine($"  Модели:     недоступны ({exception.Message})");
        }

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        var searchEngine = services.GetRequiredService<ISearchEngineClient>();
        IReadOnlyList<ProviderStatus> providers = await searchEngine.GetProviderStatusAsync(cancellationToken);
        foreach (ProviderStatus provider in providers)
        {
            Console.WriteLine(
                $"  Поиск:      {provider.Name,-12} enabled={provider.Enabled,-5} available={provider.Available,-5} {provider.Message}");
        }

        bool searchOk = providers.Any(provider => provider.Enabled && provider.Available);
        bool success = modelsOk && searchOk;
        string detail = $"LM Studio={(modelsOk ? "ok" : "fail")}; поиск={(searchOk ? "ok" : "fail")}";
        return new Outcome("env", success, detail);
    }

    private static async Task<Outcome> RunLlmAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[llm] Генерация волн 2–4 для «{cli.Query}»");

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        var expansion = services.GetRequiredService<IQueryExpansionService>();
        ScraperOptions scraper = services.GetRequiredService<IOptionsMonitor<ScraperOptions>>().CurrentValue;

        int produced = 0;
        int llmWaves = 0;
        foreach (WaveType wave in new[] { WaveType.Alternatives, WaveType.Synonyms, WaveType.RelatedTopics })
        {
            ExpansionResult result = await expansion.GenerateAsync(
                cli.Query, wave, [], scraper.MaxQueriesPerWave, cli.UseLlm, cancellationToken);

            produced += result.Queries.Count;
            if (string.Equals(result.Source, "llm", StringComparison.OrdinalIgnoreCase))
            {
                llmWaves++;
            }

            Console.WriteLine(
                $"  {WaveNames.ShortName(wave),-18} source={result.Source,-10} model={result.Model,-24} " +
                $"{result.DurationMs,6} ms  queries={result.Queries.Count}");
            foreach (string query in result.Queries)
            {
                Console.WriteLine($"      • {query}");
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                Console.WriteLine($"      ! {result.Error}");
            }
        }

        bool success = produced > 0;
        string detail = success
            ? $"{produced} запросов, LLM-волн: {llmWaves}/3"
            : "не удалось сгенерировать ни одного запроса";
        return new Outcome("llm", success, detail);
    }

    private static async Task<Outcome> RunSearchAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[search] Поиск URL для «{cli.Query}» (providers={cli.Providers ?? "из конфигурации"})");

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        var searchEngine = services.GetRequiredService<ISearchEngineClient>();

        SearchResponse response = await searchEngine.SearchAsync(
            cli.Query,
            8,
            CliOptions.ParseProviders(cli.Providers),
            cancellationToken);

        foreach (SearchHit hit in response.Hits)
        {
            Console.WriteLine($"  {hit.Rank + 1,2}. [{hit.Provider}] {hit.Title}");
            Console.WriteLine($"      {hit.Url}");
        }

        foreach (string warning in response.Warnings)
        {
            Console.WriteLine($"  ! {warning}");
        }

        bool success = response.Hits.Count > 0;
        return new Outcome("search", success,
            $"{response.Hits.Count} результатов за {response.DurationMs} ms через {string.Join(",", response.ProvidersUsed)}");
    }

    private static async Task<Outcome> RunScrapeAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        await using DemoServer? demo = cli.Url is null
            ? await DemoServer.StartAsync(cancellationToken)
            : null;

        string url = cli.Url ?? $"{demo!.BaseUrl}/demo/dotnet";
        Console.WriteLine($"[scrape] Извлечение Markdown: {url}");

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        var scraper = services.GetRequiredService<IPageScraper>();
        ScrapeOptions options = services.GetRequiredService<IOptionsMonitor<ScraperOptions>>()
            .CurrentValue.CreateDefaultOptions();
        options.MaxContentChars = Math.Max(options.MaxContentChars, 4000);

        ScrapedPage? page = await scraper.ScrapeAsync(
            new SearchHit { Url = UrlNormalizer.Normalize(url), Title = "" },
            WaveType.ExactMatch,
            url,
            options,
            cancellationToken);

        if (page is null)
        {
            return new Outcome("scrape", false, "страница не извлечена (robots.txt, Content-Type или сеть)");
        }

        Console.WriteLine($"  Title:      {page.Title}");
        Console.WriteLine($"  Status:     {page.StatusCode}, via {page.FetchedVia}");
        Console.WriteLine($"  Words:      {page.WordCount}, chars: {page.ContentChars}");
        Console.WriteLine($"  Relevance:  {page.RelevanceScore:F2}");
        Console.WriteLine("  --- Markdown ---");
        Console.WriteLine(Truncate(page.MarkdownContent, 900));
        Console.WriteLine("  ----------------");

        bool success = !string.IsNullOrWhiteSpace(page.MarkdownContent);
        return new Outcome("scrape", success, $"{page.WordCount} слов, {page.ContentChars} символов");
    }

    private static async Task<Outcome> RunWavesAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[waves] Полный многоволновой скрейпинг «{cli.Query}»");

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        ScrapeReport report = await ExecuteWavesAsync(services, cli, cancellationToken);
        PrintReportSummary(report);

        if (cli.ReportPath is not null)
        {
            await WriteReportAsync(report, cli.ReportPath, cancellationToken);
        }

        bool success = report.TotalPages > 0;
        return new Outcome("waves", success,
            $"{report.TotalPages} страниц, {report.Waves.Count} волн, {report.DurationMs} ms");
    }

    private static async Task<Outcome> RunOfflineAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine("[offline] Полный пайплайн на встроенных demo-страницах (без внешней сети)");

        await using DemoServer demo = await DemoServer.StartAsync(cancellationToken);

        var overrides = new Dictionary<string, string?>
        {
            ["Scraper:SelfBaseUrl"] = demo.BaseUrl,
            ["Scraper:EnabledProviders:0"] = "static",
            ["Scraper:PersistReports"] = "false",
            ["Scraper:PerHostDelayMs"] = "0",
            ["Scraper:RespectRobotsTxt"] = "false",
            ["Scraper:DefaultMaxPagesPerQuery"] = cli.MaxPages.ToString(),
            ["Llm:EnableLlmExpansion"] = "false"
        };

        int index = 0;
        foreach (DemoPage page in DemoPageCatalog.Pages.OrderByDescending(page => page.Slug == "vector-db"))
        {
            overrides[$"Scraper:StaticUrls:{index++}"] = $"/demo/{page.Slug}";
        }

        await using ServiceProvider services = BuildServices(cli, overrides);
        var cliOffline = cli with { UseLlm = false, Providers = "static" };
        ScrapeReport report = await ExecuteWavesAsync(services, cliOffline, cancellationToken);
        PrintReportSummary(report);

        bool hasFourWaves = report.Waves.Count == 4;
        bool wave1Pages = report.Waves
            .FirstOrDefault(wave => wave.Wave == WaveType.ExactMatch)?.Pages.Count > 0;
        bool allWavesHaveQueries = report.Waves.Count > 0
                                   && report.Waves.All(wave => wave.GeneratedQueries.Count > 0);
        bool classified = CountClassified(report.Classification) > 0;

        bool success = hasFourWaves && wave1Pages && allWavesHaveQueries && classified;
        string detail =
            $"волн={report.Waves.Count}, страниц={report.TotalPages}, " +
            $"queries={(allWavesHaveQueries ? "ok" : "fail")}, classification={(classified ? "ok" : "fail")}";
        return new Outcome("offline", success, detail);
    }

    /// <summary>
    /// Интерактивный режим: пользователь вводит запросы в цикле, между запросами
    /// можно менять параметры командами (/waves, /pages, /llm, /providers, ...).
    /// </summary>
    private static async Task<Outcome> RunInteractiveAsync(CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine("[interactive] Вводите поисковые запросы. /help — команды, /exit — выход.");
        Console.WriteLine();

        await using ServiceProvider services = BuildServices(cli, new Dictionary<string, string?>());
        var coordinator = services.GetRequiredService<ScrapeCoordinator>();
        ScraperOptions defaults = services.GetRequiredService<IOptionsMonitor<ScraperOptions>>().CurrentValue;

        var state = new InteractiveState
        {
            MaxWaves = Math.Clamp(cli.MaxWaves, 1, 4),
            MaxPages = Math.Clamp(cli.MaxPages, 1, 10),
            UseLlm = cli.UseLlm,
            Semantic = cli.Semantic,
            Providers = CliOptions.ParseProviders(cli.Providers)
        };

        PrintInteractiveSettings(state);
        Console.WriteLine();

        int succeeded = 0;
        int failed = 0;
        ScrapeReport? lastReport = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Console.IsInputRedirected)
            {
                Console.Write("Запрос> ");
            }

            string? input = Console.ReadLine();
            if (input is null)
            {
                break;
            }

            string query = input.Trim();
            if (query.Length == 0)
            {
                continue;
            }

            if (query.StartsWith('/'))
            {
                if (!HandleInteractiveCommand(query, state, lastReport))
                {
                    break;
                }

                Console.WriteLine();
                continue;
            }

            try
            {
                ScrapeOptions options = defaults.CreateDefaultOptions();
                options.MaxWaves = state.MaxWaves;
                options.MaxPagesPerQuery = state.MaxPages;
                options.EnableLlmExpansion = state.UseLlm;
                options.EnableSemanticScoring = state.Semantic;
                options.Providers = state.Providers is null ? null : [.. state.Providers];

                Console.WriteLine();
                Console.WriteLine($"Запрос: {query}");

                int lastPercent = -1;
                var progress = new InlineProgress(update =>
                {
                    if (update.Percent == lastPercent && update.Percent != 100)
                    {
                        return;
                    }

                    lastPercent = update.Percent;
                    Console.WriteLine($"  [{WaveNames.ShortName(update.Wave)}] {update.Percent,3}% {update.Message}");
                });

                ScrapeReport report = await coordinator
                    .RunAsync(query, options, progress, cancellationToken)
                    .ConfigureAwait(false);

                lastReport = report;
                PrintReportSummary(report);

                if (cli.ReportPath is not null)
                {
                    await WriteInteractiveReportAsync(report, cli.ReportPath, cancellationToken);
                }

                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"ОШИБКА: {exception.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Интерактивная сессия завершена: успешно={succeeded}, с ошибкой={failed}." +
            (lastReport is null ? "" : $" Последний отчёт: {lastReport.Id}."));

        return new Outcome("interactive", failed == 0, $"успешно={succeeded}, ошибок={failed}");
    }

    private static bool HandleInteractiveCommand(string input, InteractiveState state, ScrapeReport? lastReport)
    {
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        switch (command)
        {
            case "/exit" or "/quit" or "/q":
                return false;

            case "/help" or "/?":
                PrintInteractiveHelp();
                return true;

            case "/settings":
                PrintInteractiveSettings(state);
                return true;

            case "/waves":
                if (int.TryParse(argument, out int waves) && waves is >= 1 and <= 4)
                {
                    state.MaxWaves = waves;
                }
                else
                {
                    Console.WriteLine("Использование: /waves 1..4");
                }

                PrintInteractiveSettings(state);
                return true;

            case "/pages":
                if (int.TryParse(argument, out int pages) && pages is >= 1 and <= 10)
                {
                    state.MaxPages = pages;
                }
                else
                {
                    Console.WriteLine("Использование: /pages 1..10");
                }

                PrintInteractiveSettings(state);
                return true;

            case "/llm":
                state.UseLlm = ParseInteractiveBool(argument, state.UseLlm, "/llm [on|off]");
                PrintInteractiveSettings(state);
                return true;

            case "/semantic":
                state.Semantic = ParseInteractiveBool(argument, state.Semantic, "/semantic [on|off]");
                PrintInteractiveSettings(state);
                return true;

            case "/providers":
                state.Providers = string.IsNullOrWhiteSpace(argument)
                                  || argument.Equals("config", StringComparison.OrdinalIgnoreCase)
                                  || argument.Equals("reset", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : argument
                        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                PrintInteractiveSettings(state);
                return true;

            case "/last":
                if (lastReport is null)
                {
                    Console.WriteLine("Ещё нет выполненных запросов.");
                }
                else
                {
                    PrintReportSummary(lastReport);
                }

                return true;

            default:
                Console.WriteLine($"Неизвестная команда '{command}'. /help — список команд.");
                return true;
        }
    }

    private static bool ParseInteractiveBool(string value, bool current, string usage)
    {
        switch (value.ToLowerInvariant())
        {
            case "":
                return !current;
            case "on" or "true" or "1" or "yes" or "да":
                return true;
            case "off" or "false" or "0" or "no" or "нет":
                return false;
            default:
                Console.WriteLine($"Использование: {usage}");
                return current;
        }
    }

    private static void PrintInteractiveSettings(InteractiveState state)
    {
        Console.WriteLine(
            $"  волн={state.MaxWaves}, страниц/запрос={state.MaxPages}, " +
            $"LLM={(state.UseLlm ? "on" : "off")}, semantic={(state.Semantic ? "on" : "off")}, " +
            $"providers={(state.Providers is null ? "из конфигурации" : string.Join(",", state.Providers))}");
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine("""
            Команды:
              /help                 эта справка
              /settings             текущие параметры запуска
              /waves 1..4           количество волн
              /pages 1..10          страниц на один запрос
              /llm [on|off]         LLM для волн 2–4 (без аргумента — переключить)
              /semantic [on|off]    embedding-оценка близости
              /providers <список>   провайдеры, например wikipedia,duckduckgo
                                    /providers config — вернуть список из конфигурации
              /last                 сводка последнего отчёта
              /exit                 выход

            Любая строка без "/" отправляется как поисковый запрос.
            """);
    }

    private static async Task WriteInteractiveReportAsync(ScrapeReport report, string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        string extension = Path.GetExtension(fullPath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".json";
        }

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string target = Path.Combine(directory ?? "", $"{stem}-{report.Id}{extension}");
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        Console.WriteLine($"  JSON отчёта записан: {target}");
    }

    private static async Task<ScrapeReport> ExecuteWavesAsync(
        ServiceProvider services,
        CliOptions cli,
        CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IOptionsMonitor<ScraperOptions>>().CurrentValue;
        ScrapeOptions options = configuration.CreateDefaultOptions();
        options.MaxWaves = Math.Clamp(cli.MaxWaves, 1, 4);
        options.MaxPagesPerQuery = Math.Clamp(cli.MaxPages, 1, 10);
        options.EnableLlmExpansion = cli.UseLlm;
        options.EnableSemanticScoring = cli.Semantic;
        options.Providers = CliOptions.ParseProviders(cli.Providers);

        var coordinator = services.GetRequiredService<ScrapeCoordinator>();
        int lastPercent = -1;
        var progress = new InlineProgress(update =>
        {
            if (update.Percent == lastPercent && update.Percent != 100)
            {
                return;
            }

            lastPercent = update.Percent;
            Console.WriteLine($"  [{WaveNames.ShortName(update.Wave)}] {update.Percent,3}% {update.Message}");
        });

        return await coordinator.RunAsync(cli.Query, options, progress, cancellationToken);
    }

    private static void PrintReportSummary(ScrapeReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"  Отчёт {report.Id}: {report.TotalPages} страниц за {report.DurationMs} ms");
        Console.WriteLine($"  LLM: {report.LlmSourceSummary} (model={report.LlmModel})");
        Console.WriteLine($"  Провайдеры: {string.Join(", ", report.ProvidersUsed)}");

        foreach (WaveResult wave in report.Waves)
        {
            Console.WriteLine(
                $"  {WaveNames.ShortName(wave.Wave),-18} pages={wave.Pages.Count,2} " +
                $"urls={wave.Stats.UniqueUrls,2} duplicates={wave.Stats.DuplicateUrlsSkipped,2} " +
                $"errors={wave.Stats.SearchErrors} source={wave.ExpansionSource}");
            Console.WriteLine($"      queries: {string.Join(" | ", wave.GeneratedQueries)}");

            foreach (ScrapedPage page in wave.Pages)
            {
                Console.WriteLine($"      • [{page.MatchKind}] {page.Title} — {page.Url}");
            }
        }

        ScrapeClassification classification = report.Classification;
        Console.WriteLine(
            $"  Классификация: exact={classification.Exact.Count}, alternatives={classification.Alternatives.Count}, " +
            $"synonyms={classification.Synonyms.Count}, related={classification.Related.Count}, " +
            $"unclassified={classification.Unclassified.Count}");

        if (report.Warnings.Count > 0)
        {
            Console.WriteLine("  Предупреждения:");
            foreach (string warning in report.Warnings)
            {
                Console.WriteLine($"      ! {warning}");
            }
        }

        if (report.ReportFile is not null)
        {
            Console.WriteLine($"  Файл отчёта: {report.ReportFile}");
        }

        Console.WriteLine();
    }

    private static async Task WriteReportAsync(ScrapeReport report, string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
        Console.WriteLine($"  JSON отчёта записан: {fullPath}");
    }

    private static int CountClassified(ScrapeClassification classification)
        => classification.Exact.Count + classification.Alternatives.Count
           + classification.Synonyms.Count + classification.Related.Count;

    private static ServiceProvider BuildServices(CliOptions cli, IDictionary<string, string?> overrides)
    {
        if (!string.IsNullOrWhiteSpace(cli.Cookie))
        {
            overrides["Scraper:CookieHeader"] = cli.Cookie;
        }

        if (!string.IsNullOrWhiteSpace(cli.UserAgent))
        {
            overrides["Scraper:UserAgent"] = cli.UserAgent;
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MWS_")
            .AddInMemoryCollection(overrides)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.SetMinimumLevel(cli.Verbose ? LogLevel.Information : LogLevel.Warning);
            if (!cli.Verbose)
            {
                builder.AddFilter("MultiWaveScraper", LogLevel.Warning);
            }
        });
        services.AddMultiWaveScraper(configuration);
        return services.BuildServiceProvider();
    }

    private static LlmOptions LoadLlmOptions()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MWS_")
            .Build();

        return configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
    }

    private static void PrintOutcome(Outcome outcome)
    {
        Console.ForegroundColor = outcome.Success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(outcome.Success ? "PASS" : "FAIL");
        Console.ResetColor();
        Console.WriteLine($"  {outcome.Name}: {outcome.Detail}");
        Console.WriteLine();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private sealed record Outcome(string Name, bool Success, string Detail);

    private sealed class InlineProgress(Action<ScrapeProgress> report) : IProgress<ScrapeProgress>
    {
        public void Report(ScrapeProgress value) => report(value);
    }

    /// <summary>Мини-сервер demo-страниц для тестов без внешней сети.</summary>
    private sealed class DemoServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private DemoServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<DemoServer> StartAsync(CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));

            WebApplication app = builder.Build();
            app.MapGet("/demo", () => Results.Ok(DemoPageCatalog.Pages.Select(page => new
            {
                page.Slug,
                page.Title,
                page.Description
            })));
            app.MapGet("/demo/{slug}", (string slug) =>
            {
                DemoPage? page = DemoPageCatalog.Find(slug);
                return page is null
                    ? Results.NotFound()
                    : Results.Content(DemoPageCatalog.RenderHtml(page), "text/html; charset=utf-8");
            });

            await app.StartAsync(cancellationToken);

            IServer server = app.Services.GetRequiredService<IServer>();
            string? address = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (string.IsNullOrEmpty(address))
            {
                await app.DisposeAsync();
                throw new InvalidOperationException("Не удалось определить адрес demo-сервера.");
            }

            return new DemoServer(app, address);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private sealed class InteractiveState
    {
        public int MaxWaves { get; set; } = 4;
        public int MaxPages { get; set; } = 3;
        public bool UseLlm { get; set; } = true;
        public bool Semantic { get; set; }
        public List<string>? Providers { get; set; }
    }

    private sealed record CliOptions
    {
        public string Scenario { get; init; } = "all";
        public string Query { get; init; } = "vector database";
        public string? Providers { get; init; }
        public int MaxPages { get; init; } = 2;
        public int MaxWaves { get; init; } = 4;
        public string BaseUrl { get; init; } = "http://127.0.0.1:5199";
        public string? Url { get; init; }
        public string? ReportPath { get; init; }
        public string? Cookie { get; init; }
        public string? UserAgent { get; init; }
        public bool Semantic { get; init; }
        public bool UseLlm { get; init; } = true;
        public bool Verbose { get; init; }
        public bool Help { get; init; }

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            bool scenarioSet = false;

            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (arg is "--help" or "-h" or "/?")
                {
                    return options with { Help = true };
                }

                if (!arg.StartsWith('-'))
                {
                    if (scenarioSet)
                    {
                        throw new ArgumentException($"Лишний аргумент '{arg}'.");
                    }

                    options = options with { Scenario = arg };
                    scenarioSet = true;
                    continue;
                }

                string name = arg.TrimStart('-');
                string ReadValue()
                {
                    if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
                    {
                        throw new ArgumentException($"Для --{name} требуется значение.");
                    }

                    return args[++index];
                }

                options = name.ToLowerInvariant() switch
                {
                    "query" or "q" => options with { Query = ReadValue() },
                    "provider" or "providers" => options with { Providers = ReadValue() },
                    "max-pages" => options with { MaxPages = ParseInt(ReadValue(), name, 1, 10) },
                    "waves" or "max-waves" => options with { MaxWaves = ParseInt(ReadValue(), name, 1, 4) },
                    "base-url" => options with { BaseUrl = ReadValue() },
                    "url" => options with { Url = ReadValue() },
                    "report" => options with { ReportPath = ReadValue() },
                    "cookie" => options with { Cookie = ReadValue() },
                    "user-agent" or "useragent" => options with { UserAgent = ReadValue() },
                    "semantic" => options with { Semantic = true },
                    "use-llm" => options with { UseLlm = ParseBool(ReadValue()) },
                    "verbose" => options with { Verbose = true },
                    _ => throw new ArgumentException($"Неизвестный параметр '--{name}'.")
                };
            }

            return options;
        }

        public static List<string>? ParseProviders(string? providers)
            => string.IsNullOrWhiteSpace(providers)
                ? null
                : providers
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

        public static void PrintUsage()
        {
            Console.WriteLine("""
                Использование:
                  dotnet run --project src/MultiWaveScraper.Tester -- [сценарий] [параметры]

                Сценарии:
                  env      Проверка .NET, LM Studio и поисковых провайдеров
                  llm      Генерация ключей волн 2–4 через LLM (с fallback на правила)
                  search   Только SERP discovery (список URL)
                  scrape   Извлечение Markdown одной страницы (demo-сервер, если --url не задан)
                  waves    Полный многоволновой скрейпинг с внешними провайдерами
                  offline  Полный пайплайн на встроенных demo-страницах без сети
                  interactive  Интерактивный режим: вводите запросы, /help — команды
                  all      env + llm + offline + scrape + search + waves

                Параметры:
                  --query <текст>        Поисковый ключ (по умолчанию "vector database")
                  --provider <список>    Провайдеры через запятую: wikipedia,bing,duckduckgo,searxng,static
                  --max-pages <1..10>    Страниц на запрос (по умолчанию 2)
                  --waves <1..4>         Количество волн (по умолчанию 4)
                  --url <URL>            URL для сценария scrape
                  --cookie <строка>      Cookie для скачиваемых страниц, например "session=..."
                  --user-agent <строка>  User-Agent (по умолчанию Chrome)
                  --report <путь.json>   Сохранить полный отчёт в JSON
                  --semantic             Включить embedding-оценку близости
                  --use-llm <true|false> Использовать LLM для волн 2–4 (по умолчанию true)
                  --verbose              Подробные логи пайплайна
                  --help                 Эта справка

                Переменные окружения с префиксом MWS_ переопределяют appsettings.json,
                например MWS_Scraper__PerHostDelayMs=0.
                """);
        }

        private static int ParseInt(string value, string name, int min, int max)
        {
            if (!int.TryParse(value, out int parsed) || parsed < min || parsed > max)
            {
                throw new ArgumentException($"--{name} должен быть числом {min}..{max}.");
            }

            return parsed;
        }

        private static bool ParseBool(string value)
            => value.ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => throw new ArgumentException("--use-llm принимает true или false.")
            };
    }
}
