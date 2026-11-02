using System.Security.Cryptography;

namespace MultiWaveScraper.Core.Infrastructure;

/// <summary>
/// SHA256-отпечаток контента для точной дедупликации и 64-битный SimHash
/// для поиска почти-дубликатов (зеркал) по расстоянию Хэмминга.
/// </summary>
public static class ContentFingerprint
{
    public static string Sha256(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static ulong SimHash(string text)
    {
        var vector = new int[64];
        foreach (string token in TextTools.Tokenize(text))
        {
            ulong hash = Fnv1a64(token);
            for (int i = 0; i < 64; i++)
            {
                vector[i] += ((hash >> i) & 1UL) == 1UL ? 1 : -1;
            }
        }

        ulong result = 0;
        for (int i = 0; i < 64; i++)
        {
            if (vector[i] > 0)
            {
                result |= 1UL << i;
            }
        }

        return result;
    }

    public static int HammingDistance(ulong a, ulong b)
        => System.Numerics.BitOperations.PopCount(a ^ b);

    private static ulong Fnv1a64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }
}
