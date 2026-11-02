using System.Diagnostics;

namespace MultiWaveScraper.Core.Expansion;

/// <summary>
/// Гибридный генератор: сначала LLM (structured output), при ошибке/тайм-ауте —
/// детерминированные правила. Никогда не «ломает» пайплайн из-за недоступной модели.
/// </summary>
public sealed class QueryExpansionService(
    LlmQueryExpansionService llm,
    RuleBasedQueryExpander rules,
    IOptionsMonitor<LlmOptions> llmOptions,
    ILogger<QueryExpansionService> logger) : IQueryExpansionService
{
    public async Task<ExpansionResult> GenerateAsync(
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxQueries,
        bool enableLlm,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (wave == WaveType.ExactMatch)
        {
            return new ExpansionResult { Queries = [baseQuery], Source = "exact", Model = "none" };
        }

        string? llmError = null;
        if (enableLlm && llmOptions.CurrentValue.EnableLlmExpansion)
        {
            ExpansionResult result = await llm
                .ExpandAsync(baseQuery, wave, discoveredTitles, maxQueries, cancellationToken)
                .ConfigureAwait(false);

            if (result.Queries.Count > 0)
            {
                return result;
            }

            llmError = result.Error;
            logger.LogWarning("LLM не дала ключей для волны {Wave}: {Error}. Используем правила.", wave, llmError);
        }

        IReadOnlyList<string> fallback = rules.Expand(baseQuery, wave, discoveredTitles, maxQueries);
        return new ExpansionResult
        {
            Queries = fallback,
            Source = enableLlm ? "rules-fallback" : "rules",
            Model = llmOptions.CurrentValue.Model,
            Error = llmError,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }
}
