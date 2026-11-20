namespace Lit3Mail;

internal static class SignatureFile
{
    private const int MaximumBytes = 1024 * 1024;

    public static bool IsHtml(string path) =>
        Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase);

    public static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaximumBytes)
            throw new IOException("Choose a signature file smaller than 1 MB.");
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
