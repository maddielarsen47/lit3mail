namespace Lit3Mail;

public partial class MailForm
{
    private sealed record CacheWork(MailAccount Account, ServerMailbox Mailbox, string Folder,
        string ConnectionKey, IReadOnlyList<MailSummary>? Summaries);

    private readonly LinkedList<CacheWork> cacheQueue = new();
    private readonly HashSet<ServerMailbox> scheduledMailboxes = [];
    private CacheWork? activeCacheWork;
    private CancellationTokenSource? activeCacheCancellation;
    private bool processingCache;

    private void QueueBackgroundCache(FolderEntry? current = null, IReadOnlyList<MailSummary>? summaries = null)
    {
        if (database is null || IsDisposed)
            return;

        if (current is { Account.Mailbox: ServerMailbox mailbox, Folder: { } folder })
        {
            var node = FindQueuedCacheWork(mailbox, folder);
            if (node is not null)
                cacheQueue.Remove(node);
            cacheQueue.AddFirst(new CacheWork(current.Account, mailbox, folder, current.Account.ConnectionKey(), summaries));
        }

        foreach (var account in accounts)
        {
            if (account.Mailbox is not ServerMailbox server || !scheduledMailboxes.Add(server))
                continue;
            string key = account.ConnectionKey();
            foreach (string name in DefaultFolders)
            {
                if (FindQueuedCacheWork(server, name) is null)
                    cacheQueue.AddLast(new CacheWork(account, server, name, key, null));
            }
        }

        if (!processingCache)
            _ = ProcessCacheQueueAsync(database);
    }

    private LinkedListNode<CacheWork>? FindQueuedCacheWork(ServerMailbox mailbox, string folder)
    {
        for (var node = cacheQueue.First; node is not null; node = node.Next)
        {
            if (ReferenceEquals(node.Value.Mailbox, mailbox) && node.Value.Folder == folder)
                return node;
        }
        return null;
    }

    private void QueueInboxRefresh(MailAccount? refreshingInbox)
    {
        if (database is null || IsDisposed)
            return;
        foreach (var account in accounts)
        {
            if (account == refreshingInbox || account.Mailbox is not ServerMailbox mailbox)
                continue;
            var queued = FindQueuedCacheWork(mailbox, "Inbox");
            if (queued is not null)
                cacheQueue.Remove(queued);
            cacheQueue.AddFirst(new CacheWork(account, mailbox, "Inbox", account.ConnectionKey(), null));
        }
        if (cacheQueue.Count > 0 && !processingCache)
            _ = ProcessCacheQueueAsync(database);
    }

    private async Task ProcessCacheQueueAsync(LocalDatabase database)
    {
        processingCache = true;
        try
        {
            // Let startup and the first interactive preview finish before downloading more mail.
            await Task.Delay(750, mailboxLifetime.Token);
            while (cacheQueue.First is { } node && !IsDisposed)
            {
                var work = node.Value;
                cacheQueue.RemoveFirst();
                if (!accounts.Contains(work.Account) || !ReferenceEquals(work.Account.Mailbox, work.Mailbox))
                    continue;

                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
                activeCacheWork = work;
                activeCacheCancellation = cancellation;
                try
                {
                    await Task.Run(() => CacheFolderAsync(database, work, cancellation.Token), cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine($"Background mail cache failed ({error.GetType().Name}).");
                }
                finally
                {
                    activeCacheWork = null;
                    activeCacheCancellation = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            processingCache = false;
        }
    }

    private static async Task CacheFolderAsync(LocalDatabase database, CacheWork work, CancellationToken token)
    {
        Guid account = work.Account.Id;
        var cachedBodies = database.GetCachedMessageBodies(account, work.Folder, work.ConnectionKey);
        var summaries = work.Summaries;
        if (summaries is null)
        {
            string? revision = database.GetFolderCacheRevision(account, work.Folder);
            summaries = await work.Mailbox.ListAsync(work.Folder, "", token, null, LocalDatabase.FolderCacheLimit);
            token.ThrowIfCancellationRequested();
            database.SaveFolderCache(account, work.Folder, work.ConnectionKey, summaries,
                onlyIfUnchanged: true, expectedRevision: revision);
        }

        var recent = summaries.OrderByDescending(mail => mail.Date).Take(LocalDatabase.FolderCacheLimit).ToArray();
        token.ThrowIfCancellationRequested();
        database.SaveKnownAddresses(account, work.ConnectionKey,
            recent.SelectMany(mail => new[] { mail.FromAddress, mail.ToAddresses, mail.CcAddresses, mail.ReplyToAddress }));

        var missing = recent
            .Where(mail => !cachedBodies.Contains(mail.Id))
            .Select(mail => mail.Id).ToArray();

        var batch = new List<(Guid Id, MailContent Content)>(25);
        await foreach (var body in work.Mailbox.PrefetchContentsAsync(missing, token))
        {
            token.ThrowIfCancellationRequested();
            batch.Add(body);
            if (batch.Count == 25)
            {
                database.SaveMessageBodies(account, work.Folder, work.ConnectionKey, batch, onlyIfMissing: true);
                batch.Clear();
            }
        }
        token.ThrowIfCancellationRequested();
        if (batch.Count > 0)
            database.SaveMessageBodies(account, work.Folder, work.ConnectionKey, batch, onlyIfMissing: true);
    }

    private void CancelBackgroundCache(MailAccount account)
    {
        if (account.Mailbox is ServerMailbox mailbox)
            scheduledMailboxes.Remove(mailbox);
        for (var node = cacheQueue.First; node is not null;)
        {
            var next = node.Next;
            if (ReferenceEquals(node.Value.Account, account))
                cacheQueue.Remove(node);
            node = next;
        }

        if (ReferenceEquals(activeCacheWork?.Account, account))
            activeCacheCancellation?.Cancel();
    }
}



