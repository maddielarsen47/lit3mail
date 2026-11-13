namespace Lit3Mail;

public partial class MailForm
{
    private const string MessageMoveFormat = "Lit3Mail.MessageMove";
    private readonly Button moveMail = new() { Text = "Move...", AutoSize = true, FlatStyle = FlatStyle.Flat, Enabled = false };
    private bool updatingMessageFolders;
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
        if (e.Button != MouseButtons.Left || updatingMessageFolders ||
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
        if (SelectedFolder != drag.Source || updatingMessageFolders)
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
            (e.AllowedEffect & DragDropEffects.Move) == 0 || SelectedFolder != drag.Source ||
            updatingMessageFolders)
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
        if (updatingMessageFolders || showingCachedFolder || SelectedFolder is not { } source ||
            SelectedMessage is not { } message)
            return;

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
                if (SelectedFolder == source && SelectedMessage?.Id == message.Id)
                    await ChangeMessageFolderAsync(source, [message.Id], target.Folder, deleting: false);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
        {
            menu.Renderer = exportMenu.Renderer;
            menu.Show(moveMail, new Point(0, moveMail.Height));
        }
    }
}
