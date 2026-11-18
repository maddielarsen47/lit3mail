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
    private const string ContentClass = "lit3mail-quoted-content";
    private const string ContentStart = "<div class=\"" + ContentClass + "\">";
    private const string QuoteStart = "<div class=\"" + QuoteClass + "\">";
    private const string DocumentEnd = "</div></body></html>";
    private const string DraftMarker = "<!--lit3mail-quote:";
    internal const string OriginalHeader = "--- Original Message ---";
    private static readonly HashSet<string> LayoutTags = new(StringComparer.Ordinal)
    {
        "a", "abbr", "address", "b", "blockquote", "br", "caption",
        "article", "aside", "center", "cite", "code", "col", "colgroup", "dd", "del", "details", "div",
        "dl", "dt", "em", "figcaption", "figure", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hr", "i", "img", "ins", "li", "main", "nav", "ol", "p", "pre", "s", "section",
        "small", "span", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "u", "ul"
    };

    private static readonly HashSet<string> LayoutAttributes = new(StringComparer.Ordinal)
    {
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan",
        "dir", "face", "frame", "height", "id", "lang", "nowrap", "role", "rowspan", "rules", "size", "span", "start",
        "style", "title", "type", "valign", "width"
    };

    public static ComposeHtmlQuote Create(string header, MailContent original,
        bool includeImages, Func<string, InlineMailImage?>? approvedImage = null, bool allowRemoteContent = false)
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
                if (allowRemoteContent && HtmlMailPreview.IsStylesheetLink(tag) &&
                    tag.Attributes.FirstOrDefault(attribute => attribute.Name.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value is { } href &&
                    Uri.TryCreate(href, UriKind.Absolute, out var stylesheet) && RemoteMailImages.IsWebImageUrl(stylesheet))
                {
                    html.Append("<link rel=\"stylesheet\" href=\"").Append(WebUtility.HtmlEncode(href)).Append("\">");
                    continue;
                }
                string? documentElement = name is "html" or "body" ? name : null;
                if (documentElement is not null)
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
                if (documentElement is not null)
                    html.Append(" data-lit3mail-quote-element=\"").Append(documentElement).Append('"');
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
                        if (image is not null)
                        {
                            if (!images.Contains(image))
                                images.Add(image);
                            value = "cid:" + image.ContentId;
                        }
                        else if (!allowRemoteContent || !Uri.TryCreate(value, UriKind.Absolute, out var imageUri) ||
                            !RemoteMailImages.IsWebImageUrl(imageUri))
                            continue;
                    }
                    else if (key == "href" && name == "a")
                    {
                        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                            uri.Scheme is not ("http" or "https" or "mailto"))
                            continue;
                    }
                    else if (!LayoutAttributes.Contains(key) &&
                        !(key == "data-lit3mail-quote-element" && name == "div" && value is "html" or "body"))
                        continue;
                    else if (key == "style")
                    {
                        value = RemoveCssComments(value);
                        if (!allowRemoteContent && !IsLocalCss(value))
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
                    if (!allowRemoteContent)
                        css = LeadingCssImports.Replace(css, "");
                    if (allowRemoteContent || IsLocalCss(css))
                        html.Append(RewriteDocumentSelectors(css));
                }
                else if (hidden is null && token.Kind == HtmlTokenKind.Data)
                    html.Append(WebUtility.HtmlEncode(data.Data));
            }
        }
        if (hidden == "style")
            html.Append("</style>");

        string label = WebUtility.HtmlEncode(header.TrimEnd()).Replace("\r", "").Replace("\n", "<br>");
        return new(header + original.Body,
            header.Length == 0 ? html.ToString() : $"<div><p>{label}</p>{ContentStart}{html}</div></div>", images.ToArray());
    }

    public MailContent WithReply(MailContent reply, bool forDraft = false)
    {
        string quoteHtml = Regex.Replace(Html, @"\A<div><p>--- Original Message ---<br>.*?</p>", match =>
            Regex.Replace(match.Value, "<br>BCC: [^<]*", "", RegexOptions.IgnoreCase),
            RegexOptions.Singleline);
        string scopedQuote = ScopeStyles(forDraft ? quoteHtml : InlineQuoteStyles(quoteHtml));
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
        string scopeStart = @"<style>@scope \(\.(?:" + QuoteClass + "|" + ContentClass + @")\) \{ ";
        quoteHtml = Regex.Replace(quoteHtml, scopeStart + "(.*?)" + Regex.Escape(" }</style>"),
            "<style>$1</style>", RegexOptions.Singleline);
        editableBody = draft with { Body = body[..bodySplit], Html = replyHtml };
        return Create("", draft with { Body = body[(bodySplit + 2)..], Html = quoteHtml }, includeImages: true);
    }

    public string HtmlForPreview() => QuoteStart + ScopeStyles(Html) + "</div>";

    private static string InlineQuoteStyles(string html)
    {
        var rules = new List<(string[] Classes, (string Family, string Css)[] Declarations)>();
        var stylesheetOnly = new HashSet<string>(StringComparer.Ordinal);
        var conditionalTargets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match stylesheet in Regex.Matches(html, @"<style(?:\s[^>]*)?>(.*?)</style>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
            CollectRules(stylesheet.Groups[1].Value, depth: 0);

        if (rules.Count == 0 || stylesheetOnly.Contains("all"))
            return html;

        return Regex.Replace(html, @"<(?<element>[a-z][a-z0-9]*)\b[^>]*>", tag =>
        {
            var classAttribute = Regex.Match(tag.Value, @"\sclass=""([^""]*)""");
            if (!classAttribute.Success)
                return tag.Value;

            var classes = WebUtility.HtmlDecode(classAttribute.Groups[1].Value)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var excluded = new HashSet<string>(stylesheetOnly, StringComparer.Ordinal);
            foreach (string target in classes.Select(name => "." + name).Append(tag.Groups["element"].Value))
                if (conditionalTargets.TryGetValue(target, out var families))
                    excluded.UnionWith(families);
            if (excluded.Contains("all"))
                return tag.Value;

            string declarations = string.Join(" ", rules.Where(rule => rule.Classes.Any(classes.Contains))
                .SelectMany(rule => rule.Declarations).Where(item => !excluded.Contains(item.Family))
                .Select(item => item.Css));
            if (declarations.Length == 0)
                return tag.Value;

            string encoded = WebUtility.HtmlEncode(declarations);
            var style = Regex.Match(tag.Value, @"\sstyle=""([^""]*)""");
            return style.Success ? tag.Value.Insert(style.Groups[1].Index, encoded + " ") :
                tag.Value.Insert(tag.Length - 1, $" style=\"{encoded}\"");
        });

        void CollectRules(string css, int depth)
        {
            foreach (Match rule in Regex.Matches(css,
                         @"(?<selector>[^{};]+)\{(?<body>(?:[^{}]|(?<nested>\{)|(?<-nested>\}))*)(?(nested)(?!))\}"))
            {
                string selector = rule.Groups["selector"].Value.Trim();
                string body = rule.Groups["body"].Value;
                // Rules inside an at-rule can still match the same element at a different viewport or theme.
                if (selector.StartsWith('@') && body.Contains('{'))
                {
                    if (depth < 16)
                        CollectRules(body, depth + 1);
                    else
                        stylesheetOnly.Add("all");
                    continue;
                }

                var selectors = selector.Split(',', StringSplitOptions.TrimEntries);
                var declarations = new List<(string Family, string Css)>();
                foreach (Match declaration in Regex.Matches(body,
                             @"(?:\A|;)\s*(?<name>[a-z-]+)\s*:\s*(?<value>(?:[^;{}""']|""[^""]*""|'[^']*')+)(?=;|\z)",
                             RegexOptions.IgnoreCase))
                {
                    string name = declaration.Groups["name"].Value.ToLowerInvariant();
                    string value = declaration.Groups["value"].Value.Trim();
                    if (InlineStyleFamily(name) is { } family && value.Length > 0)
                        declarations.Add((family, $"{name}: {value};"));
                }
                if (declarations.Count == 0)
                    continue;

                bool simpleClasses = selectors.All(item => Regex.IsMatch(item, @"\A\.[a-zA-Z_][\w-]*\z"));
                if (depth == 0 && simpleClasses && declarations.All(item =>
                        item.Family != "all" && IsLocalCss(item.Css) &&
                        !Regex.IsMatch(item.Css, @"\b(?:var|attr)\s*\(", RegexOptions.IgnoreCase)))
                {
                    rules.Add((selectors.Select(item => item[1..]).ToArray(), declarations.ToArray()));
                    continue;
                }

                // Preserve precedence without guessing how complex selectors match.
                foreach (string item in selectors)
                {
                    var target = Regex.Match(item, @"(?:\.[a-zA-Z_][\w-]*|(?<![^\s>+~])[a-zA-Z][a-zA-Z0-9]*)\z");
                    var excluded = stylesheetOnly;
                    if (target.Success && item.IndexOfAny(['[', '(', ':']) < 0)
                    {
                        string key = target.Value.StartsWith('.') ? target.Value : target.Value.ToLowerInvariant();
                        if (!conditionalTargets.TryGetValue(key, out excluded))
                            conditionalTargets.Add(key, excluded = new(StringComparer.Ordinal));
                    }
                    foreach (var declaration in declarations)
                        excluded.Add(declaration.Family);
                }
            }
        }
    }

    private static string? InlineStyleFamily(string property)
    {
        string family = property.Split('-', 2)[0];
        return family switch
        {
            "min" or "max" when property is "min-width" or "max-width" or "min-height" or "max-height" => property,
            "all" or "width" or "height" or "display" or "margin" or "padding" or "border" or "background" or
            "font" or "color" or "line" or "letter" or "word" or "white" or "vertical" or "box" => family,
            "text" when property.StartsWith("text-decoration", StringComparison.Ordinal) => "text-decoration",
            "text" => property,
            _ => null
        };
    }

    private static string ScopeStyles(string html)
    {
        string scope = html.Contains(ContentStart, StringComparison.Ordinal) ? ContentClass : QuoteClass;
        return Regex.Replace(html, @"<style(?:\s[^>]*)?>(.*?)</style>", match =>
        {
            string css = match.Groups[1].Value;
            if (!HasBalancedCss(css))
                return "";
            var imports = LeadingCssImports.Match(css);
            return $"<style>{imports.Value}@scope (.{scope}) {{ {css[imports.Length..]} }}</style>";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    internal static Group? FindQuotedBcc(string body)
    {
        var header = Regex.Match(body,
            @"(?m)^--- Original Message ---\r?\n(?:^(?:From|To|CC|Reply-To): [^\r\n]*\r?\n)*(?<bcc>^BCC: [^\r\n]*\r?\n)(?:^(?:From|To|CC|Reply-To): [^\r\n]*\r?\n)*(?=^Date: [^\r\n]*\r?\n^Subject: )");
        return header.Success ? header.Groups["bcc"] : null;
    }

    private static string RewriteDocumentSelectors(string css) =>
        Regex.Replace(css, @"(?:\A|(?<=[{}]))(?<prelude>(?:[^{}""']|""[^""]*""|'[^']*')+)\{", rule =>
        {
            string selector = rule.Groups["prelude"].Value;
            if (selector.TrimStart().StartsWith('@'))
                return rule.Value;

            return Regex.Replace(selector,
                @"\[[^\]]*\]|""[^""]*""|'[^']*'|(?<![\w.#:-])(?:html|body)(?![\w-])|(?<![\w:]):root\b",
                match => match.Value.ToLowerInvariant() switch
                {
                    "html" or ":root" => "[data-lit3mail-quote-element=\"html\"]",
                    "body" => "[data-lit3mail-quote-element=\"body\"]",
                    _ => match.Value
                }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) + "{";
        });

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

    private static readonly Regex LeadingCssImports = new( @"\A(?:\s*@import\b\s*(?:url\(\s*(?:""[^""]*""|'[^']*'|[^()'""\s]+)\s*\)|""[^""]*""|'[^']*')[^;{}]*;)+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsLocalCss(string css) =>
        !Regex.IsMatch(css, @"[\\<]|/\*|\b(?:url|image|image-set|expression|behavior)\s*\(|@(?:import|font-face|namespace)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
