using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace Lit3Mail;

internal static class HtmlSignature
{
    private sealed record Style(bool Bold = false, bool Italic = false, bool Underline = false,
        bool Strike = false, Color? Color = null, int? HalfPoints = null, string? Link = null);

    public static string? ToRtf(string html, Func<string, byte[]?>? getPreviewImage = null)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var output = new StringBuilder();
        var colors = new List<Color>();
        var stack = new List<(string Tag, Style Style, bool Hidden)>();
        bool isHtml = false;
        bool lineHasContent = false;
        bool pendingSpace = false;
        bool pendingBreak = false;

        while (tokenizer.ReadNextToken(out var token))
        {
            var current = stack.Count == 0 ? default : stack[^1];
            var style = current.Style ?? new Style();
            if (token is HtmlDataToken { Kind: HtmlTokenKind.Data } text && !current.Hidden)
            {
                string collapsed = Regex.Replace(text.Data, "[ \t\r\n\f]+", " ");
                string content = collapsed.Trim(' ');
                if (content.Length > 0)
                {
                    BeginContent(collapsed.StartsWith(' '));
                    AppendText(content, style);
                }
                pendingSpace = collapsed.EndsWith(' ');
                continue;
            }
            if (token is not HtmlTagToken tag)
                continue;

            isHtml |= tag.Id != HtmlTagId.Unknown;
            string name = tag.Name.ToLowerInvariant();
            bool block = name is "div" or "p" or "section" or "article" or "table" or "tr" or
                "ul" or "ol" or "li" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
            if (tag.IsEndTag)
            {
                int index = stack.FindLastIndex(entry => entry.Tag == name);
                if (index >= 0)
                    stack.RemoveRange(index, stack.Count - index);
                if (block && !current.Hidden)
                    pendingBreak |= lineHasContent;
                continue;
            }

            bool hidden = current.Hidden || name is "head" or "title" or "script" or "style" or "template";
            if (hidden)
            {
                if (!tag.IsEmptyElement && name is not ("meta" or "link" or "base" or "img" or "br" or "hr" or "input"))
                    stack.Add((name, style, true));
                continue;
            }

            if (name == "br")
            {
                output.Append(@"\line ");
                lineHasContent = pendingSpace = pendingBreak = false;
                continue;
            }
            if (name == "img")
            {
                string? picture = ReadEmbeddedPicture(tag, style, getPreviewImage);
                if (picture is not null)
                {
                    if (picture.Length > 0)
                    {
                        BeginContent();
                        output.Append(picture);
                    }
                }
                else if (Attribute(tag, "alt") is { Length: > 0 } alt)
                {
                    BeginContent();
                    AppendText(alt, style);
                }
                continue;
            }
            if (block)
                pendingBreak |= lineHasContent;
            if (name is "td" or "th")
                pendingSpace |= lineHasContent;
            if (name == "li")
            {
                BeginContent();
                AppendText("• ", style);
            }
            if (!tag.IsEmptyElement && name is not ("meta" or "link" or "base" or "hr" or "input" or "source" or "wbr"))
                stack.Add((name, ApplyTagStyle(style, tag, name), false));
        }

        if (!isHtml)
            return null;

        var rtf = new StringBuilder(@"{\rtf1\ansi\uc1{\colortbl ;");
        foreach (Color color in colors)
            rtf.Append(CultureInfo.InvariantCulture, $"\\red{color.R}\\green{color.G}\\blue{color.B};");
        return rtf.Append('}').Append(output).Append('}').ToString();

        void BeginContent(bool startsWithSpace = false)
        {
            if (pendingBreak && lineHasContent)
                output.Append(@"\line ");
            else if (lineHasContent && (pendingSpace || startsWithSpace))
                output.Append(' ');
            pendingSpace = pendingBreak = false;
            lineHasContent = true;
        }

        void AppendText(string value, Style style)
        {
            if (style.Link is { } link)
            {
                output.Append("{\\field{\\*\\fldinst HYPERLINK \"");
                AppendEscaped(output, link);
                output.Append("\"}{\\fldrslt ");
            }
            output.Append(style.Bold ? @"{\b" : @"{\b0");
            if (style.Italic) output.Append(@"\i");
            if (style.Underline) output.Append(@"\ul");
            if (style.Strike) output.Append(@"\strike");
            if (style.HalfPoints is { } size)
                output.Append(CultureInfo.InvariantCulture, $"\\fs{size}");
            if (style.Color is { } color)
            {
                int index = colors.IndexOf(color);
                if (index < 0)
                {
                    colors.Add(color);
                    index = colors.Count - 1;
                }
                output.Append(CultureInfo.InvariantCulture, $"\\cf{index + 1}");
            }
            output.Append(' ');
            AppendEscaped(output, value);
            output.Append('}');
            if (style.Link is not null)
                output.Append("}}");
        }
    }

    private static Style ApplyTagStyle(Style style, HtmlTagToken tag, string name)
    {
        style = name switch
        {
            "strong" or "b" or "th" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => style with { Bold = true },
            "em" or "i" => style with { Italic = true },
            "u" => style with { Underline = true },
            "s" or "strike" or "del" => style with { Strike = true },
            "a" when Uri.TryCreate(Attribute(tag, "href"), UriKind.Absolute, out var link) &&
                link.Scheme is "http" or "https" or "mailto" =>
                style with { Link = link.AbsoluteUri.Replace("\"", "%22"), Underline = true, Color = Color.RoyalBlue },
            "font" when ReadColor(Attribute(tag, "color")) is { } color => style with { Color = color },
            _ => style
        };

        foreach (string item in (Attribute(tag, "style") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':');
            if (colon < 0)
                continue;
            string property = item[..colon].Trim().ToLowerInvariant();
            string value = item[(colon + 1)..].Trim().ToLowerInvariant();
            style = property switch
            {
                "font-weight" => style with { Bold = value is "bold" or "bolder" ||
                    int.TryParse(value, out int weight) && weight >= 600 },
                "font-style" => style with { Italic = value is "italic" or "oblique" },
                "text-decoration" or "text-decoration-line" => style with
                {
                    Underline = value.Contains("underline"), Strike = value.Contains("line-through")
                },
                "color" when ReadColor(value) is { } color => style with { Color = color },
                "font-size" when ReadFontSize(value) is { } size => style with { HalfPoints = size },
                _ => style
            };
        }
        return style;
    }

    private static string? Attribute(HtmlTagToken tag, string name) =>
        tag.Attributes.FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static Color? ReadColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return ColorTranslator.FromHtml(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int? ReadFontSize(string value)
    {
        if (value.EndsWith("px") && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels)
            && double.IsFinite(pixels))
            return (int)Math.Round(Math.Clamp(pixels * 1.5, 12, 144));
        if (value.EndsWith("pt") && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double points)
            && double.IsFinite(points))
            return (int)Math.Round(Math.Clamp(points * 2, 12, 144));
        return null;
    }

    private static string? ReadEmbeddedPicture(HtmlTagToken tag, Style style, Func<string, byte[]?>? getPreviewImage)
    {
        if (Attribute(tag, "src") is not { } source)
            return null;
        var (width, height) = ReadImageDimensions(tag, style);
        if (width == 0 || height == 0)
            return "";
        try
        {
            byte[]? bytes = getPreviewImage?.Invoke(source);
            if (bytes is null)
            {
                int comma = source.IndexOf(',');
                if (comma < 0 || source[..comma].ToLowerInvariant() is not
                    ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/gif;base64"))
                    return null;
                bytes = Convert.FromBase64String(source[(comma + 1)..]);
            }
            using var data = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(data);
            double aspectRatio = (double)image.Width / image.Height;
            return MailBodyEditor.CreatePictureGroup(image,
                width ?? (height is { } h ? h * aspectRatio : image.Width),
                height ?? (width is { } w ? w / aspectRatio : image.Height));
        }
        catch (Exception error) when (error is FormatException or ArgumentException or ExternalException)
        {
            return null;
        }
    }

    private static (double? Width, double? Height) ReadImageDimensions(HtmlTagToken tag, Style style)
    {
        double? fontPixels = ApplyTagStyle(style, tag, "img").HalfPoints / 1.5;
        double? width = ReadLength(Attribute(tag, "width"), allowUnitless: true);
        double? height = ReadLength(Attribute(tag, "height"), allowUnitless: true);
        foreach (string declaration in (Attribute(tag, "style") ?? "").Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon < 0)
                continue;
            string property = declaration[..colon].Trim().ToLowerInvariant();
            if (property is not ("width" or "height"))
                continue;
            string value = declaration[(colon + 1)..].Trim().ToLowerInvariant();
            if (value.EndsWith("!important"))
                value = value[..^10].TrimEnd();
            double? length = ReadLength(value);
            if (length is null && value != "auto")
                continue;
            if (property == "width")
                width = length;
            else
                height = length;
        }
        return (width, height);

        double? ReadLength(string? value, bool allowUnitless = false)
        {
            if (value is null)
                return null;
            value = value.Trim().ToLowerInvariant();
            double? multiplier = value.EndsWith("px") ? 1 :
                value.EndsWith("pt") ? 4d / 3 :
                value.EndsWith("em") ? fontPixels : null;
            string number = multiplier.HasValue ? value[..^2] : value;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) ||
                size < 0 || !double.IsFinite(size) || (!allowUnitless && multiplier is null && size != 0))
                return null;
            double pixels = size * (multiplier ?? 1);
            return double.IsFinite(pixels) ? pixels : null;
        }
    }

    private static void AppendEscaped(StringBuilder output, string text)
    {
        foreach (char character in text)
        {
            if (character is '\\' or '{' or '}')
                output.Append('\\').Append(character);
            else if (character >= ' ' && character <= '~')
                output.Append(character);
            else if (!char.IsControl(character))
                output.Append(CultureInfo.InvariantCulture, $"\\u{unchecked((short)character)}?");
        }
    }
}
