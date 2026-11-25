using System.Text.RegularExpressions;

namespace Lit3Mail;

internal static class MailFolders
{
    public static bool IsSpamFolder(string name)
    {
        string leaf = name[(name.LastIndexOfAny(['/', '\\', ':']) + 1)..].Trim();
        return leaf.Equals("Spam", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Junk", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Junk Email", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Junk E-mail", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals("Junk Mail", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MarksMovedMessagesRead(string name) =>
        name.Equals("Trash", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Bin", StringComparison.OrdinalIgnoreCase);
}

internal sealed record MessageMoveResult(bool MarkedRead, IReadOnlyDictionary<Guid, Guid>? DestinationIds = null);
internal sealed record ResponseOrigin(Guid AccountId, string ConnectionKey, string Folder, Guid MessageId, bool IsReply);

internal sealed record MailSummary(Guid Id, string Folder, bool Read, bool Star, bool Attachments,
    string Subject, string CorrespondentName, DateTime Date,
    string FromAddress, string ToAddresses, string CcAddresses, string ReplyToAddress,
    bool HasPreviewHeaders = true, string BccAddresses = "", string ExplicitReplyToAddresses = "",
    bool Replied = false, bool Forwarded = false, bool HasStandaloneImages = false)
{
    public string CorrespondentAddresses => GetCorrespondentAddresses();

    public string GetCorrespondentAddresses(string? excludedAddress = null) => string.Join("; ",
        new[] { FromAddress, ToAddresses, CcAddresses }
            .SelectMany(addresses => addresses.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(address => !address.Equals(excludedAddress, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase));
}

internal sealed record InlineMailImage(string ContentId, string MediaType, byte[] Bytes, string? ContentLocation = null);
internal sealed record MailFileAttachment(string FileName, string MediaType, byte[] Bytes);
internal sealed record MailAttachmentInfo(string PartId, string FileName, string MediaType);

internal sealed record MailContent(string Body, string? Html = null,
    IReadOnlyList<InlineMailImage>? InlineImages = null, IReadOnlyList<MailAttachmentInfo>? AttachmentList = null,
    bool PreviewTooLarge = false)
{
    private static readonly Regex ContentIdReference = new("""\bcid:([^"'<>\s)]+)""", RegexOptions.IgnoreCase);

    private static readonly Regex ImageSource = new(
        """(<img\b[^<>]*?\s+src\s*=\s*)(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase);

    private static string ImageSourceValue(Match match) =>
        System.Net.WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(2).First(group => group.Success).Value);

    internal static HashSet<string> ReferencedImageSources(string html) =>
        ImageSource.Matches(html).Select(ImageSourceValue).ToHashSet(StringComparer.Ordinal);

    internal static HashSet<string> ReferencedContentIds(string html) =>
        ContentIdReference.Matches(html).Select(match => Uri.UnescapeDataString(match.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string? HtmlForPreview() => HtmlWithInlineImages(image =>
        $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}");

    public string? HtmlWithInlineImages(Func<InlineMailImage, string> imageSource)
    {
        if (Html is null || InlineImages is not { Count: > 0 } images)
            return Html;

        var byLocation = new Dictionary<string, InlineMailImage>(StringComparer.Ordinal);
        var byId = new Dictionary<string, InlineMailImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            if (image.ContentLocation is { } location)
                byLocation.TryAdd(location, image);
            byId.TryAdd(image.ContentId, image);
        }

        string html = ImageSource.Replace(Html, match => byLocation.TryGetValue(ImageSourceValue(match), out var image)
            ? match.Groups[1].Value + "\"" + imageSource(image) + "\""
            : match.Value);
        return ContentIdReference.Replace(html, match =>
            byId.TryGetValue(Uri.UnescapeDataString(match.Groups[1].Value), out var image)
                ? imageSource(image)
                : match.Value);
    }
}
internal sealed record OutgoingMail(string From, string ReplyTo, string To, string Cc, string Subject, string Body, string? Html = null,
    IReadOnlyList<InlineMailImage>? InlineImages = null, string Bcc = "", IReadOnlyList<MailFileAttachment>? Files = null);
internal sealed record FolderCount(int Unread, int Total, string? LabelName = null, bool HasCounts = true);
internal sealed record SentMailResult(MailSummary? Summary, string? Warning = null);
internal sealed record DraftSaveResult(MailSummary? Summary, bool Replaced = false, string? Warning = null);

internal interface IMailbox
{
    Task<IReadOnlyDictionary<string, FolderCount>> GetFolderCountsAsync(CancellationToken cancellationToken);
    Task<bool> HasUnreadArrivalsAsync(DateTimeOffset since, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailSummary>> ListAsync(string folder, string query, CancellationToken cancellationToken);
    Task<MailContent> LoadContentAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(Guid id, CancellationToken cancellationToken);
    Task DownloadAttachmentAsync(Guid id, string partId, Stream output, CancellationToken cancellationToken);
    Task UpdateFlagsAsync(string folder, Guid id, bool? read, bool? star, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<Guid>> MarkAllReadAsync(string folder, CancellationToken cancellationToken);
    Task<MessageMoveResult> DeleteAsync(string folder, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<MessageMoveResult> MoveAsync(string source, string destination, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task ExportAsync(string folder, IReadOnlySet<Guid>? ids, System.IO.Compression.ZipArchive archive,
        IProgress<(int Done, int Total)> progress, CancellationToken token);
    Task<DraftSaveResult> SaveDraftAsync(OutgoingMail message, Guid? replacing, CancellationToken cancellationToken);
    Task<OutgoingMail> LoadDraftAsync(Guid id, CancellationToken cancellationToken);
    Task RemoveDraftAsync(Guid id, CancellationToken cancellationToken);
    Task<SentMailResult> SendAsync(OutgoingMail message, CancellationToken cancellationToken);
}
