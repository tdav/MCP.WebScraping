namespace MultiWaveScraper.Core.Infrastructure;

/// <summary>Токенизация, нормализация текста и метрики похожести.</summary>
public static partial class TextTools
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // EN
        "the", "a", "an", "and", "or", "but", "if", "then", "else", "for", "of", "to", "in", "on",
        "at", "by", "with", "from", "as", "is", "are", "was", "were", "be", "been", "being", "it",
        "this", "that", "these", "those", "what", "which", "who", "whom", "how", "why", "when",
        "where", "can", "could", "should", "would", "will", "shall", "may", "might", "do", "does",
        "did", "not", "no", "yes", "you", "your", "we", "our", "they", "their", "he", "she", "his",
        "her", "its", "about", "into", "than", "so", "such", "also", "more", "most", "all", "any",
        "some", "other", "using", "use", "used", "get", "got", "one", "two", "via", "vs",
        // RU
        "и", "в", "во", "не", "что", "он", "на", "я", "с", "со", "как", "а", "то", "все", "она",
        "так", "его", "но", "да", "ты", "к", "у", "же", "вы", "за", "бы", "по", "только", "ее",
        "мне", "было", "вот", "от", "меня", "еще", "нет", "о", "из", "ему", "теперь", "когда",
        "даже", "ну", "вдруг", "ли", "если", "уже", "или", "ни", "быть", "был", "него", "до",
        "вас", "нибудь", "опять", "уж", "вам", "ведь", "там", "потом", "себя", "ничего", "ей",
        "может", "они", "тут", "где", "есть", "надо", "ней", "для", "мы", "тебя", "их", "чем",
        "была", "сам", "чтоб", "без", "будто", "чего", "раз", "тоже", "себе", "под", "будет",
        "ж", "тогда", "кто", "этот", "того", "потому", "этого", "какой", "совсем", "ним", "здесь",
        "этом", "один", "почти", "мой", "тем", "чтобы", "нее", "сейчас", "были", "куда", "зачем",
        "всех", "никогда", "можно", "при", "наконец", "два", "об", "другой", "хоть", "после",
        "над", "больше", "тот", "через", "эти", "нас", "про", "всего", "них", "какая", "много",
        "разве", "три", "эту", "моя", "впрочем", "хорошо", "свою", "этой", "перед", "иногда",
        "лучше", "чуть", "том", "нельзя", "такой", "им", "более", "всегда", "конечно", "всю",
        "между", "это", "эта", "этих", "которые", "который", "которая", "которое", "которых",
        "является", "являются", "также", "были", "будет", "будто", "менее"
    };

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex TokenSeparator();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTags();

    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    /// <summary>Нормализация Markdown с сохранением переводов строк и структуры блоков.</summary>
    public static string NormalizeMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static string StripHtml(string html)
        => string.IsNullOrEmpty(html) ? "" : NormalizeWhitespace(WebUtility.HtmlDecode(HtmlTags().Replace(html, " ")));

    public static IReadOnlyList<string> Tokenize(string? text, bool removeStopWords = true)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        foreach (string raw in TokenSeparator().Split(text.ToLowerInvariant()))
        {
            if (raw.Length < 2 || raw.Length > 48)
            {
                continue;
            }

            if (removeStopWords && StopWords.Contains(raw))
            {
                continue;
            }

            tokens.Add(raw);
        }

        return tokens;
    }

    public static HashSet<string> TokenSet(string? text, bool removeStopWords = true)
        => [.. Tokenize(text, removeStopWords)];

    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>Доля токенов запроса, найденных в тексте (recall запроса).</summary>
    public static double Coverage(IReadOnlySet<string> queryTokens, IReadOnlySet<string> textTokens)
    {
        if (queryTokens.Count == 0)
        {
            return 0;
        }

        int hits = queryTokens.Count(textTokens.Contains);
        return (double)hits / queryTokens.Count;
    }

    public static IReadOnlyList<string> FindTerms(IReadOnlySet<string> queryTokens, IReadOnlySet<string> textTokens)
        => [.. queryTokens.Where(textTokens.Contains).Take(12)];

    public static int Levenshtein(string a, string b, int maxDistance = 4)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }

        if (Math.Abs(a.Length - b.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > maxDistance)
            {
                return maxDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    public static bool ContainsPhrase(string? haystack, string? needle)
        => !string.IsNullOrWhiteSpace(haystack)
           && !string.IsNullOrWhiteSpace(needle)
           && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool ContainsCyrillic(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c is >= '\u0400' and <= '\u04FF');

    public static string TruncateAtBoundary(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return text;
        }

        string slice = text[..maxChars];
        int boundary = Math.Max(
            slice.LastIndexOf("\n\n", StringComparison.Ordinal),
            Math.Max(slice.LastIndexOf(". ", StringComparison.Ordinal), slice.LastIndexOf("! ", StringComparison.Ordinal)));
        if (boundary < maxChars / 2)
        {
            boundary = maxChars;
        }

        return slice[..boundary].TrimEnd() + "\n\n…";
    }

    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int count = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }

    /// <summary>Извлекает верхние значимые термины (для related topics).</summary>
    public static IReadOnlyList<string> TopTerms(IEnumerable<string> texts, int maxTerms = 10)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string text in texts)
        {
            foreach (string token in Tokenize(text))
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(maxTerms)
            .Select(pair => pair.Key)
            .ToList();
    }
}
