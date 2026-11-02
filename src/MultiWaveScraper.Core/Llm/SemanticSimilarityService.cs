using Microsoft.Extensions.AI;

namespace MultiWaveScraper.Core.Llm;

public interface ISemanticSimilarityService
{
    bool IsEnabled { get; }
    Task<double?> ScoreAsync(string left, string right, CancellationToken cancellationToken);
}

/// <summary>Заглушка, когда эмбеддинги отключены.</summary>
public sealed class NullSemanticSimilarityService : ISemanticSimilarityService
{
    public bool IsEnabled => false;
    public Task<double?> ScoreAsync(string left, string right, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
}

/// <summary>
/// Косинусная близость через embedding-модель LM Studio.
/// Кэширует эмбеддинги в памяти, чтобы не дублировать запросы.
/// </summary>
public sealed class SemanticSimilarityService(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ILogger<SemanticSimilarityService> logger) : ISemanticSimilarityService
{
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(4, 4);

    public bool IsEnabled => true;

    public async Task<double?> ScoreAsync(string left, string right, CancellationToken cancellationToken)
    {
        try
        {
            float[] leftVector = await GetVectorAsync(left, cancellationToken).ConfigureAwait(false);
            float[] rightVector = await GetVectorAsync(right, cancellationToken).ConfigureAwait(false);
            return Cosine(leftVector, rightVector);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Эмбеддинги недоступны; семантическая оценка пропущена");
            return null;
        }
    }

    private async Task<float[]> GetVectorAsync(string text, CancellationToken cancellationToken)
    {
        string key = ContentFingerprint.Sha256(text);
        if (_cache.TryGetValue(key, out float[]? cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            GeneratedEmbeddings<Embedding<float>> embeddings = await generator
                .GenerateAsync([text], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            float[] vector = embeddings[0].Vector.ToArray();
            if (_cache.Count > 2000)
            {
                _cache.Clear();
            }

            _cache[key] = vector;
            return vector;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        double dot = 0, norma = 0, normb = 0;
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            norma += a[i] * a[i];
            normb += b[i] * b[i];
        }

        if (norma <= 0 || normb <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(norma) * Math.Sqrt(normb));
    }
}
