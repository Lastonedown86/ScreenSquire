namespace PiSignage.Signage;

public static class BoardSlug
{
    // "Top 8 Bracket" -> "top-8-bracket". Used for board keys, capture filenames,
    // and the TV page URL (?name=<slug>), so it must stay URL- and filename-safe.
    public static string From(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }
}
