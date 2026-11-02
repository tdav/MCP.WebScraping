using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MultiWaveScraper.Core.Llm;

/// <summary>
/// Фабрика клиентов LM Studio через OpenAI-совместимый API.
/// Один экземпляр используется и MCP-сервером, и консольным агентом.
/// </summary>
public static class LmStudioClientFactory
{
    public static IChatClient CreateChatClient(LlmOptions options, string? model = null)
    {
        OpenAIClient client = CreateOpenAiClient(options);
        return client.GetChatClient(model ?? options.Model).AsIChatClient();
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LlmOptions options)
    {
        OpenAIClient client = CreateOpenAiClient(options);
        return client.GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator();
    }

    public static OpenAIClient CreateOpenAiClient(LlmOptions options)
    {
        string apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "lm-studio" : options.ApiKey;
        return new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(options.BaseUrl.TrimEnd('/')),
                NetworkTimeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds))
            });
    }

    /// <summary>Список моделей из GET {BaseUrl}/models.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(LlmOptions options, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string baseUrl = options.BaseUrl.TrimEnd('/');
        string json = await httpClient.GetStringAsync($"{baseUrl}/models", cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data))
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }
}
