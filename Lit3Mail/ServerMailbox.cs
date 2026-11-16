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
    private const int MaxPreviewTextBytes = 1024 * 1024;
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
        await client.ConnectAsync(endpoint.Host, endpoint.Port,
            endpoint.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, token);
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
        Action<IReadOnlyList<MailSummary>>? publishBatch, int? limit = null)
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
        const int FirstBatchSize = 50;
        if (terms.Length == 0 && publishBatch is not null && remote.Count > FirstBatchSize)
        {
            var newest = await remote.FetchAsync(remote.Count - FirstBatchSize, -1, items, token);
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
                var fetched = await remote.FetchAsync(uids, items, token);
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
            summaries = await remote.FetchAsync(start, -1, items, token);
        }
        else
        {
            var criteria = terms.Select<string, SearchQuery>(SearchQuery.MessageContains).Aggregate((left, right) => left.And(right));
            var uids = await remote.SearchAsync(criteria, token);
            if (uids.Count == 0)
                return [];
            if (publishBatch is not null)
            {
                var results = new List<MailSummary>();
                var ordered = uids.OrderByDescending(uid => uid.Id).ToArray();
                for (int offset = 0; offset < ordered.Length;)
                {
                    int count = Math.Min(offset == 0 ? FirstBatchSize : 200, ordered.Length - offset);
                    var fetched = await remote.FetchAsync(new ArraySegment<UniqueId>(ordered, offset, count), items, token);
                    var batch = ConvertSummaries(remote, folder, fetched, Math.Max(0, LocalDatabase.FolderCacheLimit - offset));
                    results.AddRange(batch);
                    if (batch.Length > 0)
                        publishBatch(batch);
                    offset += count;
                }
                return results;
            }
            summaries = await remote.FetchAsync(uids, items, token);
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
            var correspondent = (folder is "Sent" or "Drafts" ? envelope.To : envelope.From).Mailboxes.FirstOrDefault();
            return new MailSummary(id, folder, item.Flags?.HasFlag(MessageFlags.Seen) == true,
                item.Flags?.HasFlag(MessageFlags.Flagged) == true, item.Attachments.Any(),
                envelope.Subject ?? "(No subject)", correspondent?.Name ?? correspondent?.Address ?? "",
                (envelope.Date ?? item.InternalDate)?.LocalDateTime ?? DateTime.MinValue,
                Addresses(envelope.From), Addresses(envelope.To), Addresses(envelope.Cc),
                envelope.ReplyTo.Mailboxes.FirstOrDefault()?.Address ?? envelope.From.Mailboxes.FirstOrDefault()?.Address ?? "");
        }).ToArray();
    }

    private PreviewStructure RememberPreviewStructure(Guid id, IMessageSummary summary)
    {
        var structure = new PreviewStructure(summary.TextBody, summary.HtmlBody,
            summary.BodyParts.Where(part => part.ContentId is not null || part.ContentLocation is not null).ToArray(), DescribeAttachments(summary));
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
        var structure = await GetPreviewStructureAsync(folder, uid, id, token);
        var (text, textLimited) = await ReadTextPartAsync(folder, uid, structure.Text, token);
        var (html, htmlLimited) = await ReadTextPartAsync(folder, uid, structure.Html, token);
        var inlineImages = new List<InlineMailImage>();
        int imageBudget = MaxInlinePreviewBytes;
        if (html is not null)
        {
            var pending = MailContent.ReferencedContentIds(html);
            var sources = MailContent.ReferencedImageSources(html);
            foreach (var part in structure.InlineParts)
            {
                string mediaType = part.ContentType.MimeType.ToLowerInvariant();
                string? contentId = part.ContentId?.Trim('<', '>');
                string? location = part.ContentLocation?.OriginalString;
                if (part is not BodyPartBasic imagePart ||
                    mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif" or "image/svg+xml") ||
                    !(contentId is not null && pending.Contains(contentId) || location is not null && sources.Contains(location)))
                    continue;
                using var image = await ReadPreviewPartAsync(folder, uid, imagePart, Math.Min(imageBudget, MaxInlineImageBytes), token);
                if (image is null)
                    continue;
                imageBudget -= (int)image.Length;
                using var content = new MimeContent(image, PreviewEncoding(imagePart));
                using var bytes = new MemoryStream();
                await content.DecodeToAsync(bytes, token);
                inlineImages.Add(new(contentId ?? MimeKit.Utils.MimeUtils.GenerateMessageId(), mediaType, bytes.ToArray(), location));
                if (imageBudget == 0)
                    break;
            }
        }
        return new(text ?? (html is null ? "" : ReadHtmlAsText(html)), Html: html ?? "",
            InlineImages: inlineImages, AttachmentList: structure.Attachments, PreviewTooLarge: text is null && html is null &&
                (textLimited || htmlLimited));
    }

    private static async Task<MailContent> ReadMessageContentAsync(MimeMessage message, CancellationToken token)
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
        summary.Attachments.Select(part => new MailAttachmentInfo(part.PartSpecifier,
            part.FileName ?? (part is BodyPartMessage ? "message.eml" : "attachment"), part.ContentType.MimeType)).ToArray();

    public async Task DownloadAttachmentAsync(Guid id, string partId, Stream output, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadOnly, token);
        using var part = await ((IImapFolder)folder).GetBodyPartAsync(uid, partId, token);
        await WriteAttachmentDataAsync(part, output, token);
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
            foreach (var item in summaries)
            {
                if (!known.TryGetValue(item.UniqueId, out Guid id) || item.TextBody is null && item.HtmlBody is null)
                    continue;

                var (text, _) = await ReadTextPartAsync(folder, item.UniqueId, item.TextBody, token);
                var (html, _) = await ReadTextPartAsync(folder, item.UniqueId, item.HtmlBody, token);
                if (text is not null || html is not null)
                    yield return (id, new MailContent(text ?? ReadHtmlAsText(html!), Html: html ?? ""));

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
        using var text = new TextPart(part.ContentType.MediaSubtype)
        {
            Content = new MimeContent(stream, PreviewEncoding(part))
        };
        text.ContentType.Charset = part.ContentType.Charset;
        return (text.Text, false);
    }

    private static async Task<Stream?> ReadPreviewPartAsync(IMailFolder folder, UniqueId uid, BodyPartBasic part,
        int limit, CancellationToken token)
    {
        if (limit <= 0 || part.Octets > limit)
            return null;
        // Request one extra byte to detect an oversized part even if BODYSTRUCTURE understated its size.
        string section = string.IsNullOrEmpty(part.PartSpecifier) ? "TEXT" : part.PartSpecifier;
        var stream = await folder.GetStreamAsync(uid, section, 0, limit + 1, token);
        if (stream.Length <= limit)
            return stream;
        stream.Dispose();
        return null;
    }

    private static ContentEncoding PreviewEncoding(BodyPartBasic part) => part.ContentTransferEncoding?.ToLowerInvariant() switch
    {
        "base64" => ContentEncoding.Base64,
        "quoted-printable" => ContentEncoding.QuotedPrintable,
        "x-uuencode" or "uuencode" or "uue" or "x-uue" => ContentEncoding.UUEncode,
        _ => ContentEncoding.Default
    };

    private static string ReadHtmlAsText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        var text = new System.Text.StringBuilder();
        while (tokenizer.ReadNextToken(out var part))
        {
            if (part is HtmlDataToken data && part.Kind == HtmlTokenKind.Data)
                text.Append(data.Data);
            else if (part is HtmlTagToken { Id: HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div })
                text.AppendLine();
        }
        return text.ToString().Trim();
    }

    public async Task UpdateFlagsAsync(Guid id, bool? read, bool? star, CancellationToken token)
    {
        using var client = new ImapClient();
        var (folder, uid) = await OpenMessageFolderAsync(client, id, FolderAccess.ReadWrite, token);
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
        if (markRead)
            await source.AddFlagsAsync(uids, MessageFlags.Seen, true, token);
        var moved = await source.MoveToAsync(uids, destination, token);
        if (markRead && moved.Count > 0)
        {
            try
            {
                await destination.OpenAsync(FolderAccess.ReadWrite, token);
                await destination.AddFlagsAsync(moved.Destination.ToList(), MessageFlags.Seen, true, token);
            }
            catch (Exception error) when (error is IOException or CommandException or ProtocolException or
                InvalidOperationException or OperationCanceledException)
            {
                // The move already succeeded and the source was marked read before it moved.
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
        return new(markRead, destinationIds);
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
        using var message = CreateOutgoingMessage(mail, messageId, draft: true);
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
                message.Date.LocalDateTime, mail.From, Addresses(message.To), Addresses(message.Cc), mail.ReplyTo)
            : null;
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
        using var message = CreateOutgoingMessage(mail, messageId);

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
                string.IsNullOrWhiteSpace(mail.ReplyTo) ? mail.From : mail.ReplyTo))
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
















