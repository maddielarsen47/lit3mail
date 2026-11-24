using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace Lit3Mail;

internal sealed record MailEndpoint(string Host, int Port, string User, string Password, bool StartTls);

internal sealed class ServerMailbox(MailEndpoint incoming, MailEndpoint outgoing) : IMailbox
{
    internal const string GmailLabelPrefix = "gmail-label:";
    private const string ForwardedKeyword = "$Forwarded";
    private const int MaxPreviewTextBytes = 4 * 1024 * 1024;
    private const int MaxInlineImageBytes = 5 * 1024 * 1024;
    private const int MaxInlinePreviewBytes = 12 * 1024 * 1024;
    public bool IsGmail => string.Equals(incoming.Host, "imap.gmail.com", StringComparison.OrdinalIgnoreCase);

    private static readonly (string Name, SpecialFolder Special, FolderAttributes Attribute)[] SpecialFolders =
    [
        ("Sent", SpecialFolder.Sent, FolderAttributes.Sent), ("Drafts", SpecialFolder.Drafts, FolderAttributes.Drafts),
        ("Archive", SpecialFolder.Archive, FolderAttributes.Archive), ("Trash", SpecialFolder.Trash, FolderAttributes.Trash)
    ];

    private readonly ConcurrentDictionary<Guid, (string Folder, uint Validity, UniqueId Uid)> messages = new();
    private readonly Dictionary<Guid, PreviewStructure> previewStructures = new();
    private readonly Queue<Guid> previewStructureOrder = new();

    private sealed record PreviewStructure(BodyPartText? Text, BodyPartText? Html,
        BodyPartBasic[] InlineParts, IReadOnlyList<MailAttachmentInfo> Attachments);

    private async Task ConnectAsync(MailService client, MailEndpoint endpoint, CancellationToken token)
    {
        client.Timeout = 30000;
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port,
                endpoint.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, token);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The mail server connection timed out.", error);
        }
        client.AuthenticationMechanisms.Remove("XOAUTH2");
        client.AuthenticationMechanisms.Remove("OAUTHBEARER");
        await client.AuthenticateAsync(endpoint.User, endpoint.Password, token);
    }

    public async Task TestAsync(CancellationToken token)
    {
        using var imap = new ImapClient();
        await ConnectAsync(imap, incoming, token);
        using var smtp = new SmtpClient();
        await ConnectAsync(smtp, outgoing, token);
    }

    private static async Task<Dictionary<string, IMailFolder>> FindFoldersAsync(ImapClient client, CancellationToken token,
        string? requestedFolder = null, IReadOnlyList<IMailFolder>? listedFolders = null)
    {
        var folders = new Dictionary<string, IMailFolder> { ["Inbox"] = client.Inbox };
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            foreach (var (name, special, _) in SpecialFolders)
            {
                if (client.GetFolder(special) is { } folder)
                    folders[name] = folder;
            }
        }

        if (requestedFolder is not null && folders.ContainsKey(requestedFolder))
            return folders;

        if (folders.Count <= SpecialFolders.Length)
        {
            var candidates = (listedFolders ?? await ListPersonalFoldersAsync(client, StatusItems.None, token))
                .Where(CanOpenFolder).ToArray();
            bool gmail = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
            foreach (var (name, _, attribute) in SpecialFolders)
            {
                var match = candidates.FirstOrDefault(folder => folder.Attributes.HasFlag(attribute));
                if (match is null && !gmail)
                    match = candidates.FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    folders.TryAdd(name, match);
            }
        }
        return folders;
    }

    private static bool CanOpenFolder(IMailFolder folder) =>
        (folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0;

    private static async Task<IReadOnlyList<IMailFolder>> ListPersonalFoldersAsync(ImapClient client,
        StatusItems items, CancellationToken token)
    {
        var folders = new List<IMailFolder>();
        foreach (var ns in client.PersonalNamespaces)
            folders.AddRange(await client.GetFoldersAsync(ns, items, cancellationToken: token));
        return folders;
    }

    private bool SupportsGmailLabels(ImapClient client) =>
        IsGmail || client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);

    internal static bool IsExcludedGmailLabel(string name)
    {
        int separator = name.IndexOf('/');
        if (separator < 0 ||
            !(name[..separator].Equals("[Gmail]", StringComparison.OrdinalIgnoreCase) ||
              name[..separator].Equals("[Google Mail]", StringComparison.OrdinalIgnoreCase)))
            return false;

        string label = name[(separator + 1)..];
        return label.Equals("All Mail", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Important", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Starred", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedGmailFolder(IMailFolder folder) =>
        (folder.Attributes & (FolderAttributes.All | FolderAttributes.Important | FolderAttributes.Flagged)) != 0 ||
        IsExcludedGmailLabel(folder.FullName);

    public async Task<IReadOnlyDictionary<string, FolderCount>> GetFolderCountsAsync(CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var counts = new Dictionary<string, FolderCount>();
        bool listStatus = client.Capabilities.HasFlag(ImapCapabilities.ListStatus);
        const StatusItems countItems = StatusItems.Count | StatusItems.Unread;
        var labels = SupportsGmailLabels(client)
            ? await ListPersonalFoldersAsync(client, listStatus ? countItems : StatusItems.None, token)
            : null;
        var standard = await FindFoldersAsync(client, token, listedFolders: labels);
        foreach (var (name, folder) in standard)
        {
            if (!listStatus || labels is null || !labels.Any(item => item.FullName == folder.FullName))
                await folder.StatusAsync(countItems, token);
            counts[name] = new(folder.Unread, folder.Count);
        }
        if (labels is not null)
        {
            var standardNames = standard.Values.Select(folder => folder.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in labels)
            {
                if (!CanOpenFolder(folder) || folder.Attributes.HasFlag(FolderAttributes.Inbox) ||
                    IsExcludedGmailFolder(folder) || standardNames.Contains(folder.FullName))
                    continue;
                counts[GmailLabelPrefix + folder.FullName] = new(folder.Unread, folder.Count, folder.FullName, listStatus);
            }
        }
        return counts;
    }

    public async Task<FolderCount> GetInboxCountAsync(CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var inbox = client.Inbox;
        await inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, token);
        return new FolderCount(inbox.Unread, inbox.Count);
    }

    internal readonly record struct InboxPosition(uint Validity, uint NextUid, DateTimeOffset CheckedAt);
    internal sealed record InboxArrivalPreview(Guid MessageId, DateTimeOffset ArrivalTime, string? Subject, string? Text);

    public async Task<(InboxPosition? Position, int Arrivals, InboxArrivalPreview? Preview)> CheckInboxArrivalsAsync(
        InboxPosition? previous, DateTimeOffset? notifyAfter, CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var inbox = client.Inbox;
        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await inbox.StatusAsync(StatusItems.UidValidity | StatusItems.UidNext, token);
        if (inbox.UidNext is not { IsValid: true } next || inbox.UidValidity == 0)
            return (previous, 0, null);

        var current = new InboxPosition(inbox.UidValidity, next.Id, checkedAt);
        if (notifyAfter is not { } since || previous is not { } old ||
            old.Validity != current.Validity || old.NextUid >= current.NextUid)
            return (current, 0, null);

        await inbox.OpenAsync(FolderAccess.ReadOnly, token);
        if (inbox.UidValidity != current.Validity)
            return (null, 0, null);
        var range = new UniqueIdRange(new UniqueId(old.NextUid), new UniqueId(current.NextUid - 1));
        var earliestArrival = since > old.CheckedAt ? since : old.CheckedAt;
        var candidates = await inbox.SearchAsync(SearchQuery.Uids(range).And(SearchQuery.NotDraft)
            .And(SearchQuery.DeliveredAfter(earliestArrival.UtcDateTime.Date.AddDays(-1))), token);
        var arrivals = new List<UniqueId>();
        foreach (var batch in candidates.Chunk(100))
        {
            var summaries = await inbox.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, token);
            // A move can assign a new UID while retaining the original delivery time.
            arrivals.AddRange(summaries.Where(item => item.InternalDate >= earliestArrival).Select(item => item.UniqueId));
        }
        var preview = arrivals.Count == 0 ? null : await ReadArrivalPreviewAsync(inbox, arrivals.Max(), token);
        return (current, arrivals.Count, preview);
    }

    private async Task<InboxArrivalPreview?> ReadArrivalPreviewAsync(
        IMailFolder inbox, UniqueId uid, CancellationToken token)
    {
        const int maxSnippetBytes = 8192;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        IMessageSummary? summary = null;
        Guid messageId = RegisterMessage(inbox, uid);
        try
        {
            var items = MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;
            summary = (await inbox.FetchAsync([uid], items, timeout.Token)).FirstOrDefault();
            if (summary is null)
                return null;

            string text = ReadReminderText(await ReadSnippetAsync(summary.TextBody));
            if (string.IsNullOrWhiteSpace(text))
                text = ReadReminderText(await ReadSnippetAsync(summary.HtmlBody));
            return new(messageId, summary.InternalDate ?? DateTimeOffset.MinValue, summary.Envelope?.Subject, text);
        }
        catch (Exception error) when (error is IOException or CommandException or ProtocolException or
            InvalidOperationException or OperationCanceledException or FormatException or ArgumentException)
        {
            // A missing excerpt must not prevent the arrival notification.
            return summary is null ? null : new(messageId, summary.InternalDate ?? DateTimeOffset.MinValue, summary.Envelope?.Subject, null);
        }

        async Task<string?> ReadSnippetAsync(BodyPartText? part)
        {
            if (part is null || part.IsAttachment)
                return null;
            using var stream = await inbox.GetStreamAsync(uid, PreviewSection(part), 0, maxSnippetBytes, timeout.Token);
            return DecodePreviewText(part, stream);
        }
    }

    public async Task<bool> HasUnreadArrivalsAsync(DateTimeOffset since, CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var folder = client.Inbox;
        await folder.OpenAsync(FolderAccess.ReadOnly, token);
        if (folder.Count == 0)
            return false;

        // IMAP SINCE compares calendar dates. Include the previous day for server time zones,
        // then compare each message's actual arrival timestamp to the focus time.
        var criteria = SearchQuery.NotSeen.And(SearchQuery.NotDraft)
            .And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)));
        var matches = await folder.SearchAsync(criteria, token);
        foreach (var uids in matches.OrderByDescending(uid => uid.Id).Chunk(50))
        {
            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.InternalDate | MessageSummaryItems.Flags, token);
            if (summaries.Any(summary => summary.InternalDate >= since &&
                summary.Flags is { } flags && (flags & (MessageFlags.Seen | MessageFlags.Draft)) == 0))
                return true;
        }
        return false;
    }

    public Task<IReadOnlyList<MailSummary>> ListAsync(string folder, string query, CancellationToken token) =>
        ListAsync(folder, query, token, null);

    public async Task<IReadOnlyList<MailSummary>> ListAsync(string folder, string query, CancellationToken token,
        Action<IReadOnlyList<MailSummary>>? publishBatch, int? limit = null, Guid? prioritizedMessage = null,
        Task<IReadOnlyList<MailSummary>>? cachedSearchMatches = null)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var remote = await ResolveFolderAsync(client, folder, token);
        if (remote is null)
            return [];

        await remote.OpenAsync(FolderAccess.ReadOnly, token);
        var terms = Regex.Matches(query, "\"([^\"]*)\"|[^\\s\"]+")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        if (remote.Count == 0)
            return [];

        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure;
        var request = new FetchRequest(items, [HeaderId.ReplyTo]);
        if (terms.Length == 0 && publishBatch is not null && prioritizedMessage is { } target)
        {
            UniqueId uid;
            if (messages.TryGetValue(target, out var location) && location.Folder == remote.FullName &&
                location.Validity == remote.UidValidity)
                uid = location.Uid;
            else
            {
                // Notification activation can start a fresh process without an in-memory UID map.
                var uids = await remote.SearchAsync(SearchQuery.All, token);
                uid = uids.FirstOrDefault(candidate => MessageIdFor(remote, candidate) == target);
            }
            if (uid.IsValid)
            {
                var fetched = await remote.FetchAsync([uid], request, token);
                var targetBatch = ConvertSummaries(remote, folder, fetched, 1);
                if (targetBatch.Length > 0)
                    publishBatch(targetBatch);
            }
        }
        const int FirstBatchSize = 50;
        if (terms.Length == 0 && publishBatch is not null && remote.Count > FirstBatchSize)
        {
            var newest = await remote.FetchAsync(remote.Count - FirstBatchSize, -1, request, token);
            var results = ConvertSummaries(remote, folder, newest, newest.Count).ToList();
            if (results.Count > 0)
                publishBatch(results.ToArray());

            // Page by UID so an expunge cannot shift the older message batches.
            uint firstUid = newest.Count == 0 ? uint.MaxValue : newest.Min(item => item.UniqueId.Id);
            if (firstUid <= 1)
                return results;

            var range = new UniqueIdRange(UniqueId.MinValue, new UniqueId(firstUid - 1));
            var older = await remote.SearchAsync(SearchQuery.Uids(range), token);
            foreach (var uids in older.OrderByDescending(uid => uid.Id).Chunk(200))
            {
                var fetched = await remote.FetchAsync(uids, request, token);
                var batch = ConvertSummaries(remote, folder, fetched, Math.Max(0, LocalDatabase.FolderCacheLimit - results.Count));
                results.AddRange(batch);
                if (batch.Length > 0)
                    publishBatch(batch);
            }
            return results;
        }

        IList<IMessageSummary> summaries;
        if (terms.Length == 0)
        {
            int start = limit is { } max ? Math.Max(0, remote.Count - max) : 0;
            summaries = await remote.FetchAsync(start, -1, request, token);
        }
        else
        {
            var criteria = terms.Select<string, SearchQuery>(SearchQuery.MessageContains).Aggregate((left, right) => left.And(right));
            var uids = (await remote.SearchAsync(criteria, token)).ToList();
            if (cachedSearchMatches is not null)
            {
                var cachedMatches = await cachedSearchMatches;
                token.ThrowIfCancellationRequested();
                var missing = cachedMatches.Select(mail => mail.Id).ToHashSet();
                missing.ExceptWith(uids.Select(uid => MessageIdFor(remote, uid)));
                if (missing.Count > 0)
                {
                    // Server text searches can tokenize docket codes differently from local substring searches.
                    // Confirm cached-only matches against live UIDs so moved/deleted messages cannot linger.
                    var live = await remote.SearchAsync(SearchQuery.All, token);
                    uids.AddRange(live.Where(uid => missing.Contains(MessageIdFor(remote, uid))));
                }
            }
            if (uids.Count == 0)
                return [];
            if (publishBatch is not null)
            {
                var results = new List<MailSummary>();
                var ordered = uids.OrderByDescending(uid => uid.Id).ToArray();
                for (int offset = 0; offset < ordered.Length;)
                {
                    int count = Math.Min(offset == 0 ? FirstBatchSize : 200, ordered.Length - offset);
                    var fetched = await remote.FetchAsync(new ArraySegment<UniqueId>(ordered, offset, count), request, token);
                    var batch = ConvertSummaries(remote, folder, fetched, Math.Max(0, LocalDatabase.FolderCacheLimit - offset));
                    results.AddRange(batch);
                    if (batch.Length > 0)
                        publishBatch(batch);
                    offset += count;
                }
                return results;
            }
            summaries = await remote.FetchAsync(uids, request, token);
        }
        return ConvertSummaries(remote, folder, summaries, LocalDatabase.FolderCacheLimit);
    }

    private async Task<IMailFolder?> ResolveFolderAsync(ImapClient client, string folder, CancellationToken token)
    {
        IMailFolder? remote;
        if (folder.StartsWith(GmailLabelPrefix, StringComparison.Ordinal) && SupportsGmailLabels(client))
        {
            string name = folder[GmailLabelPrefix.Length..];
            if (IsExcludedGmailLabel(name))
                return null;
            try
            {
                remote = await client.GetFolderAsync(name, token);
                if (IsExcludedGmailFolder(remote))
                    return null;
            }
            catch (FolderNotFoundException)
            {
                return null;
            }
        }
        else
            remote = folder == "Inbox" ? client.Inbox :
                (await FindFoldersAsync(client, token, folder)).GetValueOrDefault(folder);
        return remote is not null && CanOpenFolder(remote) ? remote : null;
    }

    private MailSummary[] ConvertSummaries(IMailFolder remote, string folder, IList<IMessageSummary> summaries, int remember)
    {
        var recent = summaries.Count > remember && remember > 0
            ? summaries.OrderByDescending(item => item.UniqueId.Id).Take(remember).Select(item => item.UniqueId).ToHashSet()
            : null;
        return summaries.Select(item =>
        {
            Guid id = RegisterMessage(remote, item.UniqueId);
            if (remember > 0 && (recent is null || recent.Contains(item.UniqueId)) && item.Body is not null &&
                !TryGetPreviewStructure(id, out _))
                RememberPreviewStructure(id, item);
            var envelope = item.Envelope ?? new Envelope();
            // IMAP may substitute the sender in the envelope when the Reply-To header is absent.
            string explicitReplyTo = item.Headers?[HeaderId.ReplyTo] is { } replyToHeader &&
                InternetAddressList.TryParse(replyToHeader, out var replyTo) ? Addresses(replyTo) : "";
            var correspondent = (folder is "Sent" or "Drafts" ? envelope.To : envelope.From).Mailboxes.FirstOrDefault();
            bool standaloneImages = item.BodyParts.Any(IsStandaloneImage);
            return new MailSummary(id, folder, item.Flags?.HasFlag(MessageFlags.Seen) == true,
                item.Flags?.HasFlag(MessageFlags.Flagged) == true, standaloneImages || item.Attachments.Any(),
                envelope.Subject ?? "(No subject)", correspondent?.Name ?? correspondent?.Address ?? "",
                (envelope.Date ?? item.InternalDate)?.LocalDateTime ?? DateTime.MinValue,
                Addresses(envelope.From), Addresses(envelope.To), Addresses(envelope.Cc),
                envelope.ReplyTo.Mailboxes.FirstOrDefault()?.Address ?? envelope.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                BccAddresses: Addresses(envelope.Bcc), ExplicitReplyToAddresses: explicitReplyTo,
                Replied: item.Flags?.HasFlag(MessageFlags.Answered) == true,
                Forwarded: item.Keywords?.Contains(ForwardedKeyword, StringComparer.OrdinalIgnoreCase) == true,
                HasStandaloneImages: standaloneImages);
        }).ToArray();
    }

    private PreviewStructure RememberPreviewStructure(Guid id, IMessageSummary summary)
    {
        var structure = new PreviewStructure(summary.TextBody, summary.HtmlBody,
            summary.BodyParts.Where(part => part.ContentId is not null || part.ContentLocation is not null ||
                IsStandaloneImage(part)).ToArray(), DescribeAttachments(summary));
        RememberPreviewStructure(id, structure);
        return structure;
    }

    private void RememberPreviewStructure(Guid id, PreviewStructure structure)
    {
        lock (previewStructures)
        {
            if (previewStructures.TryAdd(id, structure))
            {
                previewStructureOrder.Enqueue(id);
                if (previewStructureOrder.Count > 2000)
                    previewStructures.Remove(previewStructureOrder.Dequeue());
            }
        }
    }

    private bool TryGetPreviewStructure(Guid id, out PreviewStructure? structure)
    {
        lock (previewStructures)
            return previewStructures.TryGetValue(id, out structure);
    }

    private Guid RegisterMessage(IMailFolder folder, UniqueId uid)
    {
        Guid id = MessageIdFor(folder, uid);
        messages.TryAdd(id, (folder.FullName, folder.UidValidity, uid));
        return id;
    }

    private static Guid MessageIdFor(IMailFolder folder, UniqueId uid) =>
        MessageIdFor(folder.FullName, folder.UidValidity, uid);

    private static Guid MessageIdFor(string fullName, uint validity, UniqueId uid)
    {
        string identity = string.Create(CultureInfo.InvariantCulture, $"{validity}:{uid.Id}:{fullName}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }

    private static string Addresses(InternetAddressList list) =>
        string.Join("; ", list.Mailboxes.Select(mailbox => mailbox.Address));

    public bool KnowsMessage(Guid id) => messages.ContainsKey(id);

    private async Task<(IMailFolder Folder, UniqueId Uid)> OpenMessageFolderAsync(
        ImapClient client, Guid id, FolderAccess access, CancellationToken token)
    {
        if (!messages.TryGetValue(id, out var message))
            throw new InvalidOperationException("Refresh the folder to load this message.");
        await ConnectAsync(client, incoming, token);
        var folder = await client.GetFolderAsync(message.Folder, token);
        await folder.OpenAsync(access, token);
        if (folder.UidValidity != message.Validity)
            throw new InvalidOperationException("The server rebuilt this folder. Refresh it and try again.");
        return (folder, message.Uid);
    }

    public async Task<MailContent> LoadContentAsync(Guid id, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadOnly, token);
        return await LoadContentAsync(folder, uid, id, token);
    }

    private async Task<MailContent> LoadContentAsync(IMailFolder folder, UniqueId uid, Guid id, CancellationToken token)
    {
        var structure = await GetPreviewStructureAsync(folder, uid, id, token);
        var (text, textLimited) = await ReadTextPartAsync(folder, uid, structure.Text, token);
        var (html, htmlLimited) = await ReadTextPartAsync(folder, uid, structure.Html, token);
        string body = text ?? (html is null ? "" : ReadHtmlAsText(html));
        var inlineImages = new List<InlineMailImage>();
        int imageBudget = MaxInlinePreviewBytes;
        var standaloneImages = new StringBuilder();
        var pending = html is null ? null : MailContent.ReferencedContentIds(html);
        var sources = html is null ? null : MailContent.ReferencedImageSources(html);
        foreach (var part in structure.InlineParts)
        {
            string mediaType = part.ContentType.MimeType.ToLowerInvariant();
            string? contentId = part.ContentId?.Trim('<', '>');
            string? location = part.ContentLocation?.OriginalString;
            bool standalone = html is null && IsStandaloneImage(part);
            if (!IsPreviewImage(mediaType) || !(standalone ||
                contentId is not null && pending?.Contains(contentId) == true ||
                location is not null && sources?.Contains(location) == true))
                continue;
            using var image = await ReadPreviewPartAsync(folder, uid, part, Math.Min(imageBudget, MaxInlineImageBytes), token);
            if (image is null)
                continue;
            imageBudget -= (int)image.Length;
            using var content = new MimeContent(image, PreviewEncoding(part));
            using var bytes = new MemoryStream();
            await content.DecodeToAsync(bytes, token);
            contentId ??= MimeKit.Utils.MimeUtils.GenerateMessageId();
            inlineImages.Add(new(contentId, mediaType, bytes.ToArray(), location));
            if (standalone)
            {
                string label = System.Net.WebUtility.HtmlEncode(part.FileName ?? "Inline image");
                standaloneImages.Append($"<p><img src=\"cid:{contentId}\" alt=\"{label}\"></p>");
            }
            if (imageBudget == 0)
                break;
        }
        bool previewTooLarge = text is null && html is null && (textLimited || htmlLimited);
        if (standaloneImages.Length > 0)
        {
            string shownBody = previewTooLarge ? "This message body is too large to preview." : body;
            html = $"<div style=\"white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(shownBody)}</div>{standaloneImages}";
        }
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(html) && !previewTooLarge)
        {
            var original = await ReadOriginalPreviewAsync(folder, uid, token);
            return original with { AttachmentList = structure.Attachments };
        }
        return new(body, Html: html ?? "", InlineImages: inlineImages,
            AttachmentList: structure.Attachments, PreviewTooLarge: previewTooLarge);
    }

    private static async Task<MailContent> ReadOriginalPreviewAsync(
        IMailFolder folder, UniqueId uid, CancellationToken token)
    {
        // Some servers return an empty body section despite a valid original MIME message.
        // Bound this fallback download just like individual preview text parts.
        using var stream = await folder.GetStreamAsync(uid, 0, MaxPreviewTextBytes + 1, token);
        if (stream.Length > MaxPreviewTextBytes)
            return new("", PreviewTooLarge: true);
        using var message = await MimeMessage.LoadAsync(stream, token);
        return await ReadMessageContentAsync(message, token);
    }

    internal static async Task<MailContent> ReadMessageContentAsync(MimeMessage message, CancellationToken token)
    {
        string? html = message.HtmlBody;
        var inlineImages = new List<InlineMailImage>();
        if (html is not null)
        {
            var pending = MailContent.ReferencedContentIds(html);
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                string mediaType = part.ContentType.MimeType.ToLowerInvariant();
                if (mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") ||
                    part.ContentId is not { } contentId || part.Content is null || !pending.Remove(contentId))
                    continue;
                using var data = new MemoryStream();
                await part.Content.DecodeToAsync(data, token);
                inlineImages.Add(new(contentId, mediaType, data.ToArray()));
            }
        }
        return new(message.TextBody ?? (html is null ? "" : ReadHtmlAsText(html)),
            Html: html ?? "", InlineImages: inlineImages);
    }

    public async Task<IReadOnlyList<MailAttachmentInfo>> GetAttachmentsAsync(Guid id, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadOnly, token);
        return (await GetPreviewStructureAsync(folder, uid, id, token)).Attachments;
    }

    private async Task<PreviewStructure> GetPreviewStructureAsync(IMailFolder folder, UniqueId uid, Guid id, CancellationToken token)
    {
        if (TryGetPreviewStructure(id, out var structure) && structure is not null)
            return structure;

        return RememberPreviewStructure(id, await FetchBodyStructureAsync(folder, uid, token));
    }

    private static async Task<IMessageSummary> FetchBodyStructureAsync(IMailFolder folder, UniqueId uid, CancellationToken token)
    {
        var summaries = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
        return summaries.FirstOrDefault() ?? throw new InvalidOperationException("The message is no longer available.");
    }

    private static IReadOnlyList<MailAttachmentInfo> DescribeAttachments(IMessageSummary summary) =>
        summary.Attachments.Concat(summary.BodyParts.Where(IsStandaloneImage)).DistinctBy(part => part.PartSpecifier)
        .Select(part => new MailAttachmentInfo(part.PartSpecifier,
            part.FileName ?? (part is BodyPartMessage ? "message.eml" : "attachment"), part.ContentType.MimeType)).ToArray();

    private static bool IsStandaloneImage(BodyPartBasic part) =>
        part.ContentDisposition?.Disposition.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase) == true &&
        string.IsNullOrEmpty(part.ContentId) && part.ContentLocation is null &&
        IsPreviewImage(part.ContentType.MimeType.ToLowerInvariant());

    private static bool IsPreviewImage(string mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif" or "image/svg+xml";

    public async Task DownloadAttachmentAsync(Guid id, string partId, Stream output, CancellationToken token)
    {
        using var client = new AttachmentImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadOnly, token);
        bool rootPart = partId.Length == 0;
        using var headerStream = await folder.GetStreamAsync(uid, rootPart ? "HEADER" : partId + ".MIME", token);
        using var body = await folder.GetStreamAsync(uid, rootPart ? "TEXT" : partId, token);
        await WriteDownloadedAttachmentAsync(headerStream, body, output, rootPart, token);
    }

    private static async Task WriteDownloadedAttachmentAsync(Stream headerStream, Stream body, Stream output,
        bool rootPart, CancellationToken token)
    {
        var headers = await HeaderList.LoadAsync(headerStream, token);
        if (ContentType.TryParse(headers[HeaderId.ContentType], out var type) &&
            type.MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            if (rootPart)
            {
                var mimeHeaders = new HeaderList();
                foreach (var header in headers.Where(header => header.Field.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
                    mimeHeaders.Add(header);
                var format = FormatOptions.Default.Clone();
                format.NewLineFormat = NewLineFormat.Dos;
                await mimeHeaders.WriteToAsync(format, output, token);
                await output.WriteAsync("\r\n"u8.ToArray(), token);
            }
            else
            {
                headerStream.Position = 0;
                await headerStream.CopyToAsync(output, token);
            }
            await body.CopyToAsync(output, token);
            return;
        }

        var encoding = MimeKit.Utils.MimeUtils.TryParse(headers[HeaderId.ContentTransferEncoding], out ContentEncoding transferEncoding)
            ? transferEncoding : ContentEncoding.Default;
        using var content = new MimeContent(body, encoding);
        await content.DecodeToAsync(output, token);
    }

    private sealed class AttachmentImapClient : ImapClient
    {
        protected override ImapFolder CreateImapFolder(ImapFolderConstructorArgs args) => new AttachmentImapFolder(args);
    }

    private sealed class AttachmentImapFolder(ImapFolderConstructorArgs args) : ImapFolder(args)
    {
        protected override Stream CreateStream(UniqueId? uid, string section, int offset, int length)
        {
            if (length <= 1024 * 1024)
                return base.CreateStream(uid, section, offset, length);
            return new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
    }

    private static Task WriteAttachmentDataAsync(MimeEntity entity, Stream output, CancellationToken token) =>
        entity switch
        {
            MimePart { Content: { } content } => content.DecodeToAsync(output, token),
            MessagePart { Message: { } message } => message.WriteToAsync(output, token),
            _ => entity.WriteToAsync(output, token)
        };

    public async IAsyncEnumerable<(Guid Id, MailContent Content)> PrefetchContentsAsync(
        IReadOnlyList<Guid> ids, [EnumeratorCancellation] CancellationToken token)
    {
        if (ids.Count == 0)
            yield break;

        using var client = new ImapClient();
        var (folder, _) = await OpenMessageFolderAsync(client, ids[0], FolderAccess.ReadOnly, token);
        var known = new Dictionary<UniqueId, Guid>();
        foreach (Guid id in ids)
        {
            if (messages.TryGetValue(id, out var location) &&
                location.Folder == folder.FullName && location.Validity == folder.UidValidity)
                known.TryAdd(location.Uid, id);
        }

        foreach (var uids in known.Keys.Chunk(25))
        {
            var summaries = await folder.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
            foreach (var item in summaries.OrderByDescending(item => item.UniqueId.Id))
            {
                if (!known.TryGetValue(item.UniqueId, out Guid id))
                    continue;

                RememberPreviewStructure(id, item);
                var content = await LoadContentAsync(folder, item.UniqueId, id, token);
                if (!content.PreviewTooLarge)
                    yield return (id, content);

                // Give interactive mail operations time between background downloads.
                await Task.Delay(35, token);
            }
        }
    }

    private static async Task<(string? Text, bool Limited)> ReadTextPartAsync(
        IMailFolder folder, UniqueId uid, BodyPartText? part, CancellationToken token)
    {
        if (part is null || part.IsAttachment)
            return (null, false);

        using var stream = await ReadPreviewPartAsync(folder, uid, part, MaxPreviewTextBytes, token);
        if (stream is null)
            return (null, true);
        return (DecodePreviewText(part, stream), false);
    }

    private static string DecodePreviewText(BodyPartText part, Stream stream)
    {
        using var text = new TextPart(part.ContentType.MediaSubtype)
        {
            Content = new MimeContent(stream, PreviewEncoding(part))
        };
        text.ContentType.Charset = part.ContentType.Charset;
        return text.Text;
    }

    private static async Task<Stream?> ReadPreviewPartAsync(IMailFolder folder, UniqueId uid, BodyPartBasic part,
        int limit, CancellationToken token)
    {
        if (limit <= 0 || part.Octets > limit)
            return null;
        // Request one extra byte to detect an oversized part even if BODYSTRUCTURE understated its size.
        var stream = await folder.GetStreamAsync(uid, PreviewSection(part), 0, limit + 1, token);
        if (stream.Length <= limit)
            return stream;
        stream.Dispose();
        return null;
    }

    private static string PreviewSection(BodyPart part) =>
        string.IsNullOrEmpty(part.PartSpecifier) ? "TEXT" : part.PartSpecifier;

    private static ContentEncoding PreviewEncoding(BodyPartBasic part) => part.ContentTransferEncoding?.ToLowerInvariant() switch
    {
        "base64" => ContentEncoding.Base64,
        "quoted-printable" => ContentEncoding.QuotedPrintable,
        "x-uuencode" or "uuencode" or "uue" or "x-uue" => ContentEncoding.UUEncode,
        _ => ContentEncoding.Default
    };

    internal static string ReadReminderText(string? value)
    {
        string text = value ?? "";
        for (int pass = 0; pass < 3; pass++)
        {
            string decoded = System.Net.WebUtility.HtmlDecode(text);
            if (decoded == text)
                break;
            text = decoded;
        }

        if (text.Contains('<'))
            text = ReadHtmlAsText(text, stripNonVisibleContent: true);

        // A byte-limited excerpt can end mid-tag or contain a bare stylesheet.
        return Regex.IsMatch(text, @"<\s*(?:[!/?]|[a-z][\w:-]*(?=\s|[/>]|$))|&(?:lt|#0*60|#x0*3c);|/\*|@(?:media|font-face|import|supports)\b|\{\s*[-\w]+\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "" : text;
    }

    private static string ReadHtmlAsText(string html, bool stripNonVisibleContent = false)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        var text = new System.Text.StringBuilder();
        string? hiddenTag = null;
        while (tokenizer.ReadNextToken(out var part))
        {
            if (hiddenTag is not null)
            {
                if (part is HtmlTagToken { IsEndTag: true } close &&
                    hiddenTag.Equals(close.Name, StringComparison.OrdinalIgnoreCase))
                    hiddenTag = null;
                continue;
            }
            if (stripNonVisibleContent && part is HtmlTagToken { IsEndTag: false, IsEmptyElement: false } open &&
                open.Name.ToLowerInvariant() is "head" or "style" or "script" or "title" or "template" or "noscript" or "xml")
            {
                hiddenTag = open.Name;
                continue;
            }
            if (part is HtmlDataToken data && part.Kind == HtmlTokenKind.Data)
                text.Append(data.Data);
            else if (part is HtmlTagToken { Id: HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div })
                text.AppendLine();
        }
        return text.ToString().Trim();
    }

    public async Task<bool> MarkResponseAsync(ResponseOrigin response, CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var folder = await ResolveFolderAsync(client, response.Folder, token);
        if (folder is null)
            return false;
        await folder.OpenAsync(FolderAccess.ReadWrite, token);
        if (response.IsReply ? !folder.PermanentFlags.HasFlag(MessageFlags.Answered) :
            !folder.PermanentFlags.HasFlag(MessageFlags.UserDefined) &&
            !folder.PermanentKeywords.Contains(ForwardedKeyword, StringComparer.OrdinalIgnoreCase))
            return false;

        var uids = await ResolveMessageUidsAsync(folder, [response.MessageId], token);
        if (response.IsReply)
            await folder.AddFlagsAsync(uids, MessageFlags.Answered, true, token);
        else
            await folder.AddFlagsAsync(uids, MessageFlags.None, [ForwardedKeyword], true, token);
        return true;
    }

    public async Task UpdateFlagsAsync(string folderName, Guid id, bool? read, bool? star, CancellationToken token)
    {
        using var client = new ImapClient();
        IMailFolder folder;
        UniqueId uid;
        if (messages.ContainsKey(id))
        {
            (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadWrite, token);
        }
        else
        {
            await ConnectAsync(client, incoming, token);
            folder = await ResolveFolderAsync(client, folderName, token)
                ?? throw new InvalidOperationException("The message folder is unavailable on this server.");
            await folder.OpenAsync(FolderAccess.ReadWrite, token);
            uid = (await ResolveMessageUidsAsync(folder, [id], token))[0];
        }
        var add = MessageFlags.None;
        var remove = MessageFlags.None;
        if (read == true) add |= MessageFlags.Seen;
        if (read == false) remove |= MessageFlags.Seen;
        if (star == true) add |= MessageFlags.Flagged;
        if (star == false) remove |= MessageFlags.Flagged;
        if (add != MessageFlags.None)
            await folder.AddFlagsAsync(uid, add, true, token);
        if (remove != MessageFlags.None)
            await folder.RemoveFlagsAsync(uid, remove, true, token);
    }

    public async Task<MessageMoveResult> DeleteAsync(string folder, IReadOnlyCollection<Guid> ids, CancellationToken token)
    {
        if (folder != "Trash")
        {
            return await MoveMessagesAsync(folder, "Trash", ids, token);
        }
        if (ids.Count == 0)
            return new(true);

        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var source = await ResolveFolderAsync(client, folder, token)
            ?? throw new InvalidOperationException("The source folder is unavailable on this server.");
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            throw new InvalidOperationException("This server cannot permanently delete only the selected messages.");

        await source.OpenAsync(FolderAccess.ReadWrite, token);
        var uids = await ResolveMessageUidsAsync(source, ids, token);
        await source.AddFlagsAsync(uids, MessageFlags.Deleted | MessageFlags.Seen, true, token);
        await source.ExpungeAsync(uids, token);
        foreach (Guid id in ids)
            messages.TryRemove(id, out _);
        return new(true);
    }

    public Task<MessageMoveResult> MoveAsync(string sourceFolder, string destinationFolder, IReadOnlyCollection<Guid> ids,
        CancellationToken token) =>
        MoveMessagesAsync(sourceFolder, destinationFolder, ids, token);

    private async Task<MessageMoveResult> MoveMessagesAsync(string sourceFolder, string destinationFolder, IReadOnlyCollection<Guid> ids,
        CancellationToken token)
    {
        if (ids.Count == 0)
            return new(false);
        if (sourceFolder == destinationFolder)
            throw new InvalidOperationException("Choose a different destination folder.");

        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var source = await ResolveFolderAsync(client, sourceFolder, token)
            ?? throw new InvalidOperationException("The source folder is unavailable on this server.");
        var destination = await ResolveFolderAsync(client, destinationFolder, token);
        if (destination is null || destination.FullName == source.FullName)
            throw new InvalidOperationException("The destination folder is unavailable on this server.");
        if ((client.Capabilities & (ImapCapabilities.UidPlus | ImapCapabilities.Move)) == 0)
            throw new InvalidOperationException("This server cannot move only the selected messages.");

        await source.OpenAsync(FolderAccess.ReadWrite, token);
        var uids = await ResolveMessageUidsAsync(source, ids, token);
        bool markRead = MailFolders.MarksMovedMessagesRead(destinationFolder) ||
            MailFolders.MarksMovedMessagesRead(destination.Name);
        var moved = await source.MoveToAsync(uids, destination, token);
        bool markedRead = false;
        if (markRead && moved.Count == uids.Count)
        {
            try
            {
                await destination.OpenAsync(FolderAccess.ReadWrite, token);
                var movedUids = moved.Destination.ToList();
                if (movedUids.All(uid => uid.Validity == 0 || uid.Validity == destination.UidValidity))
                {
                    await destination.AddFlagsAsync(movedUids, MessageFlags.Seen, true, token);
                    markedRead = true;
                }
            }
            catch (Exception error)
            {
                // The move already succeeded. A later refresh can read the destination flags.
                System.Diagnostics.Debug.WriteLine($"Could not confirm the destination read flag ({error.GetType().Name}).");
            }
        }
        var destinationIds = new Dictionary<Guid, Guid>();
        foreach (var pair in moved)
        {
            var uid = pair.Value;
            uint validity = uid.Validity == 0 ? destination.UidValidity : uid.Validity;
            if (validity == 0)
                continue;
            var sourceId = MessageIdFor(source, pair.Key);
            var id = MessageIdFor(destination.FullName, validity, uid);
            destinationIds[sourceId] = id;
            messages.TryAdd(id, (destination.FullName, validity, uid));
            if (TryGetPreviewStructure(sourceId, out var structure) && structure is not null)
                RememberPreviewStructure(id, structure);
        }
        foreach (Guid id in ids)
            messages.TryRemove(id, out _);
        return new(markedRead, destinationIds);
    }

    private async Task<IList<UniqueId>> ResolveMessageUidsAsync(IMailFolder source, IReadOnlyCollection<Guid> ids,
        CancellationToken token)
    {
        var uids = new List<UniqueId>(ids.Count);
        foreach (Guid id in ids)
        {
            if (messages.TryGetValue(id, out var message) &&
                message.Folder == source.FullName && message.Validity == source.UidValidity)
                uids.Add(message.Uid);
        }

        if (uids.Count == ids.Count)
            return uids;

        // Cached rows carry stable IDs, while the UID map is populated by the live fetch.
        uids.Clear();
        var pending = ids.ToHashSet();
        var serverUids = await source.SearchAsync(SearchQuery.All, token);
        for (int i = serverUids.Count - 1; i >= 0 && pending.Count > 0; i--)
        {
            token.ThrowIfCancellationRequested();
            UniqueId uid = serverUids[i];
            if (pending.Remove(MessageIdFor(source, uid)))
                uids.Add(uid);
        }

        if (pending.Count > 0)
            throw new InvalidOperationException("Refresh the folder and select the messages again.");
        return uids;
    }

    public async Task ExportAsync(string folder, IReadOnlySet<Guid>? ids, System.IO.Compression.ZipArchive archive,
        IProgress<(int Done, int Total)> progress, CancellationToken token)
    {
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var source = await ResolveFolderAsync(client, folder, token)
            ?? throw new InvalidOperationException("The folder is unavailable on the server.");
        await source.OpenAsync(FolderAccess.ReadOnly, token);
        var uids = ids is null
            ? await source.SearchAsync(SearchQuery.All, token)
            : await ResolveMessageUidsAsync(source, ids, token);
        var names = new ExportFileNames();
        progress.Report((0, uids.Count));
        for (int i = 0; i < uids.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var message = await source.GetStreamAsync(uids[i], token);
            using var buffered = message.CanSeek ? null : new MemoryStream();
            var original = (Stream?)buffered ?? message;
            if (buffered is not null)
            {
                await message.CopyToAsync(buffered, token);
                buffered.Position = 0;
            }

            var parser = new MimeParser(original, MimeFormat.Entity);
            var headers = await parser.ParseHeadersAsync(token);
            DateTime? date = MimeKit.Utils.DateUtils.TryParse(headers[HeaderId.Date], out var sent)
                ? sent.LocalDateTime : null;
            if (date is null)
            {
                var summaries = await source.FetchAsync([uids[i]], MessageSummaryItems.InternalDate, token);
                date = summaries.FirstOrDefault()?.InternalDate?.LocalDateTime;
            }
            original.Position = 0;
            await using var output = archive.CreateEntry(names.Message(date, headers[HeaderId.Subject])).Open();
            await original.CopyToAsync(output, token);
            if (i == 0 || (i + 1) % 25 == 0 || i + 1 == uids.Count)
                progress.Report((i + 1, uids.Count));
        }
    }

    public async Task<DraftSaveResult> SaveDraftAsync(OutgoingMail mail, Guid? replacing, CancellationToken token)
    {
        string messageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        using var message = await Task.Run(() => CreateOutgoingMessage(mail, messageId, draft: true), token);
        using var client = new ImapClient();
        await ConnectAsync(client, incoming, token);
        var drafts = (await FindFoldersAsync(client, token, "Drafts")).GetValueOrDefault("Drafts")
            ?? throw new InvalidOperationException("Lit3Mail could not find the server's Drafts folder.");
        await drafts.OpenAsync(FolderAccess.ReadWrite, token);

        var options = FormatOptions.Default.Clone();
        options.HiddenHeaders.Remove(HeaderId.Bcc);
        UniqueId? uid = await drafts.AppendAsync(options, message, MessageFlags.Draft | MessageFlags.Seen, token);
        bool replaced = false;
        string? warning = null;
        // The new copy is already saved. Failure to locate it or remove the previous copy is separate.
        try
        {
            if (uid is null)
            {
                var matches = await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", $"<{messageId}>"), token);
                uid = matches.Count > 0 ? matches[^1] : null;
            }
            if (replacing is { } oldId)
            {
                if (!messages.TryGetValue(oldId, out var old) || old.Folder != drafts.FullName ||
                    old.Validity != drafts.UidValidity || !client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
                    warning = "The new draft was saved. The previous copy is still in Drafts.";
                else
                {
                    await ExpungeDraftAsync(drafts, old.Uid, token);
                    messages.TryRemove(oldId, out _);
                    replaced = true;
                }
            }
        }
        catch (Exception)
        {
            warning = "The draft was saved, but Lit3Mail could not finish updating the Drafts folder.";
        }

        var summary = uid is { } id
            ? new MailSummary(RegisterMessage(drafts, id), "Drafts", true, false, mail.Files is { Count: > 0 }, mail.Subject, mail.To,
                message.Date.LocalDateTime, mail.From, Addresses(message.To), Addresses(message.Cc), mail.ReplyTo,
                BccAddresses: Addresses(message.Bcc), ExplicitReplyToAddresses: Addresses(message.ReplyTo))
            : null;
        if (summary is null)
            warning ??= "The server accepted the draft, but Lit3Mail could not locate it in Drafts yet.";
        return new(summary, replaced, warning);
    }

    public async Task<OutgoingMail> LoadDraftAsync(Guid id, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadOnly, token);
        var summaries = await folder.FetchAsync([uid], MessageSummaryItems.Size, token);
        if (summaries.FirstOrDefault()?.Size is not { } size || size > ComposeAttachmentLimits.MaxDraftMessageBytes)
            throw new DraftSizeException("The draft size could not be verified or exceeds the 80 MiB download limit.");
        using var message = await folder.GetMessageAsync(uid, token);
        var content = await ReadMessageContentAsync(message, token);
        var files = new List<MailFileAttachment>();
        int totalBytes = 0;
        foreach (var part in message.Attachments)
        {
            using var data = new ComposeAttachmentLimits.DecodeBuffer(ComposeAttachmentLimits.RemainingFileBytes(totalBytes));
            await WriteAttachmentDataAsync(part, data, token);
            totalBytes += (int)data.Length;
            string fileName = part.ContentDisposition?.FileName ?? part.ContentType.Name ??
                (part is MessagePart ? "message.eml" : "attachment");
            files.Add(new(fileName, part.ContentType.MimeType, data.ToArray()));
        }
        return new(Addresses(message.From), message.ReplyTo.Mailboxes.FirstOrDefault()?.Address ?? "",
            Addresses(message.To), Addresses(message.Cc), message.Subject ?? "", content.Body, content.Html,
            content.InlineImages, Bcc: Addresses(message.Bcc), Files: files);
    }

    public async Task RemoveDraftAsync(Guid id, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadWrite, token);
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
            throw new InvalidOperationException("The server does not support removing a single draft.");
        await ExpungeDraftAsync(folder, uid, token);
        messages.TryRemove(id, out _);
    }

    private static async Task ExpungeDraftAsync(IMailFolder folder, UniqueId uid, CancellationToken token)
    {
        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, token);
        await folder.ExpungeAsync([uid], token);
    }

    public async Task<SentMailResult> SendAsync(OutgoingMail mail, CancellationToken token)
    {
        string messageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        using var message = await Task.Run(() => CreateOutgoingMessage(mail, messageId), token);

        using (var client = new SmtpClient())
        {
            await ConnectAsync(client, outgoing, token);
            await client.SendAsync(CreateSmtpFormatOptions(), message, token);
        }

        // SMTP has accepted the message. A failure to file the copy must never cause a resend.
        using var filingTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        filingTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await FileSentCopyAsync(message, messageId, mail, filingTimeout.Token);
        }
        catch (Exception)
        {
            return new(null, "Message sent. Lit3Mail could not confirm the copy in Sent.");
        }
    }

    private static MimeMessage CreateOutgoingMessage(OutgoingMail mail, string messageId, bool draft = false)
    {
        var message = new MimeMessage
        {
            MessageId = messageId,
            Subject = mail.Subject,
            Body = CreateMimeBody(mail)
        };
        message.From.Add(MailboxAddress.Parse(mail.From));
        if (draft && MailboxAddress.TryParse(mail.ReplyTo, out var replyTo))
            message.ReplyTo.Add(replyTo);
        else if (!draft && !string.IsNullOrWhiteSpace(mail.ReplyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(mail.ReplyTo));
        AddRecipients(message.To, mail.To, draft);
        AddRecipients(message.Cc, mail.Cc, draft);
        AddRecipients(message.Bcc, mail.Bcc, draft);
        return message;
    }

    internal static FormatOptions CreateSmtpFormatOptions()
    {
        var options = FormatOptions.Default.Clone();
        options.HiddenHeaders.Add(HeaderId.Bcc);
        return options;
    }

    private async Task<SentMailResult> FileSentCopyAsync(MimeMessage message, string messageId, OutgoingMail mail, CancellationToken token)
    {
        using var imap = new ImapClient();
        await ConnectAsync(imap, incoming, token);
        var sent = (await FindFoldersAsync(imap, token, "Sent")).GetValueOrDefault("Sent");
        if (sent is null)
            return new(null, "Message sent. The server has no Sent folder Lit3Mail can use.");

        await sent.OpenAsync(FolderAccess.ReadOnly, token);
        var criteria = SearchQuery.HeaderContains("Message-ID", $"<{messageId}>");
        var matches = await sent.SearchAsync(criteria, token);
        UniqueId? uid = matches.Count > 0 ? matches[^1] : null;
        bool gmailFilesCopy = string.Equals(incoming.Host, "imap.gmail.com", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(outgoing.Host, "smtp.gmail.com", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(incoming.User, outgoing.User, StringComparison.OrdinalIgnoreCase);
        if (uid is null && gmailFilesCopy)
        {
            // Gmail SMTP files its own copy. Allow time for the IMAP view to catch up.
            foreach (int delay in new[] { 250, 500 })
            {
                await Task.Delay(delay, token);
                matches = await sent.SearchAsync(criteria, token);
                if (matches.Count == 0)
                    continue;
                uid = matches[^1];
                break;
            }

            if (uid is null)
                return new(null);
        }
        else if (uid is null)
        {
            uid = await sent.AppendAsync(message, MessageFlags.Seen, token);
        }

        return uid is { } id
            ? new(new MailSummary(RegisterMessage(sent, id), "Sent", true, false, mail.Files is { Count: > 0 }, mail.Subject,
                mail.To, message.Date.LocalDateTime, mail.From, Addresses(message.To), Addresses(message.Cc),
                string.IsNullOrWhiteSpace(mail.ReplyTo) ? mail.From : mail.ReplyTo,
                BccAddresses: Addresses(message.Bcc), ExplicitReplyToAddresses: Addresses(message.ReplyTo)))
            : new(null);
    }

    internal static MimeEntity CreateMimeBody(OutgoingMail mail)
    {
        var builder = new BodyBuilder { TextBody = mail.Body, HtmlBody = mail.Html };
        foreach (var image in mail.InlineImages ?? [])
        {
            var part = builder.LinkedResources.Add("image.png", image.Bytes, ContentType.Parse(image.MediaType));
            part.ContentId = image.ContentId;
        }
        foreach (var file in mail.Files ?? [])
            builder.Attachments.Add(file.FileName, file.Bytes, ContentType.Parse(file.MediaType));
        return builder.ToMessageBody();
    }

    internal static void AddRecipients(InternetAddressList recipients, string input, bool allowIncomplete = false)
    {
        foreach (string segment in RecipientSeparators.Split(input))
        {
            if (!allowIncomplete)
                recipients.AddRange(InternetAddressList.Parse(segment));
            else if (InternetAddressList.TryParse(segment, out var parsed))
                recipients.AddRange(parsed);
        }
    }
}
















