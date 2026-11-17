namespace Lit3Mail;

public partial class MailForm
{
    private const string MessageMoveFormat = "Lit3Mail.MessageMove";
    private readonly Button moveMail = new() { Text = "Move...", AutoSize = true, FlatStyle = FlatStyle.Flat, Enabled = false };
    private bool updatingMessageFolders => pendingMoves.Count > 0;
    private readonly Dictionary<(FolderEntry Source, IMailbox Mailbox), HashSet<Guid>> pendingMoves = new();
    private Task messageFolderChange = Task.CompletedTask;
    private MessageDrag? pendingMessageDrag;
    private MessageDrag? activeMessageDrag;
    private FolderEntry? highlightedDropFolder;

    private sealed record MessageDrag(FolderEntry Source, HashSet<Guid> Ids, Point Start);

    private sealed class MessageGrid : DataGridView
    {
        public event MouseEventHandler? BeforeMouseDown;
        public event MouseEventHandler? LeftMouseDrag;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            BeforeMouseDown?.Invoke(this, e);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if ((e.Button & MouseButtons.Left) != 0)
            {
                LeftMouseDrag?.Invoke(this, e);
                return;
            }

            base.OnMouseMove(e);
        }
    }

    private void InitializeMessageMove()
    {
        folders.AllowDrop = true;
        messages.BeforeMouseDown += (_, e) => CaptureMessageDrag(e);
        messages.MouseUp += (_, _) => pendingMessageDrag = null;
        messages.LeftMouseDrag += (_, e) => BeginMessageDrag(e);
        folders.DragEnter += (_, e) => UpdateDropTarget(e);
        folders.DragOver += (_, e) => UpdateDropTarget(e);
        folders.DragLeave += (_, _) => HighlightDropFolder(null);
        folders.DragDrop += async (_, e) =>
        {
            var destination = GetDropTarget(e);
            var drag = activeMessageDrag;
            HighlightDropFolder(null);
            if (destination?.Folder is { } name && drag is not null)
                await ChangeMessageFolderAsync(drag.Source, drag.Ids, name, deleting: false);
        };
        var menu = new ContextMenuStrip(components!) { ShowImageMargin = false };
        moveMail.Click += (_, _) => ShowMoveMenu(menu);
    }

    private HashSet<Guid> SelectedMessageIds() => messages.SelectedRows.Cast<DataGridViewRow>()
        .Select(row => row.Tag).OfType<MailSummary>().Select(mail => mail.Id).ToHashSet();

    private void CaptureMessageDrag(MouseEventArgs e)
    {
        pendingMessageDrag = null;
        if (e.Button != MouseButtons.Left ||
            SelectedFolder is not { Account.Mailbox: not null } source)
            return;

        var hit = messages.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0 || hit.ColumnIndex < 2 || messages.Rows[hit.RowIndex].Tag is not MailSummary mail)
            return;
        var ids = selectionMade && messages.Rows[hit.RowIndex].Selected ? SelectedMessageIds() : [mail.Id];
        pendingMessageDrag = new(source, ids, e.Location);
    }

    private void BeginMessageDrag(MouseEventArgs e)
    {
        if (pendingMessageDrag is not { } drag || e.Button != MouseButtons.Left)
            return;
        var threshold = SystemInformation.DragSize;
        if (Math.Abs(e.X - drag.Start.X) < threshold.Width / 2 && Math.Abs(e.Y - drag.Start.Y) < threshold.Height / 2)
            return;

        pendingMessageDrag = null;
        if (SelectedFolder != drag.Source)
            return;

        messages.ClearSelection();
        foreach (DataGridViewRow row in messages.Rows)
            if (row.Tag is MailSummary mail && drag.Ids.Contains(mail.Id))
                row.Selected = true;
        _ = SelectCurrentMessage();
        activeMessageDrag = drag;
        try
        {
            var data = new DataObject();
            data.SetData(MessageMoveFormat, false, "move");
            messages.DoDragDrop(data, DragDropEffects.Move);
        }
        finally
        {
            activeMessageDrag = null;
            HighlightDropFolder(null);
        }
    }

    private void UpdateDropTarget(DragEventArgs e)
    {
        var destination = GetDropTarget(e);
        e.Effect = destination is null ? DragDropEffects.None : DragDropEffects.Move;
        HighlightDropFolder(destination);
    }

    private FolderEntry? GetDropTarget(DragEventArgs e)
    {
        if (activeMessageDrag is not { } drag || e.Data?.GetDataPresent(MessageMoveFormat, false) != true ||
            (e.AllowedEffect & DragDropEffects.Move) == 0 || SelectedFolder != drag.Source)
            return null;

        int index = folders.IndexFromPoint(folders.PointToClient(new Point(e.X, e.Y)));
        return index >= 0 && folders.Items[index] is FolderEntry destination && CanMoveTo(drag.Source, destination)
            ? destination : null;
    }

    private static bool CanMoveTo(FolderEntry source, FolderEntry target) =>
        source.Account == target.Account && target.Folder is { } folder && folder != source.Folder &&
        (source.Account.Mailbox is not ServerMailbox || source.Account.CountsUpdatedAt == default ||
            source.Account.Counts.ContainsKey(folder));

    private void HighlightDropFolder(FolderEntry? target)
    {
        if (highlightedDropFolder == target)
            return;
        highlightedDropFolder = target;
        folders.Invalidate();
    }

    private void ShowMoveMenu(ContextMenuStrip menu)
    {
        if (SelectedFolder is not { } source || SelectedMessage is null)
            return;

        var ids = SelectedMessageIds();
        foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().ToArray())
        {
            menu.Items.Remove(item);
            item.Dispose();
        }
        foreach (var target in folders.Items.OfType<FolderEntry>().Where(target => CanMoveTo(source, target)))
        {
            var item = new ToolStripMenuItem(target.FolderTitle);
            item.Click += async (_, _) =>
            {
                if (SelectedFolder == source)
                    await ChangeMessageFolderAsync(source, ids, target.Folder, deleting: false);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
        {
            menu.Renderer = exportMenu.Renderer;
            menu.Show(moveMail, new Point(0, moveMail.Height));
        }
    }

    private async Task DeleteSelectedMessages()
    {
        if (!selectionMade ||
            SelectedFolder is not { Account.Mailbox: not null, Folder: { } folder } entry)
            return;

        var ids = SelectedMessageIds();
        if (ids.Count == 0)
            return;

        string subject = ids.Count == 1 ? "this message" : $"these {ids.Count:N0} messages";
        if (ThemedPopup.Show(this,
                folder == "Trash" ? $"Permanently delete {subject}? This cannot be undone." : $"Move {subject} to Trash?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        await ChangeMessageFolderAsync(entry, ids, folder == "Trash" ? null : "Trash", deleting: true);
    }

    private async Task ChangeMessageFolderAsync(FolderEntry entry, HashSet<Guid> ids, string? destination, bool deleting)
    {
        if (SelectedFolder != entry || ids.Count == 0 ||
            entry is not { Account.Mailbox: { } mailbox, Folder: { } folder } ||
            (!deleting && (destination is null || !CanMoveTo(entry, new FolderEntry(entry.Account, destination)))))
            return;

        var moveKey = (entry, mailbox);
        pendingMoves.TryGetValue(moveKey, out var pendingIds);
        var rows = messages.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Tag is MailSummary mail && ids.Contains(mail.Id) && pendingIds?.Contains(mail.Id) != true)
            .ToArray();
        if (rows.Length == 0)
            return;

        var removedRows = rows.Select(row => (MailSummary)row.Tag!).ToArray();
        ids = removedRows.Select(mail => mail.Id).ToHashSet();
        var previousSelection = rows.Where(row => row.Selected).Select(row => ((MailSummary)row.Tag!).Id).ToHashSet();
        string originalQuery = search.Text;
        string? key = database is not null && mailbox is ServerMailbox ? entry.Account.ConnectionKey() : null;
        if (pendingIds is null)
            pendingMoves.Add(moveKey, pendingIds = []);
        pendingIds.UnionWith(ids);
        var previousChange = messageFolderChange;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageFolderChange = completion.Task;
        bool confirmed = false;
        bool pendingCleared = false;
        try
        {
            RemoveVisibleRows(rows);
            _ = ShowPreview();
            await previousChange;
            await exportCompletion;
            if (IsDisposed || mailboxLifetime.IsCancellationRequested || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;

            CancelBackgroundCache(entry.Account);
            var countsBeforeMove = entry.Account.Counts;
            string action = destination is null ? "Deleting" : "Moving";
            ShowMoveStatus($"{action} {ids.Count:N0} {(ids.Count == 1 ? "message" : "messages")}...", inProgress: true);
            var pendingFlags = flagUpdates.Where(pair => ReferenceEquals(pair.Key.Mailbox, mailbox) && ids.Contains(pair.Key.Id))
                .Select(pair => pair.Value.Save).ToArray();
            try { await Task.WhenAll(pendingFlags); }
            catch (Exception) { } // The flag update reports its own failure.
            if (IsDisposed || mailboxLifetime.IsCancellationRequested || !ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;

            var move = deleting
                ? await mailbox.DeleteAsync(folder, ids, mailboxLifetime.Token)
                : await mailbox.MoveAsync(folder, destination!, ids, mailboxLifetime.Token);
            confirmed = true;
            if (IsDisposed)
                return;

            if (ReferenceEquals(entry.Account.Mailbox, mailbox) &&
                ReferenceEquals(entry.Account.Counts, countsBeforeMove) && removedRows.Length == ids.Count)
            {
                int unread = removedRows.Count(mail => !mail.Read);
                AdjustFolderCount(entry.Account, folder, -unread, -ids.Count);
                if (destination is not null)
                    AdjustFolderCount(entry.Account, destination, move.MarkedRead ? 0 : unread, ids.Count);
            }
            entry.Account.CountsUpdatedAt = default;
            string result = destination is null ? "deleted" : deleting ? "moved to Trash" : "moved";
            string completedStatus = $"{ids.Count:N0} {(ids.Count == 1 ? "message" : "messages")} {result}.";
            if (deleting && destination is null)
                ShowMoveStatus(completedStatus);
            bool finishLoad = false;
            CancellationTokenSource? interruptedLoad = null;
            if (SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox))
            {
                finishLoad = showingCachedFolder || (folderLoad is not null && loadedUnfilteredFolder != entry);
                interruptedLoad = folderLoad;
                interruptedLoad?.Cancel();
            }
            var movedMessages = new List<MailSummary>();
            if (destination is not null && move.DestinationIds is { } destinationIds)
            {
                foreach (var mail in removedRows)
                    if (destinationIds.TryGetValue(mail.Id, out var id))
                        movedMessages.Add(mail with { Id = id, Folder = destination, Read = move.MarkedRead || mail.Read });
            }
            if (key is not null)
            {
                try
                {
                    await Task.Run(() => database!.RemoveMessageCaches(entry.Account.Id, folder, key, ids, destination,
                        movedMessages, move.DestinationIds));
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // The server operation has already succeeded.
                }
            }

            if (IsDisposed)
                return;
            if (!ReferenceEquals(entry.Account.Mailbox, mailbox))
                return;

            if (SelectedFolder == entry && folderLoad is { } currentLoad &&
                !ReferenceEquals(currentLoad, interruptedLoad))
            {
                interruptedLoad = currentLoad;
                currentLoad.Cancel();
                finishLoad = true;
            }
            ClearPendingIds();
            completion.TrySetResult();
            if (destination is not null)
            {
                ShowMoveStatus(completedStatus);
                var destinationEntry = new FolderEntry(entry.Account, destination);
                QueueBackgroundCache(destinationEntry);
                if (SelectedFolder == destinationEntry)
                {
                    if (movedMessages.Count > 0 && string.IsNullOrWhiteSpace(search.Text))
                    {
                        var existing = messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!);
                        RenderFolder(existing.Concat(movedMessages).DistinctBy(mail => mail.Id).ToArray(), SelectedMessage);
                    }
                    _ = ShowFolder(SelectedMessage, refreshCounts: true, backgroundRefresh: true);
                    return;
                }
            }

            if (finishLoad && SelectedFolder == entry && (folderLoad is null || ReferenceEquals(folderLoad, interruptedLoad)))
                _ = ShowFolder(SelectedMessage, refreshCounts: true);
            else
                _ = RefreshFolderCounts(entry.Account, mailbox, true, mailboxLifetime.Token);
        }
        catch (OperationCanceledException) when (mailboxLifetime.IsCancellationRequested) { }
        catch (Exception error) when (!confirmed)
        {
            ClearPendingIds();
            if (!IsDisposed)
            {
                bool sourceIsOpen = SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox);
                if (sourceIsOpen && search.Text == originalQuery)
                {
                    folderLoad?.Cancel();
                    var present = messages.Rows.Cast<DataGridViewRow>().Select(row => ((MailSummary)row.Tag!).Id).ToHashSet();
                    bool restoreSelection = !selectionMade;
                    rebuildingRows = true;
                    try
                    {
                        foreach (var mail in removedRows)
                        {
                            if (!present.Add(mail.Id))
                                continue;
                            var row = messages.Rows[AddMessageRow(mail)];
                            if (restoreSelection && previousSelection.Contains(mail.Id))
                                row.Selected = true;
                        }
                        selectionMade = messages.SelectedRows.Count > 0;
                        selectedMessageId = SelectedMessage?.Id;
                    }
                    finally
                    {
                        rebuildingRows = false;
                    }
                    SortPublishedRows();
                    _ = ShowPreview();
                }
                ShowMoveStatus(deleting ? "Delete could not be confirmed." : "Move could not be confirmed.");
                ThemedPopup.Show(this, error is InvalidOperationException ? error.Message :
                    MailOperationError(error, deleting ? "Could not delete the selected messages." : "Could not move the selected messages."), Text);
                if (sourceIsOpen && SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox))
                    _ = ShowFolder(SelectedMessage, refreshCounts: true, backgroundRefresh: true);
            }
        }
        finally
        {
            ClearPendingIds();
            completion.TrySetResult();
        }

        void ClearPendingIds()
        {
            if (pendingCleared)
                return;
            pendingCleared = true;
            pendingIds.ExceptWith(ids);
            if (pendingIds.Count == 0)
                pendingMoves.Remove(moveKey);
        }

        void ShowMoveStatus(string message, bool inProgress = false)
        {
            if (exportCancellation is null && ReferenceEquals(messageFolderChange, completion.Task))
                ShowBatchStatus(message, inProgress: inProgress);
        }

        void RemoveVisibleRows(DataGridViewRow[] rows)
        {
            if (rows.Length == 0)
                return;
            var selected = selectionMade ? messages.SelectedRows.Cast<DataGridViewRow>().ToArray() : [];
            int topIndex = messages.FirstDisplayedScrollingRowIndex;
            var top = topIndex >= 0 ? messages.Rows[topIndex] : null;
            var current = messages.CurrentCell;
            rebuildingRows = true;
            try
            {
                foreach (var row in rows.Reverse())
                    messages.Rows.Remove(row);
                messages.CurrentCell = current?.OwningRow?.DataGridView == messages ? current : null;
                messages.ClearSelection();
                foreach (var row in selected.Where(row => row.DataGridView == messages))
                    row.Selected = true;
                selectionMade = messages.SelectedRows.Count > 0;
                selectedMessageId = SelectedMessage?.Id;
                if (messages.Rows.Count > 0 && topIndex >= 0)
                    messages.FirstDisplayedScrollingRowIndex = top?.DataGridView == messages
                        ? top.Index : Math.Min(topIndex, messages.Rows.Count - 1);
            }
            finally
            {
                rebuildingRows = false;
            }
        }
    }
}
