using FreneticUtilities.FreneticExtensions;
using SwarmUI.Utils;

namespace VideoStages;

/// <summary>Lexes prompt text into leading prose and tag-shaped pieces without interpreting tag values.</summary>
internal static class PromptTagScanner
{
    internal readonly record struct Piece(string Text, bool IsLeadingText, int TagEnd)
    {
        public bool HasTag => TagEnd >= 0;
        public string Tag => Text[..TagEnd];
        public string Content => Text[(TagEnd + 1)..];
    }

    public static IEnumerable<Piece> Scan(string prompt)
    {
        string[] pieces = prompt.Split('<');
        yield return new Piece(pieces[0], IsLeadingText: true, TagEnd: -1);
        foreach (string piece in pieces.Skip(1))
        {
            if (!string.IsNullOrEmpty(piece))
            {
                yield return new Piece(piece, IsLeadingText: false, piece.IndexOf('>'));
            }
        }
    }

    public static string ExtractPrefixLower(string tag)
    {
        string prefix = tag.BeforeAndAfter(':', out _);
        int slash = prefix.IndexOf('/');
        if (slash != -1)
        {
            prefix = prefix[..slash];
        }
        if (prefix.EndsWith(']') && prefix.Contains('['))
        {
            prefix = prefix[..prefix.LastIndexOf('[')];
        }
        return prefix.ToLowerInvariant();
    }

    public static bool IsSectionStartingTag(string prefixLower)
    {
        if (BuiltInSectionStarters.Contains(prefixLower))
        {
            return true;
        }
        foreach (string prefix in PromptRegion.CustomPartPrefixes)
        {
            if (StringUtils.Equals(prefix, prefixLower))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> BuiltInSectionStarters = [
        "base", "refiner", "pixeldecoder", "video", "videoswap", "edit",
        "region", "segment", "object", "clear", "extend"
    ];
}
