using System.Text.RegularExpressions;

namespace Lit3Mail;

internal static class MailFolders
{
    public static bool MarksMovedMessagesRead(string name) =>
        name.Equals("Trash", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Bin", StringComparison.OrdinalIgnoreCase);
}

internal sealed record MailSummary(Guid Id, string Folder, bool Read, bool Star, bool Attachments,
    string Subject, string CorrespondentName, DateTime Date,
    string FromAddress, string ToAddresses, string CcAddresses, string ReplyToAddress,
    bool HasPreviewHeaders = true)
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

internal sealed record MailContent(string Body, bool HasInlineImage = false, string? Html = null,
    IReadOnlyList<InlineMailImage>? InlineImages = null, IReadOnlyList<MailAttachmentInfo>? AttachmentList = null)
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

    public string? HtmlForPreview()
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
            ? match.Groups[1].Value + "\"" + ImageData(image) + "\""
            : match.Value);
        return ContentIdReference.Replace(html, match =>
            byId.TryGetValue(Uri.UnescapeDataString(match.Groups[1].Value), out var image)
                ? ImageData(image)
                : match.Value);
    }
    private static string ImageData(InlineMailImage image) =>
        $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";
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
    Task UpdateFlagsAsync(Guid id, bool? read, bool? star, CancellationToken cancellationToken);
    Task DeleteAsync(string folder, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<bool> MoveAsync(string source, string destination, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task ExportAsync(string folder, IReadOnlySet<Guid>? ids, System.IO.Compression.ZipArchive archive,
        IProgress<(int Done, int Total)> progress, CancellationToken token);
    Task<DraftSaveResult> SaveDraftAsync(OutgoingMail message, Guid? replacing, CancellationToken cancellationToken);
    Task<OutgoingMail> LoadDraftAsync(Guid id, CancellationToken cancellationToken);
    Task RemoveDraftAsync(Guid id, CancellationToken cancellationToken);
    Task<SentMailResult> SendAsync(OutgoingMail message, CancellationToken cancellationToken);
}

internal sealed class SampleMailbox : IMailbox
{
    public SampleMailbox(bool includeSamples = true)
    {
        if (!includeSamples)
            mail.Clear();
    }

    private const string MailboxAddress = "me@example.com";
    private readonly Dictionary<Guid, OutgoingMail> draftMessages = [];
    private readonly Dictionary<Guid, IReadOnlyList<MailFileAttachment>> messageFiles = [];

    private readonly List<(MailSummary Summary, MailContent Content)> mail =
    [
        Sample("Inbox", false, true, true, "Workshop agenda", "Priya Shah", new(2026, 11, 12, 9, 15, 0),
            "Hi,\n\nI've attached the agenda for next week's workshop. Please send me any topics you'd like to \"add\".\n\nPriya",
            "priya.shah@example.com", "sam.chen@example.com; jordan.lee@example.com"),
        Sample("Inbox", true, false, false, "Coffee on Friday?", "Alex Morgan", new(2026, 11, 10, 14, 30, 0),
            "Would you be free for coffee on Friday morning? I can meet near the station.", "alex.morgan@example.com"),
        Sample("Inbox", true, true, false, "Library books ready for pickup", "City Library", new(2026, 11, 11, 11, 0, 0),
            "Your reserved books are ready at the front desk. We'll hold them for seven days.", "circulation@example.com"),
        Sample("Inbox", false, false, true, "November newsletter", "Community Garden", new(2026, 11, 8, 8, 45, 0),
            "This month's newsletter includes the spring planting schedule and details of our next working bee.",
            "newsletter@example.com", HasInlineImage: true),
        Sample("Sent", true, false, false, "Re: Room booking", "Sam Chen", new(2026, 11, 11, 16, 20, 0),
            "Thanks, Sam. I've put the room booking in my calendar.", "sam.chen@example.com"),
        Sample("Drafts", true, false, false, "Notes for Monday", "Jordan Lee", new(2026, 11, 12, 8, 0, 0),
            "Hi Jordan,\n\nA few things to discuss on Monday…", "jordan.lee@example.com"),
        Sample("Archive", true, false, true, "October statement", "Harbour Energy", new(2026, 11, 2, 10, 0, 0),
            "Your October statement is attached.", "billing@example.com")
    ];


    private static (MailSummary, MailContent) Sample(string folder, bool read, bool star, bool attachments,
        string subject, string name, DateTime date, string body, string address, string cc = "", bool HasInlineImage = false)
    {
        bool outgoing = folder is "Sent" or "Drafts";
        return (new(Guid.NewGuid(), folder, read, star, attachments, subject, name, date,
            outgoing ? MailboxAddress : address, outgoing ? address : MailboxAddress, cc, address),
            new(body, HasInlineImage));
    }

    public Task<IReadOnlyDictionary<string, FolderCount>> GetFolderCountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, FolderCount> counts = mail.GroupBy(item => item.Summary.Folder)
            .ToDictionary(group => group.Key, group => new FolderCount(group.Count(item => !item.Summary.Read), group.Count()));
        return Task.FromResult(counts);
    }

    public Task<bool> HasUnreadArrivalsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(mail.Any(item => item.Summary.Folder == "Inbox" && !item.Summary.Read &&
            item.Summary.Date >= since.LocalDateTime));
    }

    public Task<IReadOnlyList<MailSummary>> ListAsync(string folder, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terms = Regex.Matches(query, "\"([^\"]*)\"|[^\\s\"]+")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        IReadOnlyList<MailSummary> matches = mail.Where(item => item.Summary.Folder == folder &&
            terms.All(term => Contains(item.Summary.Subject, term) ||
                Contains(item.Summary.CorrespondentName, term) ||
                Contains(item.Summary.CorrespondentAddresses, term) ||
                Contains(item.Content.Body, term)))
            .Select(item => item.Summary).ToArray();
        return Task.FromResult(matches);
    }

    private static bool Contains(string field, string term) =>
        field.Contains(term, StringComparison.OrdinalIgnoreCase);

    public Task<MailContent> LoadContentAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(mail.Single(item => item.Summary.Id == id).Content);
    }

    public Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MailAttachmentInfo> attachments = messageFiles.TryGetValue(id, out var files)
            ? files.Select((file, index) => new MailAttachmentInfo(index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                file.FileName, file.MediaType)).ToArray() : [];
        return Task.FromResult(attachments);
    }

    public async Task DownloadAttachmentAsync(Guid id, string partId, Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!messageFiles.TryGetValue(id, out var files) || !int.TryParse(partId, out int index) ||
            index < 0 || index >= files.Count)
            throw new InvalidOperationException("The attachment is unavailable.");
        await output.WriteAsync(files[index].Bytes, cancellationToken);
    }

    public Task UpdateFlagsAsync(Guid id, bool? read, bool? star, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index = mail.FindIndex(item => item.Summary.Id == id);
        var (summary, content) = mail[index];
        mail[index] = (summary with { Read = read ?? summary.Read, Star = star ?? summary.Star }, content);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string folder, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (folder == "Drafts")
            foreach (Guid id in ids)
                draftMessages.Remove(id);
        if (folder == "Trash")
        {
            mail.RemoveAll(item => item.Summary.Folder == folder && ids.Contains(item.Summary.Id));
            foreach (Guid id in ids)
                messageFiles.Remove(id);
        }
        else
            for (int index = 0; index < mail.Count; index++)
            {
                var item = mail[index];
                if (item.Summary.Folder == folder && ids.Contains(item.Summary.Id))
                    mail[index] = (item.Summary with { Folder = "Trash", Read = true }, item.Content);
            }
        return Task.CompletedTask;
    }

    public Task<bool> MoveAsync(string source, string destination, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool markRead = MailFolders.MarksMovedMessagesRead(destination);
        for (int index = 0; index < mail.Count; index++)
        {
            var item = mail[index];
            if (item.Summary.Folder == source && ids.Contains(item.Summary.Id))
            {
                mail[index] = (item.Summary with { Folder = destination, Read = markRead || item.Summary.Read }, item.Content);
                if (source == "Drafts" && destination != "Drafts")
                    draftMessages.Remove(item.Summary.Id);
            }
        }
        return Task.FromResult(markRead);
    }

    public async Task ExportAsync(string folder, IReadOnlySet<Guid>? ids, System.IO.Compression.ZipArchive archive,
        IProgress<(int Done, int Total)> progress, CancellationToken token)
    {
        var items = mail.Where(item => item.Summary.Folder == folder &&
            (ids is null || ids.Contains(item.Summary.Id))).ToArray();
        var names = new ExportFileNames();
        progress.Report((0, items.Length));
        for (int i = 0; i < items.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var (summary, content) = items[i];
            messageFiles.TryGetValue(summary.Id, out var files);
            using var message = ServerMailbox.CreateOutgoingMessage(new(summary.FromAddress, summary.ReplyToAddress,
                summary.ToAddresses, summary.CcAddresses, summary.Subject, content.Body, content.Html,
                content.InlineImages, Files: files), MimeKit.Utils.MimeUtils.GenerateMessageId());
            message.Date = summary.Date;
            await using var stream = archive.CreateEntry(names.Message(summary.Date, summary.Subject)).Open();
            await message.WriteToAsync(stream, token);
            progress.Report((i + 1, items.Length));
        }
    }

    public async Task<DraftSaveResult> SaveDraftAsync(OutgoingMail message, Guid? replacing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = new MailSummary(Guid.NewGuid(), "Drafts", true, false, message.Files is { Count: > 0 }, message.Subject,
            message.To, DateTime.Now, message.From, message.To, message.Cc, message.ReplyTo);
        mail.Add((summary, new MailContent(message.Body, Html: message.Html, InlineImages: message.InlineImages)));
        draftMessages.Add(summary.Id, message);
        if (message.Files is { Count: > 0 } files)
            messageFiles.Add(summary.Id, files);
        if (replacing is { } id)
            await RemoveDraftAsync(id, cancellationToken);
        return new(summary, replacing is not null);
    }

    public Task<OutgoingMail> LoadDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (draftMessages.TryGetValue(id, out var message))
            return Task.FromResult(message);
        var (summary, content) = mail.Single(item => item.Summary.Id == id && item.Summary.Folder == "Drafts");
        return Task.FromResult(new OutgoingMail(summary.FromAddress, summary.ReplyToAddress, summary.ToAddresses,
            summary.CcAddresses, summary.Subject, content.Body, content.Html, content.InlineImages));
    }

    public Task RemoveDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        mail.RemoveAll(item => item.Summary.Folder == "Drafts" && item.Summary.Id == id);
        draftMessages.Remove(id);
        messageFiles.Remove(id);
        return Task.CompletedTask;
    }

    public Task<SentMailResult> SendAsync(OutgoingMail message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = new MailSummary(Guid.NewGuid(), "Sent", true, false, message.Files is { Count: > 0 }, message.Subject,
            message.To, DateTime.Now, message.From, message.To, message.Cc, message.ReplyTo);
        var sent = (summary, new MailContent(message.Body, Html: message.Html, InlineImages: message.InlineImages));
        mail.Add(sent);
        if (message.Files is { Count: > 0 } files)
            messageFiles.Add(summary.Id, files);
        return Task.FromResult(new SentMailResult(summary));
    }
}






