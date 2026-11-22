namespace UniversalPlayer.Core.Genres;

using System.Text;

/// <summary>
/// Canonical genre vocabulary plus a normaliser that maps the many labels seen in
/// ID3 tags and online databases onto a small, consistent set. Lives in Core so both the
/// scanner (tags) and the classifier (online / local model) share the same vocabulary.
/// </summary>
public static class GenreCatalog
{
    public static readonly string[] Canonical =
    {
        "Rock", "Hard Rock", "Metal", "Punk", "Alternative", "Indie", "Grunge",
        "Pop", "Electronic", "Dance", "Hip-Hop", "Rap", "R&B", "Soul", "Funk", "Disco",
        "Jazz", "Blues", "Classical", "Instrumental", "Folk", "Country", "Reggae",
        "Latin", "World", "Ambient", "Soundtrack", "Industrial", "Chanson", "Bard", "Christmas"
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["electronica"] = "Electronic", ["edm"] = "Electronic", ["house"] = "Electronic",
        ["techno"] = "Electronic", ["trance"] = "Electronic", ["dubstep"] = "Electronic",
        ["drum and bass"] = "Electronic", ["dnb"] = "Electronic", ["synthpop"] = "Electronic",
        ["synth-pop"] = "Electronic", ["new wave"] = "Electronic", ["chillout"] = "Ambient",
        ["idm"] = "Electronic", ["electro"] = "Electronic", ["breakbeat"] = "Electronic",
        ["dance"] = "Dance", ["eurodance"] = "Dance", ["euro-disco"] = "Disco",
        ["heavy metal"] = "Metal", ["thrash metal"] = "Metal", ["death metal"] = "Metal",
        ["black metal"] = "Metal", ["nu metal"] = "Metal", ["power metal"] = "Metal",
        ["symphonic metal"] = "Metal", ["industrial metal"] = "Metal", ["metalcore"] = "Metal",
        ["alternative metal"] = "Metal", ["rap metal"] = "Metal", ["stoner rock"] = "Rock",
        ["hardrock"] = "Hard Rock", ["hard-rock"] = "Hard Rock", ["glam rock"] = "Hard Rock",
        ["classic rock"] = "Rock", ["progressive rock"] = "Rock", ["psychedelic rock"] = "Rock",
        ["garage rock"] = "Rock", ["post-rock"] = "Rock", ["indie rock"] = "Indie",
        ["alternative rock"] = "Alternative", ["alt rock"] = "Alternative", ["alt-rock"] = "Alternative",
        ["pop rock"] = "Rock", ["soft rock"] = "Rock", ["punk rock"] = "Punk", ["pop punk"] = "Punk",
        ["post-punk"] = "Punk", ["grunge"] = "Grunge", ["hardcore"] = "Punk",
        ["pop music"] = "Pop", ["dance pop"] = "Pop", ["electropop"] = "Pop", ["k-pop"] = "Pop",
        ["russian pop"] = "Pop", ["ruspop"] = "Pop", ["estrada"] = "Pop",
        ["hip hop"] = "Hip-Hop", ["hiphop"] = "Hip-Hop", ["rap"] = "Rap", ["trap"] = "Hip-Hop",
        ["rnb"] = "R&B", ["r&b"] = "R&B", ["rhythm and blues"] = "R&B", ["neo soul"] = "Soul",
        ["motown"] = "Soul",
        ["swing"] = "Jazz", ["bebop"] = "Jazz", ["big band"] = "Jazz", ["smooth jazz"] = "Jazz",
        ["blues rock"] = "Blues", ["delta blues"] = "Blues",
        ["orchestral"] = "Classical", ["opera"] = "Classical", ["baroque"] = "Classical",
        ["romantic"] = "Classical", ["piano"] = "Instrumental", ["soundtrack"] = "Soundtrack",
        ["film score"] = "Soundtrack", ["score"] = "Soundtrack", ["ambient"] = "Ambient",
        ["acoustic"] = "Folk", ["singer-songwriter"] = "Folk",
        ["авторская песня"] = "Bard", ["bard"] = "Bard", ["chanson"] = "Chanson",
        ["шансон"] = "Chanson", ["русский рок"] = "Rock", ["russian rock"] = "Rock",
        ["ska"] = "Reggae", ["reggaeton"] = "Latin", ["kids"] = "Pop", ["christmas"] = "Christmas",
        ["drum & bass"] = "Electronic", ["drum'n'bass"] = "Electronic", ["jungle"] = "Electronic",
        ["dub step"] = "Electronic", ["chill"] = "Ambient", ["chill-out"] = "Ambient",
        ["rnr"] = "Rock", ["rock'n'roll"] = "Rock", ["rock and roll"] = "Rock", ["rockabilly"] = "Rock",
        ["industrial"] = "Industrial", ["industrial rock"] = "Industrial", ["ebm"] = "Industrial",
        ["электронная музыка"] = "Electronic", ["электроника"] = "Electronic", ["электронная"] = "Electronic",
        ["ballad"] = "Pop", ["polka"] = "Folk", ["фолк-рок"] = "Folk",
        ["хип-хоп"] = "Hip-Hop", ["рэп"] = "Rap", ["джаз"] = "Jazz", ["блюз"] = "Blues",
        ["классика"] = "Classical", ["классическая музыка"] = "Classical", ["саундтрек"] = "Soundtrack",
        ["панк"] = "Punk", ["металл"] = "Metal", ["хэви-метал"] = "Metal", ["хард-рок"] = "Hard Rock",
        ["поп"] = "Pop", ["рок"] = "Rock", ["шансон"] = "Chanson",
        // localized labels returned by the online databases (Deezer localises genres)
        ["rok"] = "Rock", ["rock'n'roll"] = "Rock", ["rokenrol"] = "Rock", ["alternatywa"] = "Alternative",
        ["rock alternatywny"] = "Alternative", ["muzyka alternatywna"] = "Alternative",
        ["muzyka"] = "Pop", ["muzyka rozrywkowa"] = "Pop", ["rozrywkowa"] = "Pop",
        ["muzyka klasyczna"] = "Classical", ["klasyczna"] = "Classical", ["klasyka"] = "Classical",
        ["muzyka elektroniczna"] = "Electronic", ["elektronika"] = "Electronic", ["elektroniczna"] = "Electronic",
        ["muzyka taneczna"] = "Dance", ["taniec"] = "Dance", ["taneczna"] = "Dance",
        ["muzyka folkowa"] = "Folk", ["folkowa"] = "Folk", ["muzyka ludowa"] = "Folk",
        ["muzyka filmowa"] = "Soundtrack", ["ścieżka dźwiękowa"] = "Soundtrack", ["swiateczna"] = "Christmas",
        ["świąteczna"] = "Christmas", ["bożonarodzeniowa"] = "Christmas",
        ["dla dzieci"] = "Pop", ["dziecięca"] = "Pop", ["dziecieca"] = "Pop",
        ["etniczna"] = "World", ["muzyka świata"] = "World", ["latino"] = "Latin", ["latynoska"] = "Latin",
        ["reggaeton"] = "Latin", ["rock and roll"] = "Rock", ["alternativ"] = "Alternative",
        ["музыка"] = "Pop", ["эстрада"] = "Pop", ["рок-музыка"] = "Rock", ["тяжёлый рок"] = "Hard Rock",
        ["хэви метал"] = "Metal", ["рэп-музыка"] = "Rap", ["хип хоп"] = "Hip-Hop",
        ["singer & songwriter"] = "Folk", ["singer/songwriter"] = "Folk", ["cantautor"] = "Folk",
        ["автор-исполнитель"] = "Bard", ["бард"] = "Bard", ["ancient"] = "World", ["easy listening"] = "Pop",
        ["музыка для детей"] = "Pop", ["детская"] = "Pop", ["саундтреки"] = "Soundtrack",
        ["dzez"] = "Jazz", ["dzsessz"] = "Jazz", ["muzyka jazzowa"] = "Jazz", ["jazz music"] = "Jazz",
        ["classique"] = "Classical", ["musique"] = "Pop", ["variete"] = "Pop", ["chanson francaise"] = "Chanson",
        ["electronique"] = "Electronic", ["musique electronique"] = "Electronic", ["musica"] = "Pop",
        ["musica clasica"] = "Classical", ["musica electronica"] = "Electronic", ["oldies"] = "Rock",
        ["roots"] = "World", ["spiritual"] = "Soul", ["gospel"] = "Soul", ["orchestre"] = "Classical"
    };

    /// <summary>True for labels that carry no usable genre information (placeholders, ads, urls).</summary>
    public static bool IsJunk(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var s = raw.Trim();
        if (s.Length <= 1 || s.Length > 40) return true;
        if (s.Contains('@') || s.Contains('#')) return true;
        if (s.Contains("http", StringComparison.OrdinalIgnoreCase)) return true;

        var low = s.ToLowerInvariant();
        if (low is "other" or "unknown" or "misc" or "none" or "generic" or "humour" or "humor"
            or "cover" or "retro" or "videogame" or "tuntematon genre" or "notasound" or "various")
            return true;

        if (!s.Contains(' ') &&
            (low.EndsWith(".ru") || low.EndsWith(".com") || low.EndsWith(".net") ||
             low.EndsWith(".org") || low.EndsWith(".mp3")))
            return true;

        return false;
    }

    /// <summary>Normalises a free-form genre label; returns null if nothing recognisable.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        foreach (var part in raw.Split(new[] { ';', ',', '/', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Clean(part);
            if (candidate is null) continue;

            if (Aliases.TryGetValue(candidate, out var alias)) return alias;

            var canonical = Canonical.FirstOrDefault(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null) return canonical;

            foreach (var c in Canonical)
                if (candidate.Contains(c, StringComparison.OrdinalIgnoreCase))
                    return c;
            foreach (var (key, value) in Aliases)
                if (candidate.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return value;
        }

        return null;
    }

    private static string? Clean(string value)
    {
        var s = value.Trim().Trim('(', ')', '[', ']', '"', '\'').ToLowerInvariant();
        s = StripDiacritics(s);
        if (s.Length == 0) return null;
        if (s.StartsWith("id3v") || s.StartsWith("(")) return null;
        if (s.Length <= 2 && !s.Any(char.IsLetter)) return null;
        if (s is "other" or "unknown" or "misc" or "none" or "generic") return null;
        if (s.EndsWith(" genre")) s = s[..^6];
        return s.Length == 0 ? null : s;
    }

    /// <summary>Removes accents so "variété" / "électronique" match their plain spellings.</summary>
    private static string StripDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Coarse family used to judge "similar genre" for the recommendation engine.</summary>
    public static string Family(string? genre)
    {
        var g = Normalize(genre) ?? genre;
        return g switch
        {
            null => "Unknown",
            "Metal" or "Hard Rock" or "Punk" or "Grunge" => "Rock",
            "Rock" or "Alternative" or "Indie" => "Rock",
            "Electronic" or "Dance" or "Disco" or "Ambient" => "Electronic",
            "Hip-Hop" or "Rap" or "R&B" or "Soul" or "Funk" => "Urban",
            "Jazz" or "Blues" => "JazzBlues",
            "Classical" or "Instrumental" or "Soundtrack" => "Classical",
            "Folk" or "Country" or "Bard" or "Chanson" => "Folk",
            _ => g
        };
    }
}
