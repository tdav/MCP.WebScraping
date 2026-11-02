using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MultiWaveScraper.Core;
using MultiWaveScraper.McpServer.Api;
using MultiWaveScraper.McpServer.Tools;

string[] filteredArgs = args
    .Where(arg => !arg.Equals("--stdio", StringComparison.OrdinalIgnoreCase)
                  && !arg.Equals("--http", StringComparison.OrdinalIgnoreCase))
    .ToArray();

bool useStdio = args.Any(arg => arg.Equals("--stdio", StringComparison.OrdinalIgnoreCase))
                || string.Equals(Environment.GetEnvironmentVariable("MCP_TRANSPORT"), "stdio", StringComparison.OrdinalIgnoreCase);

if (useStdio)
{
    await RunStdioAsync(filteredArgs);
    return;
}

await RunHttpAsync(filteredArgs);
return;

static void ConfigureMcpServer(McpServerOptions options)
{
    options.ServerInfo = new Implementation
    {
        Name = "multi-wave-scraper",
        Version = "1.0.0"
    };
    options.ServerInstructions =
        "MCP-сервер многоволнового веб-скрейпинга. Инструмент multi_wave_scrape выполняет 4 волны: " +
        "точный ключ, альтернативы, синонимы и смежные темы, затем ищет URL и очищает страницы в Markdown. " +
        "Для длительных запусков используйте start_scrape_job + get_scrape_job.";
}

async Task RunStdioAsync(string[] commandLineArgs)
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = commandLineArgs,
        ContentRootPath = AppContext.BaseDirectory
    });

    builder.Configuration
        .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(commandLineArgs);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Information);

    builder.Services.AddMultiWaveScraper(builder.Configuration);
    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(ScraperTools).Assembly);

    await builder.Build().RunAsync();
}

async Task RunHttpAsync(string[] commandLineArgs)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = commandLineArgs,
        ContentRootPath = AppContext.BaseDirectory
    });

    builder.Configuration
        .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(commandLineArgs);

    int port = 5199;
    string? portArgument = args.SkipWhile(arg => !arg.Equals("--port", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
    if (portArgument is not null && int.TryParse(portArgument, out int parsedPort))
    {
        port = parsedPort;
    }
    else if (int.TryParse(builder.Configuration["port"] ?? builder.Configuration["Server:Port"], out parsedPort))
    {
        port = parsedPort;
    }

    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    builder.Services.AddMultiWaveScraper(builder.Configuration);
    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly(typeof(ScraperTools).Assembly);
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

    var app = builder.Build();
    app.UseCors();

    app.MapMcp("/mcp");
    app.MapRestEndpoints();
    app.MapDemoEndpoints();

    app.Logger.LogInformation(
        "MultiWaveScraper MCP server: REST http://127.0.0.1:{Port}/scrape, MCP http://127.0.0.1:{Port}/mcp",
        port,
        port);

    await app.RunAsync();
}
