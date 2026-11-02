using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpClientType = ModelContextProtocol.Client.McpClient;

namespace MultiWaveScraper.McpClient;

/// <summary>
/// Консольный MCP-клиент: подключается к серверу по HTTP (Streamable HTTP)
/// или stdio, показывает инструменты, вызывает demo-инструменты или произвольный tool.
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

        try
        {
            IClientTransport transport = CreateTransport(cli);

            Console.WriteLine($"Подключение ({cli.Transport})…");
            await using McpClientType client = await McpClientType.CreateAsync(transport, cancellationToken: cancellation.Token);

            Console.WriteLine($"Сервер: {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
            if (!string.IsNullOrWhiteSpace(client.ServerInstructions))
            {
                Console.WriteLine($"Инструкции: {client.ServerInstructions}");
            }

            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
            Console.WriteLine($"Инструментов: {tools.Count}");
            foreach (McpClientTool tool in tools)
            {
                Console.WriteLine($"  • {tool.Name,-24} {Trim(tool.Description, 100)}");
            }

            Console.WriteLine();

            if (cli.ListToolsOnly)
            {
                return 0;
            }

            if (cli.ToolName is not null)
            {
                CallToolResult result = await client.CallToolAsync(
                    cli.ToolName,
                    cli.Arguments,
                    cancellationToken: cancellation.Token);
                PrintResult(result);
                return result.IsError == true ? 1 : 0;
            }

            await RunDemoAsync(client, cli, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Отменено пользователем.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка MCP-клиента: {exception.Message}");
            if (cli.Verbose)
            {
                Console.Error.WriteLine(exception);
            }

            return 1;
        }
    }

    private static IClientTransport CreateTransport(CliOptions cli)
    {
        if (string.Equals(cli.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            string serverDll = cli.ServerDll ?? LocateServerDll()
                ?? throw new InvalidOperationException(
                    "Не найден MultiWaveScraper.McpServer.dll. Укажите --server <path> или соберите решение.");

            Console.WriteLine($"stdio: dotnet {serverDll} --stdio");
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "multi-wave-scraper-stdio",
                Command = "dotnet",
                Arguments = [serverDll, "--stdio"],
                WorkingDirectory = Path.GetDirectoryName(serverDll)
            });
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "multi-wave-scraper-http",
            Endpoint = new Uri(cli.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(20)
        });
    }

    private static async Task RunDemoAsync(McpClientType client, CliOptions cli, CancellationToken cancellationToken)
    {
        Console.WriteLine("Demo 1: generate_wave_queries (правила, без сети)");
        CallToolResult waves = await client.CallToolAsync(
            "generate_wave_queries",
            new Dictionary<string, object?>
            {
                ["searchKey"] = cli.Query,
                ["maxWaves"] = 4,
                ["useLlm"] = cli.UseLlm
            },
            cancellationToken: cancellationToken);
        PrintResult(waves);

        Console.WriteLine();
        Console.WriteLine("Demo 2: discover_urls (SERP discovery)");
        var arguments = new Dictionary<string, object?>
        {
            ["searchKey"] = cli.Query,
            ["maxResults"] = 5
        };
        if (!string.IsNullOrWhiteSpace(cli.Providers))
        {
            arguments["providers"] = cli.Providers;
        }

        CallToolResult urls = await client.CallToolAsync(
            "discover_urls",
            arguments,
            cancellationToken: cancellationToken);
        PrintResult(urls);
    }

    private static void PrintResult(CallToolResult result)
    {
        if (result.IsError == true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Tool вернул ошибку:");
            Console.ResetColor();
        }

        foreach (ContentBlock block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                Console.WriteLine(text.Text);
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(block, JsonOptions));
            }
        }

        if (result.StructuredContent is JsonElement structured)
        {
            Console.WriteLine("StructuredContent:");
            Console.WriteLine(JsonSerializer.Serialize(structured, JsonOptions));
        }
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

    private static string Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "…";
    }

    private sealed record CliOptions
    {
        public string Transport { get; init; } = "http";
        public string Url { get; init; } = "http://127.0.0.1:5199/mcp";
        public string? ServerDll { get; init; }
        public string Query { get; init; } = "vector database";
        public string? Providers { get; init; }
        public string? ToolName { get; init; }
        public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();
        public bool UseLlm { get; init; }
        public bool ListToolsOnly { get; init; }
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
                    "transport" or "t" => options with { Transport = ReadValue() },
                    "url" => options with { Url = ReadValue() },
                    "server" or "server-dll" => options with { ServerDll = ReadValue() },
                    "query" or "q" => options with { Query = ReadValue() },
                    "provider" or "providers" => options with { Providers = ReadValue() },
                    "tool" => options with { ToolName = ReadValue() },
                    "args" => options with { Arguments = ParseArguments(ReadValue()) },
                    "use-llm" => options with { UseLlm = ParseBool(ReadValue()) },
                    "list" or "list-tools" => options with { ListToolsOnly = true },
                    "verbose" => options with { Verbose = true },
                    _ => throw new ArgumentException($"Неизвестный параметр '--{name}'.")
                };
            }

            if (options.Transport is not ("http" or "stdio"))
            {
                throw new ArgumentException("--transport должен быть http или stdio.");
            }

            if (options.ToolName is not null && options.ToolName.Length == 0)
            {
                throw new ArgumentException("--tool требует имя инструмента.");
            }

            return options;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
                Использование:
                  dotnet run --project src/MultiWaveScraper.McpClient -- [параметры]

                Подключение:
                  --transport <http|stdio>  Транспорт (по умолчанию http)
                  --url <URL>               MCP endpoint (по умолчанию http://127.0.0.1:5199/mcp)
                  --server <path.dll>       Сервер для stdio (по умолчанию ищется в src/.../bin)

                Действия:
                  (без параметров)          Показать инструменты и выполнить demo-вызовы
                  --list-tools              Только список инструментов
                  --tool <имя>              Вызвать конкретный инструмент
                  --args '<json>'           Аргументы для --tool, например '{"searchKey":"MCP"}'
                  --query <текст>           Поисковый ключ для demo
                  --provider <список>       Провайдеры для discover_urls
                  --use-llm <true|false>    LLM для generate_wave_queries (по умолчанию false)
                  --verbose                 Печатать стек ошибок
                  --help                    Эта справка

                Примеры:
                  dotnet run --project src/MultiWaveScraper.McpClient
                  dotnet run --project src/MultiWaveScraper.McpClient -- --transport stdio --list-tools
                  dotnet run --project src/MultiWaveScraper.McpClient -- --tool multi_wave_scrape --args "{\"searchKey\":\"vector database\",\"maxWaves\":2,\"maxPagesPerQuery\":1}"
                """);
        }

        private static IReadOnlyDictionary<string, object?> ParseArguments(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("--args должен содержать JSON-объект.");
                }

                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                       ?? new Dictionary<string, object?>();
            }
            catch (JsonException exception)
            {
                throw new ArgumentException($"Некорректный JSON в --args: {exception.Message}");
            }
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
