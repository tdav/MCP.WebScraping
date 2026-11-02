namespace MultiWaveScraper.Core.Models;

/// <summary>
/// Четыре волны расширения поискового запроса (объединение plan1 и plan2).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaveType
{
    /// <summary>Волна 1 — точный ключ.</summary>
    ExactMatch = 1,

    /// <summary>Волна 2 — альтернативные формулировки (перефразирование).</summary>
    Alternatives = 2,

    /// <summary>Волна 3 — синонимы и профессиональные термины.</summary>
    Synonyms = 3,

    /// <summary>Волна 4 — смежные темы (латеральное расширение).</summary>
    RelatedTopics = 4
}

/// <summary>
/// Классификация страницы по типу совпадения с исходным запросом (логика plan1).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchKind
{
    None = 0,
    Exact = 1,
    Alternative = 2,
    Synonym = 3,
    Related = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScrapeJobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public static class WaveNames
{
    public static string Describe(WaveType wave) => wave switch
    {
        WaveType.ExactMatch => "точный ключ (exact)",
        WaveType.Alternatives => "альтернативные формулировки (alternatives)",
        WaveType.Synonyms => "синонимы (synonyms)",
        WaveType.RelatedTopics => "смежные темы (related topics)",
        _ => wave.ToString()
    };

    public static string ShortName(WaveType wave) => wave switch
    {
        WaveType.ExactMatch => "wave1_exact",
        WaveType.Alternatives => "wave2_alternatives",
        WaveType.Synonyms => "wave3_synonyms",
        WaveType.RelatedTopics => "wave4_related",
        _ => $"wave{(int)wave}"
    };

    public static bool TryParse(string value, out WaveType wave)
    {
        wave = WaveType.ExactMatch;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (int.TryParse(value, out int number) && number is >= 1 and <= 4)
        {
            wave = (WaveType)number;
            return true;
        }

        if (value.StartsWith("wave", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
            if (int.TryParse(value, out number) && number is >= 1 and <= 4)
            {
                wave = (WaveType)number;
                return true;
            }
        }

        return Enum.TryParse(value, ignoreCase: true, out wave);
    }
}
