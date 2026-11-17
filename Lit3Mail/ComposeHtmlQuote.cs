using System.Net;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit.Text;

namespace Lit3Mail;

internal sealed record ComposeHtmlQuote(string Body, string Html, InlineMailImage[] Images)
{
    private const string DocumentStart = "<!doctype html><html><body>";
    private const string QuoteClass = "lit3mail-original-message";
    private const string QuoteStart = "<div class=\"" + QuoteClass + "\">";
    private const string DocumentEnd = "</div></body></html>";
    private const string DraftMarker = "<!--lit3mail-quote:";
    private const string OriginalHeader = "--- Original Message ---";
    private static readonly HashSet<string> LayoutTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "address", "b", "blockquote", "br", "caption", "center", "cite", "code", "col",
        "colgroup", "dd", "del", "div", "dl", "dt", "em", "font", "h1", "h2", "h3", "h4", "h5", "h6",
        "hr", "i", "img", "ins", "li", "ol", "p", "pre", "s", "small", "span", "strong", "sub", "sup",
        "table", "tbody", "td", "tfoot", "th", "thead", "tr", "u", "ul"
    };

    private static readonly HashSet<string> LayoutAttributes = new(StringComparer.Ordinal)
    {
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan",
        "dir", "face", "height", "id", "lang", "nowrap", "role", "rowspan", "size", "span", "start",
        "style", "title", "type", "valign", "width"
    };

    public static ComposeHtmlQuote Create(string header, MailContent original,
        bool includeImages, Func<string, InlineMailImage?>? approvedImage = null)
    {
        var images = new List<InlineMailImage>();
        var html = new StringBuilder();
        using var reader = new StringReader(original.Html ?? "");
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        string? hidden = null;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                string name = tag.Name.ToLowerInvariant();
                if (hidden is not null)
                {
                    if (tag.IsEndTag && name == hidden)
                    {
                        if (hidden == "style")
                            html.Append("</style>");
                        hidden = null;
                    }
                    continue;
                }
                if (name is "script" or "title" or "iframe" or "object" or "svg" or "textarea" or "template" or "noscript")
                {
                    if (!tag.IsEndTag && !tag.IsEmptyElement)
                        hidden = name;
                    continue;
                }
                if (name == "body")
                    name = "div";
                else if (!LayoutTags.Contains(name) && name != "style")
                    continue;

                html.Append('<');
                if (tag.IsEndTag)
                {
                    html.Append('/').Append(name).Append('>');
                    continue;
                }
                html.Append(name);
                foreach (var attribute in tag.Attributes)
                {
                    string key = attribute.Name.ToLowerInvariant();
                    string value = attribute.Value ?? "";
                    if (key == "src" && name == "img")
                    {
                        if (!includeImages)
                            continue;
                        var image = images.FirstOrDefault(item => item.ContentLocation == value)
                            ?? FindImage(value, original.InlineImages) ?? ReadDataImage(value) ?? approvedImage?.Invoke(value);
                        if (image is null)
                            continue;
                        if (!images.Contains(image))
                            images.Add(image);
                        value = "cid:" + image.ContentId;
                    }
                    else if (key == "href" && name == "a")
                    {
                        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                            uri.Scheme is not ("http" or "https" or "mailto"))
                            continue;
                    }
                    else if (!LayoutAttributes.Contains(key))
                        continue;
                    else if (key == "style")
                    {
                        value = RemoveCssComments(value);
                        if (!IsLocalCss(value))
                            continue;
                    }
                    html.Append(' ').Append(key).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
                }
                html.Append('>');
                if (name == "style")
                    hidden = "style";
            }
            else if (token is HtmlDataToken data)
            {
                if (hidden == "style")
                {
                    string css = RemoveCssComments(data.Data);
                    if (IsLocalCss(css))
                        html.Append(css);
                }
                else if (hidden is null && token.Kind == HtmlTokenKind.Data)
                    html.Append(WebUtility.HtmlEncode(data.Data));
            }
        }
        if (hidden == "style")
            html.Append("</style>");

        string label = WebUtility.HtmlEncode(header.TrimEnd()).Replace("\r", "").Replace("\n", "<br>");
        return new(header + original.Body,
            header.Length == 0 ? html.ToString() : $"<div><p>{label}</p>{html}</div>", images.ToArray());
    }

    public MailContent WithReply(MailContent reply, bool forDraft = false)
    {
        string quoteHtml = Regex.Replace(Html, @"\A<div><p>--- Original Message ---<br>.*?</p>", match =>
            Regex.Replace(match.Value, "<br>BCC: [^<]*", "", RegexOptions.IgnoreCase),
            RegexOptions.Singleline);
        string scopedQuote = Regex.Replace(quoteHtml, @"<style(?:\s[^>]*)?>(.*?)</style>", match =>
        {
            string css = match.Groups[1].Value;
            return HasBalancedCss(css) ? $"<style>@scope (.{QuoteClass}) {{ {css} }}</style>" : "";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string marker = forDraft ? $"{DraftMarker}{reply.Body.ReplaceLineEndings("\n").Length.ToString(CultureInfo.InvariantCulture)}-->" : "";
        string body = FindQuotedBcc(Body) is { } bcc ? Body.Remove(bcc.Index, bcc.Length) : Body;
        return new(reply.Body + "\n\n" + body,
            Html: DocumentStart + reply.Html + marker + QuoteStart + scopedQuote + DocumentEnd,
            InlineImages: (reply.InlineImages ?? []).Concat(Images).ToArray());
    }

    public static ComposeHtmlQuote? RestoreDraftQuote(MailContent draft, out MailContent editableBody)
    {
        editableBody = draft;
        if (draft.Html is not { } html || !html.StartsWith(DocumentStart, StringComparison.Ordinal) ||
            !html.TrimEnd().EndsWith(DocumentEnd, StringComparison.Ordinal))
            return null;
        html = html.TrimEnd();
        int quoteStart = html.IndexOf(QuoteStart, DocumentStart.Length, StringComparison.Ordinal);
        if (quoteStart < 0)
            return null;

        string replyHtml = html[DocumentStart.Length..quoteStart];
        string body = draft.Body.ReplaceLineEndings("\n");
        int bodySplit = body.IndexOf("\n\n" + OriginalHeader, StringComparison.Ordinal);
        int markerStart = replyHtml.LastIndexOf(DraftMarker, StringComparison.Ordinal);
        if (markerStart >= 0 && replyHtml.EndsWith("-->", StringComparison.Ordinal) &&
            int.TryParse(replyHtml.AsSpan(markerStart + DraftMarker.Length, replyHtml.Length - markerStart - DraftMarker.Length - 3),
                NumberStyles.None, CultureInfo.InvariantCulture, out int replyLength))
        {
            replyHtml = replyHtml[..markerStart];
            if (replyLength <= body.Length && body.AsSpan(replyLength).StartsWith("\n\n" + OriginalHeader, StringComparison.Ordinal))
                bodySplit = replyLength;
        }
        if (bodySplit < 0)
            return null;

        string quoteHtml = html[(quoteStart + QuoteStart.Length)..^DocumentEnd.Length];
        string scopeStart = $"<style>@scope (.{QuoteClass}) {{ ";
        quoteHtml = Regex.Replace(quoteHtml, Regex.Escape(scopeStart) + "(.*?)" + Regex.Escape(" }</style>"),
            "<style>$1</style>", RegexOptions.Singleline);
        editableBody = draft with { Body = body[..bodySplit], Html = replyHtml };
        return Create("", draft with { Body = body[(bodySplit + 2)..], Html = quoteHtml }, includeImages: true);
    }

    internal static Group? FindQuotedBcc(string body)
    {
        var header = Regex.Match(body,
            @"(?m)^--- Original Message ---\r?\n(?:^(?:From|To|CC|Reply-To): [^\r\n]*\r?\n)*(?<bcc>^BCC: [^\r\n]*\r?\n)(?:^(?:From|To|CC|Reply-To): [^\r\n]*\r?\n)*(?=^Date: [^\r\n]*\r?\n^Subject: )");
        return header.Success ? header.Groups["bcc"] : null;
    }

    private static bool HasBalancedCss(string css)
    {
        int depth = 0;
        char quoted = '\0';
        foreach (char character in css)
        {
            if (quoted != '\0')
            {
                if (character == quoted)
                    quoted = '\0';
            }
            else if (character is '\'' or '"')
                quoted = character;
            else if (character == '{')
                depth++;
            else if (character == '}' && --depth < 0)
                return false;
        }
        return depth == 0 && quoted == '\0';
    }

    private static InlineMailImage? FindImage(string source, IReadOnlyList<InlineMailImage>? images)
    {
        string id = source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(source[4..]) : "";
        return images?.FirstOrDefault(image => IsSupportedImage(image.MediaType) &&
            (image.ContentLocation == source || id.Equals(image.ContentId, StringComparison.OrdinalIgnoreCase)));
    }

    private static InlineMailImage? ReadDataImage(string source)
    {
        int comma = source.IndexOf(',');
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 12 ||
            source.Length - comma > 14 * 1024 * 1024 ||
            !source.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return null;
        string mediaType = source[5..(comma - 7)].ToLowerInvariant();
        if (!IsSupportedImage(mediaType))
            return null;
        try
        {
            return new(MimeKit.Utils.MimeUtils.GenerateMessageId(), mediaType,
                Convert.FromBase64String(source[(comma + 1)..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static bool IsSupportedImage(string mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif";

    private static string RemoveCssComments(string css) => Regex.Replace(css, @"/\*[\s\S]*?\*/", "");

    private static bool IsLocalCss(string css) =>
        !Regex.IsMatch(css, @"[\\<]|/\*|\b(?:url|image|image-set|expression|behavior)\s*\(|@(?:import|font-face|namespace)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
