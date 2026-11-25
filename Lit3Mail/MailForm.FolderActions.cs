namespace Lit3Mail;

public partial class MailForm
{
    private bool markingFolderRead;
    private sealed class FolderReadOptimism(FolderEntry entry, IMailbox mailbox,
        Dictionary<Guid, bool> originalRead, FolderCount? originalCount)
    {
        public FolderEntry Entry { get; } = entry;
        public IMailbox Mailbox { get; } = mailbox;
        public Dictionary<Guid, bool> OriginalRead { get; } = originalRead;
        public FolderCount? OriginalCount { get; set; } = originalCount;
    }
    private FolderReadOptimism? pendingFolderRead;
    private ContextMenuStrip folderMenu = null!;

    private void InitializeFolderActions()
    {
        folderMenu = new ContextMenuStrip(components!) { ShowImageMargin = false };
        var export = new ToolStripMenuItem("Export folder...");
        var markRead = new ToolStripMenuItem("Mark all as read");
        FolderEntry? target = null;
        folderMenu.Items.AddRange([export, markRead]);
        folders.ContextMenuStrip = folderMenu;
        folders.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                folders.SelectedIndex = folders.IndexFromPoint(e.Location);
        };
        folderMenu.Opening += (_, e) =>
        {
            target = folders.SelectedItem as FolderEntry;
            if (target?.Folder is null)
            {
                e.Cancel = true;
                return;
            }
            bool available = target.Account.Mailbox is not null && !NetworkUnavailable() &&
                !updatingMessageFolders && exportCancellation is null;
            export.Enabled = markRead.Enabled = available;
        };
        export.Click += async (_, _) =>
        {
            if (target is { } entry)
                await ExportMessagesAsync(selectionOnly: false, targetFolder: entry);
        };
        markRead.Click += async (_, _) =>
        {
            if (target is { } entry)
                await MarkFolderReadAsync(entry);
        };
    }

    private async Task MarkFolderReadAsync(FolderEntry entry)
    {
        if (updatingMessageFolders || exportCancellation is not null ||
            entry is not { Account.Mailbox: { } mailbox, Folder: { } folder })
            return;
        markingFolderRead = true;
        string key = entry.Account.ConnectionKey();
        var token = mailboxLifetime.Token;
        if (SelectedFolder == entry)
            folderLoad?.Cancel();
        CancelBackgroundCache(entry.Account);
        var originals = SelectedFolder == entry
            ? messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!)
                .ToDictionary(mail => mail.Id, mail => mail.Read)
            : new Dictionary<Guid, bool>();
        var optimistic = pendingFolderRead = new FolderReadOptimism(entry, mailbox, originals,
            entry.Account.Counts.GetValueOrDefault(folder));
        var precedingReads = flagUpdates.Where(pair => ReferenceEquals(pair.Key.Mailbox, mailbox) && pair.Key.ReadFlag)
            .ToArray();
        bool confirmed = false;
        UpdateVisibleReadFlags(originals.Keys.ToDictionary(id => id, _ => true));
        if (optimistic.OriginalCount is { } originalCount)
        {
            entry.Account.Counts = new Dictionary<string, FolderCount>(entry.Account.Counts)
                { [folder] = originalCount with { Unread = 0 } };
            folders.Invalidate();
        }
        ShowBatchStatus("Marking all messages as read...", inProgress: true);
        try
        {
            // Finish earlier checkbox changes before applying the folder-wide update.
            var pending = flagUpdates.Where(pair => ReferenceEquals(pair.Key.Mailbox, mailbox))
                .Select(pair => pair.Value.Save).ToArray();
            var counts = folderCountRefreshes.Where(pair => pair.Key.Item1 == entry.Account &&
                ReferenceEquals(pair.Key.Item2, mailbox)).Select(pair => pair.Value).ToArray();
            try { await Task.WhenAll(pending); }
            catch (Exception) { } // Individual flag changes report their own errors.
            foreach (var preceding in precedingReads)
                if (originals.ContainsKey(preceding.Key.Id))
                    originals[preceding.Key.Id] = preceding.Value.Baseline.ConfirmedValue;
            await Task.WhenAll(counts);
            token.ThrowIfCancellationRequested();
            if (!accounts.Contains(entry.Account) || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;
            var changed = await Task.Run(() => mailbox.MarkAllReadAsync(folder, token), token);
            confirmed = true;
            pendingFolderRead = null;
            token.ThrowIfCancellationRequested();
            string status = "All messages marked as read.";
            if (database is { } store)
            {
                try { await Task.Run(() => store.MarkCachedMessagesRead(entry.Account.Id, folder, key, changed), token); }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    status = "Messages marked as read. The local cache could not be updated.";
                }
            }
            if (IsDisposed || !accounts.Contains(entry.Account) || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;
            if (SelectedFolder == entry)
            {
                UpdateVisibleReadFlags(changed.ToDictionary(id => id, _ => true));
            }
            await RefreshFolderCounts(entry.Account, mailbox, true, token);
            ShowBatchStatus(status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                if (!confirmed)
                    RollBackOptimisticRead();
                ShowBatchStatus(MailOperationError(error, "Could not mark all messages as read."));
                if (SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox))
                    await ShowFolder(SelectedMessage, backgroundRefresh: true, refreshCounts: true);
            }
        }
        finally
        {
            if (ReferenceEquals(pendingFolderRead, optimistic))
                RollBackOptimisticRead();
            markingFolderRead = false;
            if (!IsDisposed)
                QueueBackgroundCache();
        }

        void UpdateVisibleReadFlags(IReadOnlyDictionary<Guid, bool> values)
        {
            if (IsDisposed || SelectedFolder != entry || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;
            if (folderLoadFlagOverrides is { } overrides)
                foreach (var (id, read) in values)
                    overrides[(id, true)] = read;
            var rows = messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!)
                .Select(mail => values.TryGetValue(mail.Id, out bool read) ? mail with { Read = read } : mail).ToArray();
            RenderFolder(rows, SelectedMessage, preserveScrollAnchor: true);
        }

        void RollBackOptimisticRead()
        {
            pendingFolderRead = null;
            if (IsDisposed || !accounts.Contains(entry.Account) || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;
            UpdateVisibleReadFlags(originals);
            if (optimistic.OriginalCount is { } count)
            {
                entry.Account.Counts = new Dictionary<string, FolderCount>(entry.Account.Counts) { [folder] = count };
                folders.Invalidate();
            }
        }
    }
}
