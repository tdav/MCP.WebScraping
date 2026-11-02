namespace MultiWaveScraper.Core.Expansion;

public interface IQueryExpansionService
{
    Task<ExpansionResult> GenerateAsync(
        string baseQuery,
        WaveType wave,
        IReadOnlyList<string> discoveredTitles,
        int maxQueries,
        bool enableLlm,
        CancellationToken cancellationToken);
}
