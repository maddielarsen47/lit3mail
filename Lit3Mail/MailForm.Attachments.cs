using System.Diagnostics;

namespace Lit3Mail;

public partial class MailForm
{
    private readonly List<MailFileAttachment> composeFiles = [];
    private readonly List<string> openedAttachmentFiles = [];
    private CancellationTokenSource? previewAttachmentLoad;

    private readonly Button attachFiles = new() { Text = "Attach Files…", AutoSize = true };
    private readonly Button removeAttachment = new() { Text = "Remove", AutoSize = true };
    private readonly ComboBox composeAttachmentPicker = new()
    {
        AccessibleName = "Files attached to the draft", DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat, Width = 225, DisplayMember = nameof(MailFileAttachment.FileName)
    };
    private readonly FlowLayoutPanel composeAttachmentPanel = new()
    {
        AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Visible = false
    };

    private readonly Button openAttachment = new() { Text = "Open", AutoSize = true };
    private readonly Button saveAttachment = new() { Text = "Save As…", AutoSize = true };
    private readonly ComboBox previewAttachmentPicker = new()
    {
        AccessibleName = "Received attachments", DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat, Width = 225, DisplayMember = nameof(MailAttachmentInfo.FileName)
    };
    private readonly Label previewAttachmentStatus = new() { AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel previewAttachmentPanel = new()
    {
        AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 4), Visible = false
    };

    private void InitializeAttachmentControls()
    {
        var composeAttachmentLabel = AttachmentLabel();
        composeAttachmentLabel.Anchor = composeAttachmentPicker.Anchor = removeAttachment.Anchor = AnchorStyles.Left;
        composeAttachmentLabel.Margin = new Padding(0, 0, 8, 0);
        composeAttachmentPicker.Margin = new Padding(0, 0, 8, 0);
        removeAttachment.Margin = Padding.Empty;
        composeAttachmentPanel.Controls.Add(composeAttachmentLabel);
        composeAttachmentPanel.Controls.Add(composeAttachmentPicker);
        composeAttachmentPanel.Controls.Add(removeAttachment);
        previewAttachmentPanel.Controls.Add(AttachmentLabel());
        previewAttachmentPanel.Controls.Add(previewAttachmentPicker);
        previewAttachmentPanel.Controls.Add(openAttachment);
        previewAttachmentPanel.Controls.Add(saveAttachment);
        previewAttachmentPanel.Controls.Add(previewAttachmentStatus);

        attachFiles.Click += async (_, _) => await ChooseComposeFilesAsync();
        removeAttachment.Click += (_, _) =>
        {
            int index = composeAttachmentPicker.SelectedIndex;
            if (index < 0)
                return;
            composeFiles.RemoveAt(index);
            RefreshComposeAttachments(index);
        };
        openAttachment.Click += async (_, _) => await SaveReceivedAttachmentAsync(open: true);
        saveAttachment.Click += async (_, _) => await SaveReceivedAttachmentAsync(open: false);
        Disposed += (_, _) =>
        {
            foreach (string file in openedAttachmentFiles)
                DeleteOpenedAttachment(file);
        };

        static Label AttachmentLabel() => new() { Text = "Attachments", AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
    }

    private async Task ChooseComposeFilesAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Attach files", Multiselect = true, CheckFileExists = true, RestoreDirectory = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        composePage.Enabled = false;
        var failed = new List<string>();
        var oversized = new List<string>();
        int totalBytes = composeFiles.Sum(file => file.Bytes.Length);
        var token = mailboxLifetime.Token;
        try
        {
            foreach (string path in picker.FileNames)
            {
                string name = Path.GetFileName(path);
                try
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    long length = input.Length;
                    if (length > ComposeAttachmentLimits.RemainingFileBytes(totalBytes))
                    {
                        oversized.Add(name);
                        continue;
                    }
                    byte[] bytes = new byte[(int)length];
                    await input.ReadExactlyAsync(bytes, token);
                    totalBytes += bytes.Length;
                    composeFiles.Add(new(name, MimeKit.MimeTypes.GetMimeType(name), bytes));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failed.Add(name);
                }
            }
            if (!IsDisposed)
            {
                RefreshComposeAttachments(composeFiles.Count - 1);
                string readError = failed.Count == 0 ? "" : $"Could not attach {string.Join(", ", failed)}.";
                string sizeError = oversized.Count == 0 ? "" :
                    $"{ComposeAttachmentLimits.Error} Skipped {string.Join(", ", oversized)}.";
                composeStatus.Text = $"{readError} {sizeError}".Trim();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!IsDisposed)
                composePage.Enabled = true;
        }
    }

    private async Task IncludeOriginalAttachmentsAsync(IMailbox mailbox, Guid messageId,
        IReadOnlyList<MailAttachmentInfo>? knownAttachments)
    {
        composePage.Enabled = false;
        composeStatus.Text = "Loading original attachments…";
        var token = mailboxLifetime.Token;
        var failed = new List<string>();
        var oversized = new List<string>();
        int totalBytes = composeFiles.Sum(file => file.Bytes.Length);
        try
        {
            var attachments = knownAttachments ?? await mailbox.GetAttachmentsAsync(messageId, token);
            foreach (var attachment in attachments)
            {
                string name = SafeAttachmentName(attachment.FileName);
                try
                {
                    using var output = new ComposeAttachmentLimits.DecodeBuffer(
                        ComposeAttachmentLimits.RemainingFileBytes(totalBytes));
                    await mailbox.DownloadAttachmentAsync(messageId, attachment.PartId, output, token);
                    token.ThrowIfCancellationRequested();
                    byte[] bytes = output.ToArray();
                    composeFiles.Add(new(name, attachment.MediaType, bytes));
                    totalBytes += bytes.Length;
                }
                catch (DraftSizeException)
                {
                    oversized.Add(name);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failed.Add(name);
                }
            }

            if (!IsDisposed)
            {
                string downloadError = failed.Count == 0 ? "" : $"Could not attach {string.Join(", ", failed)}.";
                string sizeError = oversized.Count == 0 ? "" :
                    $"{ComposeAttachmentLimits.Error} Skipped {string.Join(", ", oversized)}.";
                composeStatus.Text = $"{downloadError} {sizeError}".Trim();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed)
                composeStatus.Text = MailOperationError(error, "Could not load original attachments.");
        }
        finally
        {
            if (!IsDisposed)
            {
                RefreshComposeAttachments(composeFiles.Count - 1);
                composePage.Enabled = true;
            }
        }
    }

    private void SetComposeAttachments(IReadOnlyList<MailFileAttachment>? files)
    {
        composeFiles.Clear();
        if (files is not null)
            composeFiles.AddRange(files);
        RefreshComposeAttachments(0);
    }

    private void RefreshComposeAttachments(int selectedIndex)
    {
        composeAttachmentPicker.Items.Clear();
        composeAttachmentPicker.Items.AddRange(composeFiles.Cast<object>().ToArray());
        composeAttachmentPanel.Visible = composeFiles.Count > 0;
        if (composeFiles.Count > 0)
            composeAttachmentPicker.SelectedIndex = Math.Clamp(selectedIndex, 0, composeFiles.Count - 1);
    }

    private void ResetPreviewAttachments()
    {
        previewAttachmentLoad?.Cancel();
        previewAttachmentPanel.Hide();
        previewAttachmentPicker.Items.Clear();
        previewAttachmentStatus.Hide();
    }

    private async Task LoadPreviewAttachmentsAsync(IMailbox mailbox, Guid messageId, int request)
    {
        using var load = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        previewAttachmentLoad = load;
        try
        {
            var files = await mailbox.GetAttachmentsAsync(messageId, load.Token);
            if (load.IsCancellationRequested || IsDisposed || request != previewRequest)
                return;

            DisplayPreviewAttachments(files);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && request == previewRequest)
            {
                previewAttachmentPicker.Hide();
                openAttachment.Hide();
                saveAttachment.Hide();
                previewAttachmentStatus.Text = MailOperationError(error, "Could not load attachments.");
                previewAttachmentStatus.Show();
                previewAttachmentPanel.Show();
            }
        }
        finally
        {
            if (ReferenceEquals(previewAttachmentLoad, load))
                previewAttachmentLoad = null;
        }
    }

    private void DisplayPreviewAttachments(IReadOnlyList<MailAttachmentInfo> files)
    {
        if (files.Count == 0)
            return;
        previewAttachmentPicker.Items.Clear();
        previewAttachmentPicker.Items.AddRange(files.Cast<object>().ToArray());
        previewAttachmentPicker.SelectedIndex = 0;
        previewAttachmentPicker.Show();
        openAttachment.Show();
        saveAttachment.Show();
        previewAttachmentStatus.Hide();
        previewAttachmentPanel.Show();
    }

    private async Task SaveReceivedAttachmentAsync(bool open)
    {
        if (SelectedFolder?.Account.Mailbox is not { } mailbox || SelectedMessage is not { } message ||
            previewAttachmentPicker.SelectedItem is not MailAttachmentInfo attachment)
            return;

        string name = SafeAttachmentName(attachment.FileName);
        string path;
        if (open)
        {
            string directory = Path.Combine(Path.GetTempPath(), "Lit3Mail", Guid.NewGuid().ToString("N"));
            path = Path.Combine(directory, name);
        }
        else
        {
            using var picker = new SaveFileDialog { FileName = name, Title = "Save attachment", RestoreDirectory = true };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            path = picker.FileName;
        }

        openAttachment.Enabled = saveAttachment.Enabled = false;
        bool opened = false;
        try
        {
            if (open)
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await DownloadAttachmentToFileAsync(mailbox, message.Id, attachment.PartId, path, open, mailboxLifetime.Token);
            if (open && !IsDisposed)
            {
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                openedAttachmentFiles.Add(path);
                opened = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed)
                ThemedPopup.Show(this, MailOperationError(error,
                    open ? "Could not open the attachment." : "Could not save the attachment."), Text);
        }
        finally
        {
            if (open && !opened)
                DeleteOpenedAttachment(path);
            if (!IsDisposed)
                openAttachment.Enabled = saveAttachment.Enabled = true;
        }
    }

    internal static string SafeAttachmentName(string original)
    {
        string name = original.Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        name = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsAsciiDigit(stem[3]))
            name = "_" + name;
        return name.Length == 0 ? "attachment" : name;
    }

    internal static async Task DownloadAttachmentToFileAsync(IMailbox mailbox, Guid id, string partId, string target,
        bool requireInternetZone, CancellationToken token)
    {
        string partial = Path.Combine(Path.GetDirectoryName(target)!, $".{Guid.NewGuid():N}.part");
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
                await mailbox.DownloadAttachmentAsync(id, partId, output, token);

            try
            {
                await File.WriteAllTextAsync(partial + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", token);
            }
            catch (Exception error) when (!requireInternetZone &&
                error is (IOException or UnauthorizedAccessException or NotSupportedException))
            {
                // The destination volume can lack support for alternate streams.
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void DeleteOpenedAttachment(string file)
    {
        try
        {
            File.Delete(file);
            Directory.Delete(Path.GetDirectoryName(file)!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void ApplyAttachmentTheme(Color surface, Color chrome, Color ink, Color border, Color selected)
    {
        foreach (var picker in new[] { composeAttachmentPicker, previewAttachmentPicker })
        {
            picker.BackColor = chrome;
            picker.ForeColor = ink;
        }
        composeAttachmentPanel.ForeColor = previewAttachmentPanel.ForeColor = ink;
        previewAttachmentPanel.BackColor = surface;
        foreach (var button in new[] { attachFiles, removeAttachment, openAttachment, saveAttachment })
            StyleButton(button, chrome, ink, border, selected);
    }
}
