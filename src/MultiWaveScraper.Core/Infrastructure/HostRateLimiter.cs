namespace MultiWaveScraper.Core.Infrastructure;

/// <summary>
/// Ограничитель частоты запросов на один хост (plan2, шаг 6).
/// Позволяет не получить 429 Too Many Requests при параллельном скрейпинге.
/// </summary>
public sealed class HostRateLimiter(IOptionsMonitor<ScraperOptions> options)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new(StringComparer.OrdinalIgnoreCase);

    public async Task WaitAsync(string host, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int delayMs = Math.Max(0, options.CurrentValue.PerHostDelayMs);
            if (delayMs > 0 && _lastRequest.TryGetValue(host, out DateTimeOffset last))
            {
                double waitMs = delayMs - (DateTimeOffset.UtcNow - last).TotalMilliseconds;
                if (waitMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequest[host] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }
}
