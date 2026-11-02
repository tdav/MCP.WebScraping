using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using MultiWaveScraper.Core.Llm;
using MultiWaveScraper.Core.Models;

namespace MultiWaveScraper.Agent;

/// <summary>
/// Демонстрационный агент Microsoft Agent Framework: LM Studio (IChatClient)
/// + MCP-инструменты скрейпера, полученные по HTTP или stdio.
/// </summary>
internal static class Program
{
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

        if (cli.TimeoutSeconds > 0)
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(cli.TimeoutSeconds));
        }

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.SetMinimumLevel(cli.Verbose ? LogLevel.Information : LogLevel.Warning);
        });

        try
        {
            LlmOptions llm = LoadLlmOptions(cli);
            Console.WriteLine($"LM Studio: {llm.BaseUrl}, model={llm.Model}");

            IChatClient chatClient = LmStudioClientFactory.CreateChatClient(llm);

            IClientTransport? transport = cli.NoTools ? null : CreateTransport(cli);
            await using McpClient? mcpClient = transport is null
                ? null
                : await McpClient.CreateAsync(transport, cancellationToken: cancellation.Token);

            IList<AITool> tools = [];
            if (mcpClient is not null)
            {
                IList<McpClientTool> discovered = await mcpClient.ListToolsAsync(cancellationToken: cancellation.Token);
                Console.WriteLine($"MCP: {mcpClient.ServerInfo?.Name}, инструментов: {discovered.Count}");

                tools = SelectTools(discovered, cli.Tools);
                Console.WriteLine($"Агенту передано инструментов: {tools.Count} ({string.Join(", ", tools.Select(t => t.Name))})");
            }

            if (cli.ListToolsOnly)
            {
                return 0;
            }

            AIAgent agent = chatClient.AsAIAgent(
                instructions: BuildInstructions(cli),
                name: "MultiWaveScraperAgent",
                description: "Исследовательский агент с многоволновым веб-скрейпингом через MCP",
                tools: tools,
                loggerFactory: loggerFactory);

            string prompt = cli.Prompt ?? BuildPrompt(cli.Query);
            Console.WriteLine();
            Console.WriteLine($"Запрос: {prompt}");
            Console.WriteLine("--- ответ агента ---");

            if (cli.Stream)
            {
                await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
                                   prompt, cancellationToken: cancellation.Token))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        Console.Write(update.Text);
                    }
                }

                Console.WriteLine();
            }
            else
            {
                AgentResponse response = await agent.RunAsync(prompt, cancellationToken: cancellation.Token);
                Console.WriteLine(response.Text);

                List<FunctionCallContent> calls = response.Messages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();
                if (calls.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("Вызовы инструментов:");
                    foreach (FunctionCallContent call in calls)
                    {
                        Console.WriteLine($"  • {call.Name}({Trim(JsonSerializer.Serialize(call.Arguments), 160)})");
                    }
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Агент остановлен по тайм-ауту или сигналу Ctrl+C.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка агента: {exception.Message}");
            if (cli.Verbose)
            {
                Console.Error.WriteLine(exception);
            }

            return 1;
        }
    }

    private static string BuildInstructions(CliOptions cli)
    {
        if (cli.NoTools)
        {
            return "Ты — полезный ассистент. Отвечай кратко и по делу на русском языке.";
        }

        return """
            Ты — исследовательский агент с доступом к MCP-инструментам многоволнового веб-скрейпинга.
            Для исследовательских вопросов вызывай multi_wave_scrape. Для экономии контекста используй
            maxWaves=2, maxPagesPerQuery=1, includeContent=false. Если нужен только список источников,
            вызывай discover_urls. Не выдумывай URL и факты: опирайся только на результаты инструментов.
            В конце дай краткое резюме (3–5 пунктов) и список источников вида "Название — URL".
            Отвечай на русском языке, структурированно и без лишних рассуждений.
            """;
    }

    private static string BuildPrompt(string query)
        => $"Найди актуальные источники по теме «{query}». " +
           "Сначала вызови multi_wave_scrape (maxWaves=2, maxPagesPerQuery=1, includeContent=false), " +
           "затем перечисли 3–5 самых релевантных источников и сделай 3 коротких вывода.";

    private static IList<AITool> SelectTools(IList<McpClientTool> discovered, string selector)
    {
        IEnumerable<McpClientTool> selected = selector.Trim().ToLowerInvariant() switch
        {
            "all" => discovered,
            "none" => [],
            "core" => discovered.Where(tool =>
                tool.Name is "multi_wave_scrape" or "discover_urls"
                    or "generate_wave_queries" or "scrape_single_url" or "list_scrape_providers"),
            "scrape" => discovered.Where(tool =>
                tool.Name is "multi_wave_scrape" or "discover_urls" or "scrape_single_url"),
            _ => discovered.Where(tool => selector
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        };

        return selected.Cast<AITool>().ToList();
    }

    private static IClientTransport CreateTransport(CliOptions cli)
    {
        if (string.Equals(cli.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            string serverDll = cli.ServerDll ?? LocateServerDll()
                ?? throw new InvalidOperationException(
                    "Не найден MultiWaveScraper.McpServer.dll. Укажите --server <path> или соберите решение.");

            Console.WriteLine($"MCP transport: stdio ({serverDll})");
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "multi-wave-scraper-stdio",
                Command = "dotnet",
                Arguments = [serverDll, "--stdio"],
                WorkingDirectory = Path.GetDirectoryName(serverDll)
            });
        }

        Console.WriteLine($"MCP transport: {cli.Transport} ({cli.Url})");
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "multi-wave-scraper-http",
            Endpoint = new Uri(cli.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(20)
        });
    }

    private static LlmOptions LoadLlmOptions(CliOptions cli)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("MWS_")
            .Build();

        LlmOptions options = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
        if (cli.Model is not null)
        {
            options.Model = cli.Model;
        }

        if (cli.BaseUrl is not null)
        {
            options.BaseUrl = cli.BaseUrl;
        }

        return options;
    }

    private static string? LocateServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                string candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "MultiWaveScraper.McpServer",
                    "bin",
                    configuration,
                    "net11.0",
                    "MultiWaveScraper.McpServer.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private sealed record CliOptions
    {
        public string? Prompt { get; init; }
        public string Query { get; init; } = "vector database";
        public string Transport { get; init; } = "http";
        public string Url { get; init; } = "http://127.0.0.1:5199/mcp";
        public string? ServerDll { get; init; }
        public string Tools { get; init; } = "scrape";
        public string? Model { get; init; }
        public string? BaseUrl { get; init; }
        public bool NoTools { get; init; }
        public bool Stream { get; init; }
        public bool ListToolsOnly { get; init; }
        public int TimeoutSeconds { get; init; } = 240;
        public bool Verbose { get; init; }
        public bool Help { get; init; }

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (arg is "--help" or "-h" or "/?")
                {
                    return options with { Help = true };
                }

                if (!arg.StartsWith('-'))
                {
                    throw new ArgumentException($"Лишний аргумент '{arg}'.");
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
                    "prompt" or "p" => options with { Prompt = ReadValue() },
                    "query" or "q" => options with { Query = ReadValue() },
                    "transport" or "t" => options with { Transport = ReadValue() },
                    "url" => options with { Url = ReadValue() },
                    "server" or "server-dll" => options with { ServerDll = ReadValue() },
                    "tools" => options with { Tools = ReadValue() },
                    "model" => options with { Model = ReadValue() },
                    "base-url" => options with { BaseUrl = ReadValue() },
                    "no-tools" => options with { NoTools = true },
                    "stream" => options with { Stream = true },
                    "list-tools" => options with { ListToolsOnly = true },
                    "timeout" => options with { TimeoutSeconds = ParseInt(ReadValue(), name, 0, 3600) },
                    "verbose" => options with { Verbose = true },
                    _ => throw new ArgumentException($"Неизвестный параметр '--{name}'.")
                };
            }

            if (options.Transport is not ("http" or "stdio"))
            {
                throw new ArgumentException("--transport должен быть http или stdio.");
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
                Использование:
                  dotnet run --project src/MultiWaveScraper.Agent -- [параметры]

                Модель и MCP:
                  --model <id>              Модель LM Studio (по умолчанию из appsettings: lfm3-vl-2b)
                  --base-url <URL>          OpenAI-совместимый endpoint LM Studio
                  --transport <http|stdio>  Транспорт MCP (по умолчанию http)
                  --url <URL>               MCP endpoint (по умолчанию http://127.0.0.1:5199/mcp)
                  --server <path.dll>       Сервер для stdio
                  --tools <scrape|core|all|none|имена>  Набор инструментов (по умолчанию scrape)
                  --no-tools                Не подключать MCP (обычный чат)

                Запрос:
                  --prompt <текст>          Готовый промпт агенту
                  --query <текст>           Тема для встроенного промпта (по умолчанию "vector database")
                  --stream                  Потоковый вывод
                  --timeout <сек>           Общий тайм-аут (по умолчанию 240; 0 — без тайм-аута)
                  --list-tools              Только показать инструменты и выйти
                  --verbose                 Подробные логи и стек ошибок
                  --help                    Эта справка

                Примеры:
                  dotnet run --project src/MultiWaveScraper.Agent -- --query "MCP сервер"
                  dotnet run --project src/MultiWaveScraper.Agent -- --transport stdio --query "vector database" --stream
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
    }
}
