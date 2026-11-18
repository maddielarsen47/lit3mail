using MimeKit;

namespace Lit3Mail;

public partial class MailForm
{
    private readonly Button openEml = new() { Text = "Open EML...", AutoSize = true, Dock = DockStyle.Bottom };
    private MailSummary? openedEml;
    private string? openedEmlPath;
    private ToolTip? selectionPathTip;
    private MailSummary? PreviewMessage => openedEml ?? SelectedMessage;
    private int emlOpenVersion;

    private void UpdateMessageSelectionCount()
    {
        bool filePreview = openedEml is not null && messages.SelectedRows.Count == 0;
        messageSelectionCount.Text = filePreview ? openedEmlPath : $"{messages.SelectedRows.Count:N0} selected";
        selectionPathTip?.SetToolTip(messageSelectionCount, filePreview ? openedEmlPath : null);
    }

    private async Task PickEmlAsync()
    {
        using var picker = new OpenFileDialog
        {
            Filter = "Email message (*.eml)|*.eml", CheckFileExists = true, RestoreDirectory = true
        };
        if (picker.ShowDialog(ComposeDialogOwner) == DialogResult.OK)
            await OpenEmlAsync(picker.FileName);
    }

    public async Task OpenEmlAsync(string path)
    {
        int version = ++emlOpenVersion;
        try
        {
            path = Path.GetFullPath(path);
            var token = mailboxLifetime.Token;
            var loaded = await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous);
                if (stream.Length > ComposeAttachmentLimits.MaxDraftMessageBytes)
                    throw new IOException("The email exceeds the 80 MiB file limit.");
                using var message = await MimeMessage.LoadAsync(stream, token);
                var content = await ServerMailbox.ReadMessageContentAsync(message, token);
                var summary = new MailSummary(Guid.NewGuid(), "", true, false, message.Attachments.Any(),
                    message.Subject ?? "(No subject)", "", message.Date.LocalDateTime,
                    message.From.ToString(), message.To.ToString(), message.Cc.ToString(), message.ReplyTo.ToString());
                return (Summary: summary, Content: content);
            }, token);
            if (IsDisposed || token.IsCancellationRequested || version != emlOpenVersion)
                return;

            folderLoad?.Cancel();
            previewLoad?.Cancel();
            ++previewRequest;
            ResetPreviewAttachments();
            htmlPreview?.ClearDisplay();
            previewContent = null;
            previewRemoteContentFailed = false;
            imagePermission = null;
            rebuildingRows = true;
            try
            {
                messages.CurrentCell = null;
                messages.ClearSelection();
                selectionMade = false;
                selectedMessageId = null;
            }
            finally { rebuildingRows = false; }
            openedEml = loaded.Summary;
            openedEmlPath = path;
            UpdateMessageSelectionCount();
            settingsPage.Hide();
            mailLayout.Show();
            previewSubject.Text = openedEml.Subject;
            previewDetails.Text = $"From: {openedEml.FromAddress}  ·  To: {openedEml.ToAddresses}" +
                (openedEml.CcAddresses.Length > 0 ? $"  ·  Cc: {openedEml.CcAddresses}" : "") +
                $"  ·  {openedEml.Date.ToString(DateFormat)}  ·  {Path.GetFileName(path)}" +
                (openedEml.Attachments ? "  ·  Has attachments" : "");
            editDraft.Hide();
            moveMail.Enabled = deleteMail.Enabled = false;
            preview.Show();
            DisplayMessageBody(openedEml, loaded.Content);
        }
        catch (OperationCanceledException) when (mailboxLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            if (!IsDisposed && version == emlOpenVersion)
                ThemedPopup.Show(this, $"Could not open the email file. {error.Message}", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}