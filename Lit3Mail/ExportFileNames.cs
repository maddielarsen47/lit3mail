using System.Globalization;
using System.Text.RegularExpressions;

namespace Lit3Mail;

internal sealed class ExportFileNames
{
    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars();
    private readonly HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);

    public static string SafeStem(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        string stem = new string(text.Select(character =>
            InvalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd(' ', '.');
        if (stem.Length > 180)
        {
            int length = char.IsHighSurrogate(stem[179]) ? 179 : 180;
            stem = stem[..length].TrimEnd(' ', '.');
        }

        if (stem.Length == 0)
            return fallback;
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]) *(\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            stem = "_" + stem;
        return stem;
    }

    public string Message(DateTime? date, string? subject)
    {
        string timestamp = date?.ToString("yyMMdd_HHmm", CultureInfo.InvariantCulture) ?? "000000_0000";
        string subjectStem = SafeStem(subject, "No subject").Replace(' ', '_');
        string stem = $"{timestamp}_{subjectStem}";
        string name = stem + ".eml";
        for (int suffix = 2; !entries.Add(name); suffix++)
            name = $"{stem}_{suffix}.eml";
        return name;
    }
}
