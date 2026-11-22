using System.Text;

namespace UniversalPlayer.Core.Text;

/// <summary>
/// Repairs text that was stored with the wrong encoding - the classic case being Cyrillic
/// (cp1251) bytes that were decoded as Western (cp1252), so "Король и Шут" ends up as
/// "Êîðîëü è Øóò". Only touches strings that look broken, so correctly encoded text
/// (including Latin names with accents) is left alone.
/// </summary>
public static class TextRepair
{
    static TextRepair()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* provider already registered */ }
    }

    /// <summary>Returns the repaired text, or null when the input already looks correct.</summary>
    public static string? Fix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        int letters = 0, suspiciousLatin1 = 0, cyrillic = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch >= '\u00A0' && ch <= '\u00FF') suspiciousLatin1++;
            if (IsCyrillic(ch)) cyrillic++;
        }

        if (letters == 0) return null;
        if (cyrillic * 2 > letters) return null;                  // already mostly Cyrillic
        if (suspiciousLatin1 * 10 < letters * 3) return null;     // fewer than ~30% odd characters

        string? best = null;
        var bestRatio = 0.0;
        foreach (var candidate in Candidates(value))
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\uFFFD')) continue;
            var ratio = CyrillicRatio(candidate);
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                best = candidate;
            }
        }

        // the repaired text has to be convincingly Cyrillic to count as a fix
        return bestRatio >= 0.5 ? best : null;
    }

    /// <summary>Applies <see cref="Fix"/> if it produces a result, otherwise returns the input.</summary>
    public static string FixOrKeep(string? value) => string.IsNullOrWhiteSpace(value)
        ? value ?? string.Empty
        : Fix(value) ?? value;

    private static IEnumerable<string?> Candidates(string value)
    {
        yield return Transcode(value, 1252, 1251);   // cp1252 seen as bytes -> Cyrillic (main case)
        yield return Transcode(value, 1252, 866);    // -> DOS Cyrillic
        yield return Transcode(value, 1251, 65001);  // UTF-8 bytes seen as cp1251
        yield return Transcode(value, 1252, 65001);  // UTF-8 bytes seen as cp1252
    }

    private static string? Transcode(string value, int fromCodePage, int toCodePage)
    {
        try
        {
            var from = Encoding.GetEncoding(fromCodePage);
            var to = Encoding.GetEncoding(toCodePage);
            return to.GetString(from.GetBytes(value));
        }
        catch
        {
            return null;
        }
    }

    private static double CyrillicRatio(string value)
    {
        int letters = 0, cyrillic = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (IsCyrillic(ch)) cyrillic++;
        }
        return letters == 0 ? 0 : cyrillic / (double)letters;
    }

    private static bool IsCyrillic(char c) => c >= '\u0400' && c <= '\u04FF';
}
