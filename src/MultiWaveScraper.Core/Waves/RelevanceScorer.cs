namespace MultiWaveScraper.Core.Waves;

using MultiWaveScraper.Core.Llm;

public sealed record RelevanceResult
{
    public MatchKind Kind { get; init; }
    public double Score { get; init; }
    public IReadOnlyList<string> Terms { get; init; } = [];
}

/// <summary>
/// Классификация страницы по типу совпадения (plan1):
/// точное совпадение → синонимы → похожие темы, плюс семантическая близость.
/// </summary>
public static class RelevanceScorer
{
    public static async Task<RelevanceResult> ScoreAsync(
        string baseQuery,
        IReadOnlyList<(WaveType Wave, string Query)> queries,
        ScrapedPage page,
        ISemanticSimilarityService? semantic,
        bool enableSemantic,
        CancellationToken cancellationToken)
    {
        string title = page.Title ?? "";
        string description = page.Description ?? "";
        string body = page.ScoredText ?? page.MarkdownContent ?? "";
        string head = body.Length > 2500 ? body[..2500] : body;
        string titleAndDescription = $"{title} {description}";

        var baseTokens = TextTools.TokenSet(baseQuery);
        var titleTokens = TextTools.TokenSet(titleAndDescription);
        var bodyTokens = TextTools.TokenSet(body);

        // 1. Точное совпадение фразы.
        if (TextTools.ContainsPhrase(title, baseQuery))
        {
            return new RelevanceResult
            {
                Kind = MatchKind.Exact,
                Score = 1.0,
                Terms = TextTools.FindTerms(baseTokens, titleTokens)
            };
        }

        if (TextTools.ContainsPhrase(head, baseQuery))
        {
            return new RelevanceResult
            {
                Kind = MatchKind.Exact,
                Score = 0.92,
                Terms = TextTools.FindTerms(baseTokens, bodyTokens)
            };
        }

        if (TextTools.ContainsPhrase(body, baseQuery))
        {
            return new RelevanceResult
            {
                Kind = MatchKind.Exact,
                Score = 0.85,
                Terms = TextTools.FindTerms(baseTokens, bodyTokens)
            };
        }

        // 2. Альтернативы: высокая доля токенов запроса.
        double titleCoverage = TextTools.Coverage(baseTokens, titleTokens);
        double bodyCoverage = TextTools.Coverage(baseTokens, bodyTokens);

        if (titleCoverage >= 0.75 || baseTokens.Count <= 2 && titleCoverage >= 0.5)
        {
            return new RelevanceResult
            {
                Kind = MatchKind.Alternative,
                Score = Math.Clamp(0.72 + (titleCoverage * 0.12), 0, 0.9),
                Terms = TextTools.FindTerms(baseTokens, titleTokens)
            };
        }

        if (bodyCoverage >= 0.8)
        {
            return new RelevanceResult
            {
                Kind = MatchKind.Alternative,
                Score = Math.Clamp(0.62 + (bodyCoverage * 0.1), 0, 0.85),
                Terms = TextTools.FindTerms(baseTokens, bodyTokens)
            };
        }

        // 3. Синонимы: совпадение фраз из волны синонимов.
        var synonymQueries = queries
            .Where(q => q.Wave == WaveType.Synonyms)
            .Select(q => q.Query)
            .Where(q => !string.Equals(q, baseQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string synonymQuery in synonymQueries)
        {
            if (TextTools.ContainsPhrase(titleAndDescription, synonymQuery)
                || TextTools.ContainsPhrase(head, synonymQuery))
            {
                return new RelevanceResult
                {
                    Kind = MatchKind.Synonym,
                    Score = 0.78,
                    Terms = [synonymQuery]
                };
            }

            var synonymTokens = TextTools.TokenSet(synonymQuery);
            double coverage = TextTools.Coverage(synonymTokens, bodyTokens);
            if (coverage >= 0.7 && synonymTokens.Count > 1)
            {
                return new RelevanceResult
                {
                    Kind = MatchKind.Synonym,
                    Score = Math.Clamp(0.6 + coverage * 0.12, 0, 0.75),
                    Terms = TextTools.FindTerms(synonymTokens, bodyTokens)
                };
            }
        }

        // 4. Похожие темы: семантика или пересечение терминов.
        double? semanticScore = null;
        if (enableSemantic && semantic is { IsEnabled: true })
        {
            string sample = body.Length > 2000 ? body[..2000] : body;
            semanticScore = await semantic.ScoreAsync(baseQuery, sample, cancellationToken).ConfigureAwait(false);
        }

        double jaccard = TextTools.Jaccard(baseTokens, bodyTokens);
        if ((semanticScore is >= 0.55) || jaccard >= 0.12 || bodyCoverage >= 0.55)
        {
            double score = Math.Max(semanticScore ?? 0, bodyCoverage);
            return new RelevanceResult
            {
                Kind = MatchKind.Related,
                Score = Math.Clamp(0.45 + (score * 0.25), 0, 0.7),
                Terms = TextTools.FindTerms(baseTokens, bodyTokens)
            };
        }

        return new RelevanceResult
        {
            Kind = MatchKind.None,
            Score = 0.2,
            Terms = TextTools.FindTerms(baseTokens, bodyTokens)
        };
    }
}
