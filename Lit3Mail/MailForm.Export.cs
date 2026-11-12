using System.IO.Compression;

namespace Lit3Mail;

public partial class MailForm
{
    private readonly Button exportMail = new() { Text = "Export…", AutoSize = true, Dock = DockStyle.Bottom };
    private readonly Label batchStatus = new() { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    private readonly Button cancelExport = new() { Text = "Cancel", AutoSize = true };
    private readonly FlowLayoutPanel batchBar = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 2, 12, 2), Visible = false
    };
    private CancellationTokenSource? exportCancellation;
    private object? batchActivity;
    private System.Windows.Forms.Timer batchStatusTimer = null!;
    private ContextMenuStrip exportMenu = null!;

    private void InitializeExport()
    {
        batchStatusTimer = new System.Windows.Forms.Timer(components!) { Interval = 10_000 };
        batchStatusTimer.Tick += (_, _) =>
        {
            batchStatusTimer.Stop();
            batchBar.Hide();
        };
        batchBar.Controls.Add(batchStatus);
        batchBar.Controls.Add(cancelExport);
        cancelExport.Click += (_, _) => exportCancellation?.Cancel();
        exportMenu = new ContextMenuStrip(components!) { ShowImageMargin = false };
        var selection = new ToolStripMenuItem("Export selected messages…");
        var folder = new ToolStripMenuItem("Export entire folder…");
        selection.Click += async (_, _) => await ExportMessagesAsync(selectionOnly: true);
        folder.Click += async (_, _) => await ExportMessagesAsync(selectionOnly: false);
        exportMenu.Items.AddRange([selection, folder]);

        exportMail.Click += (_, _) =>
        {
            bool available = SelectedFolder?.Account.Mailbox is not null && !updatingMessageFolders && exportCancellation is null;
            selection.Enabled = available && !showingCachedFolder && selectionMade && messages.SelectedRows.Count > 0;
            folder.Enabled = available;
            exportMenu.Show(exportMail, new Point(0, exportMail.Height));
        };
    }

    private sealed class ExportMenuColors(Color background, Color border) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => background;
        public override Color MenuBorder => border;
    }

    private sealed class ExportMenuRenderer(Color background, Color ink, Color muted, Color border,
        Color hover, Color hoverText) : ToolStripProfessionalRenderer(new ExportMenuColors(background, border))
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            bool highlighted = e.Item.Enabled && e.Item.Selected;
            using var fill = new SolidBrush(highlighted ? hover : background);
            e.Graphics.FillRectangle(fill, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? muted : e.Item.Selected ? hoverText : ink;
            base.OnRenderItemText(e);
        }
    }

    private async Task ExportMessagesAsync(bool selectionOnly)
    {
        if (updatingMessageFolders || exportCancellation is not null ||
            SelectedFolder is not { Account.Mailbox: { } mailbox, Folder: { } folder } entry)
            return;
        var ids = selectionOnly ? SelectedMessageIds() : null;
        if (selectionOnly && (showingCachedFolder || !selectionMade || ids?.Count == 0))
            return;

        using var picker = new SaveFileDialog
        {
            Filter = "ZIP archive (*.zip)|*.zip", DefaultExt = "zip", AddExtension = true,
            FileName = $"{ExportFileNames.SafeStem(entry.Account.Counts.GetValueOrDefault(folder)?.LabelName ?? folder, "Mail")}.zip", RestoreDirectory = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        string target = picker.FileName;
        string partial = Path.Combine(Path.GetDirectoryName(target)!, $".{Guid.NewGuid():N}.part");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        exportCancellation = cancellation;
        var progress = new Progress<(int Done, int Total)>(state =>
        {
            if (!IsDisposed && ReferenceEquals(exportCancellation, cancellation))
                batchStatus.Text = $"Exporting {state.Done:N0} of {state.Total:N0} messages…";
        });
        ShowBatchStatus("Preparing export…", cancellable: true);
        try
        {
            await Task.Run(async () =>
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await mailbox.ExportAsync(folder, ids, archive, progress, cancellation.Token);
                }

                cancellation.Token.ThrowIfCancellationRequested();
                File.Move(partial, target, overwrite: true);
            }, cancellation.Token);
            if (!IsDisposed)
                ShowBatchStatus("Export complete.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!IsDisposed)
                ShowBatchStatus("Export cancelled.");
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                ShowBatchStatus($"{MailOperationError(error, "Export failed.")} The destination archive was left unchanged.");
        }
        finally
        {
            exportCancellation = null;
            try { File.Delete(partial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private object? BeginBatchActivity(string message)
    {
        if (batchActivity is not null || updatingMessageFolders || exportCancellation is not null)
            return null;

        ShowBatchStatus(message, inProgress: true);
        batchActivity = new object();
        return batchActivity;
    }

    private void EndBatchActivity(object? activity)
    {
        if (activity is null || !ReferenceEquals(batchActivity, activity))
            return;

        batchActivity = null;
        if (!IsDisposed && !Disposing)
            batchBar.Hide();
    }

    private void ShowBatchStatus(string message, bool cancellable = false, bool inProgress = false)
    {
        batchStatusTimer.Stop();
        batchActivity = null;
        batchStatus.Text = message;
        cancelExport.Visible = cancellable;
        batchBar.Show();
        if (!cancellable && !inProgress)
            batchStatusTimer.Start();
    }
}

