namespace Lit3Mail;

public partial class MailForm
{
    private readonly Panel composePage = new() { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
    private RecipientAutocomplete? recipientAutocomplete;
    private readonly WrappingAddressSelector composeFrom = new()
    {
        AccessibleName = "From", Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat
    };
    private readonly TextBox composeTo = new() { AccessibleName = "To", Dock = DockStyle.Fill };
    private readonly TextBox composeCc = new() { AccessibleName = "Cc", Dock = DockStyle.Fill };
    private readonly TextBox composeBcc = new() { AccessibleName = "Bcc", Dock = DockStyle.Fill };
    private readonly TextBox composeReplyTo = new() { AccessibleName = "Reply-To", Dock = DockStyle.Fill };
    private readonly TextBox composeSubject = new() { AccessibleName = "Subject", Dock = DockStyle.Fill };
    private readonly CheckBox composeAddressDetails = new()
    {
        Text = "More", Appearance = Appearance.Button,
        AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 3, 3)
    };
    private sealed record SenderOption(MailAccount Account)
    {
        public override string ToString() => Account.Email.Trim();
    }
    private MailAccount? ComposeSender => (composeFrom.SelectedItem as SenderOption)?.Account;
    private sealed record ComposeDraft(MailAccount? Sender, string To, string Cc, string Bcc, string ReplyTo,
        bool UsesDefaultReplyTo, string Subject, string BodyRtf, ComposeSignatureRange Signature,
        IReadOnlyList<MailFileAttachment> Files, ComposeHtmlQuote? HtmlQuote = null,
        ResponseOrigin? Response = null);
    private sealed record EditingDraft(MailAccount Account, IMailbox Mailbox, Guid Id);
    private readonly MailBodyEditor composeBody = new()
    {
        AccessibleName = "Message", Dock = DockStyle.Fill, Multiline = true, AcceptsTab = false,
        ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false, DetectUrls = false,
        Font = new Font("Segoe UI", 12), ForeColor = Color.Black, BackColor = Color.White, BulletIndent = 16
    };
    private readonly FlowLayoutPanel composeFormatting = new()
    {
        AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, AccessibleName = "Message formatting"
    };
    private const int ComposePaneHeight = 500;
    private const string SavingDraftStatus = "Saving draft...";
    private const string SendingMailStatus = "Sending mail...";
    private readonly Panel composeQuoteHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly RowStyle composeQuoteRow = new(SizeType.Absolute, 0);
    private readonly Label composeQuoteLabel = new() { Text = "Quote", AutoSize = true, Visible = false };
    private readonly List<Button> composeActionCopies = [];
    private readonly TextBox composeQuoteFallback = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "Original message"
    };
    private readonly Button showComposeQuoteImages = new()
    {
        Text = "⚠ Show images", AccessibleName = "Show images in quoted message", AutoSize = true,
        Margin = new Padding(0, 3, 3, 3), FlatStyle = FlatStyle.Flat, Visible = false
    };
    private HtmlMailPreview? composeQuotePreview;
    private Form? composeWindow;
    private Form ComposeDialogOwner => composeWindow is { Visible: true } window ? window : this;
    private readonly Button popOutCompose = new()
    {
        Text = "Pop Out", AutoSize = true, FlatStyle = FlatStyle.Flat,
        Tag = ToolbarIcon.PopOut, Padding = new Padding(22, 0, 0, 0)
    };
    private readonly Button backFromCompose = new()
    {
        Text = "Back to Mail", AutoSize = true, FlatStyle = FlatStyle.Flat,
        Tag = ToolbarIcon.Back, Padding = new Padding(22, 0, 0, 0)
    };
    private readonly Button sendMail = new()
    {
        Text = "Send Mail", AutoSize = true, FlatStyle = FlatStyle.Flat,
        Tag = ToolbarIcon.Send, Padding = new Padding(22, 0, 0, 0)
    };
    private readonly Button saveDraft = new()
    {
        Text = "Save Draft", AutoSize = true, FlatStyle = FlatStyle.Flat,
        Tag = ToolbarIcon.Save, Padding = new Padding(22, 0, 0, 0)
    };
    private readonly Label composeStatus = new()
    {
        AutoSize = true, Dock = DockStyle.Top, Visible = false, Padding = new Padding(0, 0, 0, 8)
    };

    private sealed class ComposeSession
    {
        public ComposeDraft? Baseline;
        public ResponseOrigin? Response;
        public EditingDraft? EditingServerDraft;
        public Guid? EditingLocalDraft;
        public (Guid SenderId, Guid DraftId, string ConnectionKey)? LocalDraftServerLink;
        public bool LoadingDraft;
        public bool OperationPending;
        public string? OperationFailure;
        public ComposeSignatureRange Signature;
        public string PreviousBody = "";
        public bool UpdatingBody;
        public bool ChangingSender;
        public bool ChangingReplyTo;
        public bool UsesDefaultReplyTo = true;
        public ComposeHtmlQuote? HtmlQuote;
        public (string Header, MailContent Message)? QuoteImageSource;
        public bool ShowQuoteImages;
    }

    private readonly ComposeSession composeSession = new();

    private void InitializeCompose(Font headingFont, ToolTip hints)
    {
        InitializeComposePage(headingFont, hints);
        Controls.Add(composePage);

        composeBody.TextChanged += (_, _) =>
        {
            if (composeSession.UpdatingBody)
                return;
            string body = composeBody.Text;
            composeSession.Signature = composeSession.Signature.TrackEdit(composeSession.PreviousBody, body, composeBody.SelectionStart);
            composeSession.PreviousBody = body;
        };
        composeReplyTo.TextChanged += (_, _) =>
        {
            if (!composeSession.ChangingReplyTo)
                composeSession.UsesDefaultReplyTo = false;
        };
        composeFrom.SelectedIndexChanged += (_, _) =>
        {
            if (composeSession.ChangingSender || ComposeSender is not { } sender)
                return;
            sendMail.Enabled = sender.Mailbox is not null;
            ReplaceSignature(sender);
            if (composeSession.UsesDefaultReplyTo)
                SetReplyTo(DefaultReplyTo(sender));
            UpdateRecipientAccount();
        };

        newEmail.Click += (_, _) => OpenNewCompose();
        reply.Click += async (_, _) => await OpenResponseAsync(isReply: true);
        replyAll.Click += async (_, _) => await OpenResponseAsync(isReply: true, includeOtherRecipients: true);
        forward.Click += async (_, _) => await OpenResponseAsync(isReply: false);
        backFromCompose.Click += (_, _) => LeaveCompose();
        popOutCompose.Click += (_, _) => ToggleComposeWindow();
        localDrafts.Click += async (_, _) => await OpenLocalDraftAsync();

        editDraft.Click += async (_, _) => await OpenServerDraftAsync();
        saveDraft.Click += async (_, _) => await SaveComposeDraftAsync();

        sendMail.Click += async (_, _) => await SendComposeAsync();
    }

    private void PopulateSenders(MailAccount? preferred)
    {
        composeSession.ChangingSender = true;
        composeFrom.BeginUpdate();
        try
        {
            composeFrom.Items.Clear();
            foreach (var account in accounts.Where(account => !string.IsNullOrWhiteSpace(account.Email)))
                composeFrom.Items.Add(new SenderOption(account));
            if (composeFrom.Items.Count > 0)
            {
                int index = preferred is null ? -1 : composeFrom.Items.IndexOf(new SenderOption(preferred));
                composeFrom.SelectedIndex = Math.Max(0, index);
            }
        }
        finally
        {
            composeFrom.EndUpdate();
            composeSession.ChangingSender = false;
        }
    }

    private static string DefaultReplyTo(MailAccount account) =>
        string.IsNullOrWhiteSpace(account.ReplyTo) ? account.Email.Trim() : account.ReplyTo.Trim();

    private void SetReplyTo(string value)
    {
        composeSession.ChangingReplyTo = true;
        composeReplyTo.Text = value;
        composeSession.ChangingReplyTo = false;
    }

    private void UpdateRecipientAccount()
    {
        var account = ComposeSender;
        recipientAutocomplete?.SetAccount(account?.Id, account?.ConnectionKey());
    }

    private void OpenCompose(Control focus, bool resetBaseline = true)
    {
        composeStatus.Text = "";
        sendMail.Enabled = ComposeSender?.Mailbox is not null;
        saveDraft.Enabled = database is not null || sendMail.Enabled;
        if (composeWindow is null)
            mailLayout.Hide();
        composeAddressDetails.Checked = false;
        if (resetBaseline)
            composeSession.Baseline = CaptureDraft();
        composePage.Show();
        if (composeWindow is { Visible: false } window)
            window.Show();
        DisplayComposeQuote();
        UpdateRecipientAccount();
        focus.Focus();
    }

    private bool FocusComposeWindow()
    {
        if (composeWindow is not { Visible: true } window)
            return false;
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Activate();
        return true;
    }

    private void ToggleComposeWindow()
    {
        if (composeWindow is not null)
        {
            if (settingsPage.Visible)
                return;
            mailLayout.Hide();
            RestoreComposeToMainWindow();
            composeBody.Focus();
            return;
        }

        var window = new Form
        {
            Text = "New Mail", Font = Font, AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterScreen, MinimumSize = MinimumSize,
            Size = new Size(Math.Max(720, Math.Min(Width, 860)), Math.Max(520, Math.Min(Height, 800))),
            Icon = Icon, TopMost = true
        };
        window.Activated += async (_, _) =>
        {
            OnMailWindowActivated();
            await DismissMailRemindersAsync();
        };
        window.Deactivate += (_, _) => CheckMailWindowFocus();
        window.FormClosing += (_, args) =>
        {
            if (args.CloseReason != CloseReason.UserClosing)
                return;
            args.Cancel = true;
            this.PostIfHandleAvailable(() =>
            {
                if (ReferenceEquals(composeWindow, window))
                    LeaveCompose();
            });
        };
        composeWindow = window;
        window.Controls.Add(composePage);
        popOutCompose.Text = "Dock";
        mailLayout.Show();
        window.Show();
        composeBody.Focus();
    }

    private void RestoreComposeToMainWindow()
    {
        if (composeWindow is not { } window)
            return;
        composeWindow = null;
        Controls.Add(composePage);
        composePage.BringToFront();
        popOutCompose.Text = "Pop Out";
        window.Dispose();
    }

    private ComposeDraft CaptureDraft()
    {
        return new(ComposeSender, composeTo.Text, composeCc.Text, composeBcc.Text, composeReplyTo.Text,
            composeSession.UsesDefaultReplyTo, composeSubject.Text, composeBody.Rtf ?? "", composeSession.Signature, composeFiles.ToArray(),
            composeSession.HtmlQuote, composeSession.Response);
    }

    private static bool SameDraft(ComposeDraft draft, ComposeDraft baseline) =>
        (draft with { Files = baseline.Files }) == baseline && draft.Files.SequenceEqual(baseline.Files);

    private bool HasUnsavedDrafts() =>
        composeSession.Baseline is { } baseline && !SameDraft(CaptureDraft(), baseline);

    private bool ConfirmDiscardDrafts() => ThemedPopup.Show(ComposeDialogOwner,
        "Discard unsaved draft changes?", "Unsaved drafts", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void RestoreDraft(ComposeDraft draft)
    {
        PopulateSenders(draft.Sender ?? SelectedFolder?.Account);
        (composeTo.Text, composeCc.Text, composeBcc.Text, composeSubject.Text) =
            (draft.To, draft.Cc, draft.Bcc, draft.Subject);
        SetComposeAttachments(draft.Files);
        composeSession.UsesDefaultReplyTo = draft.UsesDefaultReplyTo;
        SetReplyTo(composeSession.UsesDefaultReplyTo && ComposeSender is { } sender ? DefaultReplyTo(sender) : draft.ReplyTo);
        composeSession.UpdatingBody = true;
        try
        {
            composeBody.Rtf = draft.BodyRtf;
            composeSession.HtmlQuote = draft.HtmlQuote;
            composeSession.Response = draft.Response;
            composeSession.Signature = draft.Signature;
            composeSession.PreviousBody = composeBody.Text;
        }
        finally
        {
            composeSession.UpdatingBody = false;
        }
        RemoveLegacyQuotedBcc();
        if (ComposeSender is { } replacement && !ReferenceEquals(replacement, draft.Sender))
            ReplaceSignature(replacement);
    }

    private void CloseCompose()
    {
        bool wasVisible = composePage.Visible;
        recipientAutocomplete?.SetAccount(null, null);
        composePage.Hide();
        RestoreComposeToMainWindow();
        composeQuotePreview?.ClearDisplay();
        composeQuoteFallback.Clear();
        SetComposeQuoteVisible(false);
        composeSession.HtmlQuote = null;
        composeSession.QuoteImageSource = null;
        composeSession.ShowQuoteImages = false;
        composeSession.Response = null;
        composeSession.Baseline = null;
        composeSession.EditingServerDraft = null;
        composeSession.EditingLocalDraft = null;
        composeSession.LocalDraftServerLink = null;
        if (!settingsPage.Visible)
        {
            mailLayout.Show();
            if (wasVisible)
                newEmail.Focus();
        }
    }

    private void ReplaceSignature(MailAccount sender)
    {
        var oldRange = composeSession.Signature;
        int selectionStart = composeBody.SelectionStart;
        int selectionEnd = selectionStart + composeBody.SelectionLength;
        int originalLength = composeBody.TextLength;
        composeSession.UpdatingBody = true;
        try
        {
            composeBody.Select(oldRange.Start, oldRange.Length);
            composeBody.SelectedText = "";
            if (!string.IsNullOrWhiteSpace(sender.Signature))
            {
                composeBody.SelectedText = "\r\n\r\n";
                composeBody.InsertHtmlOrText(sender.Signature);
            }

            composeSession.Signature = new(oldRange.Start, composeBody.TextLength - originalLength + oldRange.Length);
            composeSession.PreviousBody = composeBody.Text;
            int start = AdjustPosition(selectionStart);
            composeBody.Select(start, AdjustPosition(selectionEnd) - start);
        }
        finally
        {
            composeSession.UpdatingBody = false;
        }

        int AdjustPosition(int position) => position <= oldRange.Start ? position :
            position >= oldRange.End ? position + composeSession.Signature.Length - oldRange.Length : composeSession.Signature.End;
    }

    private void SetComposeBody(MailAccount account, string? quoteHeader = null, MailContent? quotedMessage = null,
        bool includeQuotedImages = false)
    {
        composeSession.Response = null;
        SetComposeAttachments(null);
        PopulateSenders(account);
        var sender = ComposeSender ?? account;
        composeSession.UsesDefaultReplyTo = true;
        SetReplyTo(DefaultReplyTo(sender));
        composeSession.UpdatingBody = true;
        try
        {
            composeSession.HtmlQuote = quoteHeader is not null && quotedMessage?.Html is { Length: > 0 }
                ? ComposeHtmlQuote.Create(quoteHeader, quotedMessage,
                    includeQuotedImages, includeQuotedImages ? source => htmlPreview?.GetInlineQuoteImage(source) : null)
                : null;
            composeSession.ShowQuoteImages = includeQuotedImages;
            composeSession.QuoteImageSource = composeSession.HtmlQuote is not null && quoteHeader is not null && quotedMessage is not null
                ? (quoteHeader, quotedMessage)
                : null;
            composeBody.SetPlainText(quoteHeader is null || composeSession.HtmlQuote is not null ? "" : "\r\n\r\n" + quoteHeader);
            if (quotedMessage is not null && composeSession.HtmlQuote is null)
            {
                composeBody.Select(composeBody.TextLength, 0);
                composeBody.AppendPlainText(quotedMessage.Body);
            }
            composeSession.Signature = default;
            composeSession.PreviousBody = composeBody.Text;
        }
        finally
        {
            composeSession.UpdatingBody = false;
        }
        ReplaceSignature(sender);
        composeBody.Select(0, 0);
    }

    private void FinishCompose()
    {
        composeTo.Clear();
        composeCc.Clear();
        composeBcc.Clear();
        SetReplyTo("");
        composeSubject.Clear();
        composeBody.Clear();
        SetComposeAttachments(null);
        CloseCompose();
    }

    private void RestoreServerDraft(MailAccount account, OutgoingMail mail)
    {
        PopulateSenders(account);
        (composeTo.Text, composeCc.Text, composeBcc.Text, composeSubject.Text) =
            (mail.To, mail.Cc, mail.Bcc, mail.Subject);
        SetComposeAttachments(mail.Files);
        composeSession.UsesDefaultReplyTo = mail.ReplyTo == DefaultReplyTo(account);
        SetReplyTo(mail.ReplyTo);
        composeSession.UpdatingBody = true;
        try
        {
            composeSession.Response = null;
            var body = new MailContent(mail.Body, Html: mail.Html, InlineImages: mail.InlineImages);
            composeSession.HtmlQuote = ComposeHtmlQuote.RestoreDraftQuote(body, out var editableBody);
            string? html = editableBody.HtmlForPreview();
            if (string.IsNullOrEmpty(html))
                composeBody.SetPlainText(editableBody.Body);
            else
            {
                composeBody.SetPlainText("");
                composeBody.InsertHtmlOrText(html);
            }
            composeSession.Signature = new(composeBody.TextLength, 0);
            composeSession.PreviousBody = composeBody.Text;
        }
        finally
        {
            composeSession.UpdatingBody = false;
        }
        RemoveLegacyQuotedBcc();
    }

    private void RemoveLegacyQuotedBcc()
    {
        if (composeSession.HtmlQuote is not null || ComposeHtmlQuote.FindQuotedBcc(composeBody.Text) is not { } bcc)
            return;
        composeBody.Select(bcc.Index, bcc.Length);
        composeBody.SelectedText = "";
        composeBody.Select(0, 0);
    }

    private async Task OpenResponseAsync(bool isReply, bool includeOtherRecipients = false)
    {
        if (FocusComposeWindow() || composeSession.OperationPending || SelectedFolder is not { } folder || SelectedMessage is not { } mail ||
            previewContent is not { } content)
            return;

        (composeTo.Text, composeCc.Text) = includeOtherRecipients
            ? ReplyRecipients.ReplyAll(mail, accounts.Select(account => account.Email))
            : (isReply ? ReplyRecipients.ReplyTarget(mail) : "", "");
        composeBcc.Clear();
        string prefix = isReply ? "Re: " : "Fwd: ";
        composeSubject.Text = mail.Subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? mail.Subject : prefix + mail.Subject;
        var quotedContent = content.PreviewTooLarge
            ? content with { Body = "[Original message body omitted because it is too large to preview.]" }
            : content;
        var originalAddresses = new (string Label, string Value)[]
        {
            ("From", mail.FromAddress), ("To", mail.ToAddresses), ("CC", mail.CcAddresses),
            ("Reply-To", mail.ExplicitReplyToAddresses)
        };
        string addressLines = string.Concat(originalAddresses
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => $"{field.Label}: {field.Value}\r\n"));
        SetComposeBody(folder.Account,
            $"{ComposeHtmlQuote.OriginalHeader}\r\n{addressLines}"
            + $"Date: {mail.Date.ToString(DateFormat)}\r\nSubject: {mail.Subject}\r\n\r\n", quotedContent,
            includeQuotedImages: imagePermission == mail.Id);
        if (folder.Folder is { } sourceFolder)
            composeSession.Response = new(folder.Account.Id, folder.Account.ConnectionKey(), sourceFolder, mail.Id, isReply);

        OpenCompose(isReply ? composeBody : composeTo);
        if ((mail.Attachments || content.AttachmentList is { Count: > 0 }) &&
            folder.Account.Mailbox is { } mailbox &&
            ThemedPopup.Show(ComposeDialogOwner, "Include attachments from the original email?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            await IncludeOriginalAttachmentsAsync(mailbox, mail.Id, content.AttachmentList);
        }
    }

    private void LeaveCompose()
    {
        if (composePage.Enabled && (!HasUnsavedDrafts() || ConfirmDiscardDrafts()))
            CloseCompose();
    }

    private async Task<bool> SaveDraftLocallyAsync(ComposeDraft snapshot)
    {
        if (database is null)
            return false;

        var id = composeSession.EditingLocalDraft ?? Guid.NewGuid();
        var editor = new DraftEditorState(snapshot.To, snapshot.Cc, snapshot.Bcc, snapshot.ReplyTo,
            snapshot.Subject, snapshot.UsesDefaultReplyTo, snapshot.BodyRtf, snapshot.Signature, snapshot.HtmlQuote,
            snapshot.Response);
        var sender = snapshot.Sender;
        var link = composeSession.LocalDraftServerLink;
        if (sender is not null && composeSession.EditingServerDraft is { } serverDraft && ReferenceEquals(serverDraft.Account, sender))
            link = (sender.Id, serverDraft.Id, sender.ConnectionKey());
        if (link is { } previous && (sender?.Id != previous.SenderId || sender.ConnectionKey() != previous.ConnectionKey))
            link = null;
        var draft = new LocalDraft(snapshot.Sender?.Id, editor, snapshot.Files.ToArray(),
            link?.DraftId, link?.ConnectionKey);
        try
        {
            await Task.Run(() => database.SaveLocalDraft(id, snapshot.Sender?.Email.Trim() ?? "", draft));
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    private async Task RemoveLocalDraftAfterServerSuccessAsync()
    {
        if (composeSession.EditingLocalDraft is not { } id || database is null)
            return;
        try
        {
            await Task.Run(() => database.DeleteLocalDraft(id));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            if (!IsDisposed)
                ThemedPopup.Show(this, "The server operation succeeded. A copy remains in Local Drafts.", Text);
        }
    }

    private void BeginComposeOperation(string status)
    {
        composeSession.OperationPending = true;
        recipientAutocomplete?.SetAccount(null, null);
        composePage.Hide();
        composeWindow?.Hide();
        composeQuotePreview?.ClearDisplay();
        if (!settingsPage.Visible)
            mailLayout.Show();
        ShowComposeOperationStatus(status, inProgress: true);
    }

    private void ReopenFailedCompose()
    {
        if (composeSession.OperationFailure is not { } failure)
            return;
        composeSession.OperationFailure = null;
        OpenCompose(composeBody, resetBaseline: false);
        composeStatus.Text = failure;
    }

    private bool NewMessageMentionsAttachments()
    {
        string body = composeBody.GetVisibleText();
        if (composeSession.HtmlQuote is null)
        {
            string header = ComposeHtmlQuote.OriginalHeader;
            if (body.StartsWith(header + "\n", StringComparison.Ordinal))
                return false;

            int quoteStart = body.IndexOf("\n" + header + "\n", StringComparison.Ordinal);
            if (quoteStart >= 0)
                body = body[..quoteStart];
        }
        return body.Contains("attach", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendComposeAsync()
    {
        if (composeSession.OperationPending)
            return;

        string recipient = composeTo.Text.Trim();
        string cc = composeCc.Text.Trim();
        string bcc = composeBcc.Text.Trim();
        if (!(recipient + cc + bcc).Any(character => character is not (';' or ',') && !char.IsWhiteSpace(character)))
        {
            composeStatus.Text = "Enter a recipient.";
            composeTo.Focus();
            return;
        }

        if (ComposeSender is not { Mailbox: { } mailbox } sender)
        {
            composeStatus.Text = "Choose a connected sending account.";
            return;
        }
        if (NetworkUnavailable())
        {
            composeStatus.Text = "The computer appears to be offline. Use Save Draft to keep this message on this computer.";
            return;
        }
        string replyTo = composeReplyTo.Text.Trim();
        if (replyTo.Length > 0 && (!MimeKit.MailboxAddress.TryParse(replyTo, out var replyAddress) ||
            !replyAddress.Address.Contains('@')))
        {
            composeStatus.Text = "Enter a valid Reply-To address.";
            composeAddressDetails.Checked = true;
            composeReplyTo.Focus();
            return;
        }
        string subject = composeSubject.Text.Trim();
        string confirmation = "Send this email?";
        if (composeFiles.Count == 0 && NewMessageMentionsAttachments())
            confirmation += "\r\n\r\nDon\'t forget attachments.";
        if (ThemedPopup.Show(ComposeDialogOwner, confirmation, sendMail.Text, MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var sourceDraft = composeSession.EditingServerDraft;
        var originalMessage = composeSession.Response;
        var sentFolder = new FolderEntry(sender, "Sent");
        BeginComposeOperation(SendingMailStatus);
        await Task.Yield();

        try
        {
            if (IsDisposed || Disposing)
                return;

            SentMailResult sent;
            MailContent content;
            try
            {
                string from = sender.Email.Trim();
                content = GetComposeMessageContent();
                sent = await mailbox.SendAsync(new(from, replyTo, recipient, cc,
                    subject.Length == 0 ? "(No subject)" : subject, content.Body, content.Html, content.InlineImages,
                    Bcc: bcc, Files: composeFiles.ToArray()), mailboxLifetime.Token);
            }
            catch (OperationCanceledException) when (mailboxLifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    composeSession.OperationFailure = $"{MailOperationError(error, "Could not confirm delivery.")} " +
                        "Nothing was queued. Save a draft or check Sent before retrying.";
                    ShowComposeOperationStatus(composeSession.OperationFailure, persistent: true);
                }
                return;
            }

            if (IsDisposed)
                return;
            await RemoveLocalDraftAfterServerSuccessAsync();
            if (IsDisposed)
                return;

            if (ReferenceEquals(sender.Mailbox, mailbox))
            {
                CacheRecipientAddresses(sender, recipient, cc, bcc);
                if (sent.Summary is { } summary)
                    CacheSentMessage(sentFolder, mailbox, summary, content);
                sender.CountsUpdatedAt = default;
            }

            FinishCompose();
            ShowComposeOperationStatus(sent.Warning ?? "Mail sent.", persistent: sent.Warning is not null);
            if (originalMessage is not null)
                _ = MarkOriginalResponseAsync(originalMessage);
            if (SelectedFolder == sentFolder && ReferenceEquals(sender.Mailbox, mailbox))
            {
                if (sent.Summary is { } sentSummary && loadedUnfilteredFolder == sentFolder &&
                    string.IsNullOrWhiteSpace(search.Text))
                {
                    var rows = messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!).ToArray();
                    if (rows.All(mail => mail.Id != sentSummary.Id))
                    {
                        int index = SortFolder(rows.Append(sentSummary).ToArray())
                            .TakeWhile(mail => mail.Id != sentSummary.Id).Count();
                        AddMessageRow(sentSummary, index);
                        if (!selectionMade)
                        {
                            messages.CurrentCell = null;
                            messages.ClearSelection();
                        }
                    }
                }
                _ = ShowFolder(SelectedMessage, refreshCounts: true, backgroundRefresh: true);
            }
            if (sourceDraft is not null)
                await RemoveDraftAfterSend(sourceDraft);
        }
        finally
        {
            composeSession.OperationPending = false;
            if (!IsDisposed && !settingsPage.Visible)
                ReopenFailedCompose();
        }
    }

    private void OpenNewCompose()
    {
        if (FocusComposeWindow() || composeSession.OperationPending || SelectedFolder is not { } entry)
            return;
        (composeTo.Text, composeCc.Text, composeBcc.Text, composeSubject.Text) = ("", "", "", "");
        SetComposeBody(entry.Account);
        OpenCompose(composeTo);
    }

    private async Task OpenLocalDraftAsync()
    {
        if (FocusComposeWindow() || database is null || composeSession.LoadingDraft || composeSession.OperationPending)
            return;
        composeSession.LoadingDraft = true;
        localDrafts.Enabled = false;
        try
        {
            if (ChooseLocalDraft(database) is not { } id)
                return;
            var draft = await Task.Run(() => database.LoadLocalDraft(id));
            if (IsDisposed || settingsPage.Visible || composePage.Visible || composeSession.OperationPending)
                return;
            if (draft is null)
            {
                ThemedPopup.Show(this, "This local draft is no longer available.", Text);
                return;
            }
            var sender = accounts.FirstOrDefault(account => account.Id == draft.SenderId);
            var editor = draft.Editor;
            RestoreDraft(new ComposeDraft(sender, editor.To, editor.Cc, editor.Bcc, editor.ReplyTo,
                editor.UsesDefaultReplyTo, editor.Subject, editor.BodyRtf, editor.Signature, draft.Files,
                HtmlQuote: editor.HtmlQuote, Response: editor.Response));
            composeSession.EditingLocalDraft = id;
            if (sender is not null && draft.ServerDraftId is { } serverId &&
                draft.ServerConnectionKey is { } connectionKey && connectionKey == sender.ConnectionKey())
            {
                composeSession.LocalDraftServerLink = (sender.Id, serverId, connectionKey);
                if (sender.Mailbox is { } mailbox)
                    composeSession.EditingServerDraft = new(sender, mailbox, serverId);
            }
            OpenCompose(composeBody);
            composeStatus.Text = "Opened a draft saved on this computer.";
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException or ArgumentException)
        {
            if (!IsDisposed)
                ThemedPopup.Show(this, "Could not open the local draft. Try again.", Text);
        }
        finally
        {
            composeSession.LoadingDraft = false;
            if (!IsDisposed)
                localDrafts.Enabled = true;
        }
    }

    private async Task OpenServerDraftAsync()
    {
        if (FocusComposeWindow() || composeSession.LoadingDraft || composeSession.OperationPending || showingCachedFolder || SelectedMessage is not { } summary ||
            SelectedFolder is not { Folder: "Drafts", Account.Mailbox: { } mailbox } folder)
            return;
        composeSession.LoadingDraft = true;
        editDraft.Enabled = false;
        try
        {
            var local = ReadDraftEditorState(folder.Account, summary.Id);
            var serverDraft = local is null || summary.Attachments
                ? await mailbox.LoadDraftAsync(summary.Id, mailboxLifetime.Token) : null;
            if (IsDisposed || SelectedFolder != folder || SelectedMessage?.Id != summary.Id ||
                settingsPage.Visible || composePage.Visible || composeSession.OperationPending)
                return;

            if (local is not null)
                RestoreDraft(new ComposeDraft(folder.Account, local.To, local.Cc, local.Bcc, local.ReplyTo,
                    local.UsesDefaultReplyTo, local.Subject, local.BodyRtf, local.Signature,
                    serverDraft?.Files ?? [], HtmlQuote: local.HtmlQuote, Response: local.Response));
            else
                RestoreServerDraft(folder.Account, serverDraft!);
            composeSession.EditingServerDraft = new(folder.Account, mailbox, summary.Id);
            OpenCompose(composeBody);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed)
                ThemedPopup.Show(this, error is DraftSizeException ? error.Message :
                    MailOperationError(error, "Could not open the server draft for editing."), Text);
        }
        finally
        {
            composeSession.LoadingDraft = false;
            if (!IsDisposed)
                editDraft.Enabled = true;
        }
    }

    private async Task SaveComposeDraftAsync()
    {
        if (composeSession.OperationPending)
            return;

        var snapshot = CaptureDraft();
        var sender = snapshot.Sender;
        var mailbox = sender?.Mailbox;
        Guid? replacing = mailbox is not null && ReferenceEquals(composeSession.EditingServerDraft?.Mailbox, mailbox) ? composeSession.EditingServerDraft?.Id : null;
        var target = sender is null ? null : new FolderEntry(sender, "Drafts");
        BeginComposeOperation(SavingDraftStatus);
        await Task.Yield();

        try
        {
            if (IsDisposed || Disposing)
                return;
            bool offline = NetworkUnavailable();
            if (sender is null || mailbox is null || offline)
            {
                await SaveLocalFallbackAsync(offline
                    ? "The computer appears to be offline." : "Choose a connected account to save to a mail server.");
                return;
            }

            DraftSaveResult saved;
            MailContent content;
            try
            {
                CancelBackgroundCache(sender);
                content = GetComposeMessageContent(forDraft: true);
                saved = await mailbox.SaveDraftAsync(new(sender.Email.Trim(), snapshot.ReplyTo.Trim(),
                    snapshot.To, snapshot.Cc, snapshot.Subject, content.Body, content.Html, content.InlineImages,
                    Bcc: snapshot.Bcc, Files: snapshot.Files), replacing, mailboxLifetime.Token);
            }
            catch (OperationCanceledException) when (mailboxLifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                await SaveLocalFallbackAsync(MailOperationError(error, "Could not confirm a server draft save."));
                return;
            }

            if (IsDisposed)
                return;

            await RemoveLocalDraftAfterServerSuccessAsync();
            if (IsDisposed)
                return;
            if (ReferenceEquals(sender.Mailbox, mailbox))
            {
                if (saved.Replaced && replacing is { } oldId)
                    ClearDraftCache(sender, oldId);
                if (saved.Summary is { } summary && target is not null)
                {
                    if (database is not null)
                    {
                        if (mailbox is ServerMailbox)
                        {
                            string key = sender.ConnectionKey();
                            var cached = ReadCachedFolder(target, "Drafts", key);
                            WriteCachedFolder(target, "Drafts", key, cached.Where(mail => mail.Id != summary.Id)
                                .Prepend(summary).ToArray());
                        }
                        CacheDraftEditorState(sender, summary, new DraftEditorState(snapshot.To, snapshot.Cc, snapshot.Bcc,
                            snapshot.ReplyTo, snapshot.Subject, snapshot.UsesDefaultReplyTo, snapshot.BodyRtf,
                            snapshot.Signature, snapshot.HtmlQuote, snapshot.Response), content);
                    }
                    if (SelectedFolder == target && string.IsNullOrWhiteSpace(search.Text))
                    {
                        var current = SelectedMessage;
                        var rows = messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!)
                            .Where(mail => mail.Id != summary.Id && (!saved.Replaced || mail.Id != replacing));
                        RenderFolder(rows.Prepend(summary).ToArray(), current?.Id == replacing && saved.Replaced ? summary : current);
                    }
                }
                sender.CountsUpdatedAt = default;
                if (SelectedFolder == target)
                    _ = ShowFolder(SelectedMessage, refreshCounts: true, backgroundRefresh: true, prioritizeCurrentFolder: true);
            }

            FinishCompose();
            ShowComposeOperationStatus(saved.Warning ?? "Draft saved.", persistent: saved.Warning is not null);
        }
        finally
        {
            composeSession.OperationPending = false;
            if (!IsDisposed && !settingsPage.Visible)
                ReopenFailedCompose();
        }

        async Task SaveLocalFallbackAsync(string reason)
        {
            bool savedLocally = await SaveDraftLocallyAsync(snapshot);
            if (IsDisposed)
                return;
            if (savedLocally)
            {
                FinishCompose();
                ShowComposeOperationStatus($"{reason} Draft saved on this computer. Find it under Local Drafts.", persistent: true);
            }
            else
            {
                composeSession.OperationFailure = $"{reason} Could not save the draft. Your changes are still in the editor.";
                ShowComposeOperationStatus(composeSession.OperationFailure, persistent: true);
            }
        }
    }

    private void InitializeComposePage(Font headingFont, ToolTip hints)
    {
        var heading = new Label { Text = "New Mail", Dock = DockStyle.Top, Height = 48, Font = headingFont };
        var addressHelp = new Label
        {
            Text = "Separate multiple addresses with a semi-colon ; or comma ,",
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 9),
            Font = new Font(Font, FontStyle.Italic)
        };
        var helpFont = addressHelp.Font;
        addressHelp.Disposed += (_, _) => helpFont.Dispose();
        var formScroll = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, LogicalToDeviceUnits(8), 0)
        };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2
        };
        var editorRow = new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(ComposePaneHeight) + 12);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddFormattingButton("B", "Bold", FontStyle.Bold, () => composeBody.HasFormat(MailBodyEditor.Format.Bold),
            () => composeBody.ToggleFormat(MailBodyEditor.Format.Bold));
        AddFormattingButton("I", "Italic", FontStyle.Italic, () => composeBody.HasFormat(MailBodyEditor.Format.Italic),
            () => composeBody.ToggleFormat(MailBodyEditor.Format.Italic));
        AddFormattingButton("U", "Underline", FontStyle.Underline, () => composeBody.HasFormat(MailBodyEditor.Format.Underline),
            () => composeBody.ToggleFormat(MailBodyEditor.Format.Underline));
        AddFormattingButton("", "Bullets", FontStyle.Regular, () => composeBody.SelectionBullet,
            () => composeBody.SelectionBullet = !composeBody.SelectionBullet, ToolbarIcon.BulletedList);
        AddFormattingButton("", "Numbered list", FontStyle.Regular, composeBody.HasNumberedList,
            composeBody.ToggleNumberedList, ToolbarIcon.NumberedList);
        AddFormattingAction("", "Font color", () =>
        {
            Color selected = composeBody.SelectionColor;
            using var picker = new ColorDialog { Color = selected.IsEmpty ? composeBody.ForeColor : selected, FullOpen = true };
            if (picker.ShowDialog(ComposeDialogOwner) == DialogResult.OK)
                composeBody.SelectionColor = picker.Color;
        }, ToolbarIcon.Palette);
        AddFormattingAction("↑", "Increase font size", () => composeBody.ChangeFontSize(1));
        AddFormattingAction("↓", "Decrease font size", () => composeBody.ChangeFontSize(-1));
        AddFormattingAction("Reset", "Reset formatting", composeBody.ResetFormatting);

        void AddFormattingButton(string glyph, string label, FontStyle style, Func<bool> isActive, Action toggle,
            ToolbarIcon? icon = null)
        {
            var glyphFont = new Font(Font, style);
            var button = new CheckBox
            {
                Text = glyph, Font = glyphFont, Tag = icon,
                Appearance = Appearance.Button, AutoCheck = false, FlatStyle = FlatStyle.Flat
            };
            button.Disposed += (_, _) => glyphFont.Dispose();
            button.Click += (_, _) =>
            {
                composeBody.Focus();
                toggle();
                button.Checked = isActive();
            };
            composeBody.SelectionChanged += (_, _) => button.Checked = isActive();
            AddToolbarButton(button, label);
        }

        void AddFormattingAction(string glyph, string label, Action action, ToolbarIcon? icon = null)
        {
            var button = new Button { Text = glyph, Tag = icon };
            button.Click += (_, _) =>
            {
                composeBody.Focus();
                action();
            };
            AddToolbarButton(button, label);
        }

        void AddToolbarButton(ButtonBase button, string label)
        {
            button.AccessibleName = label;
            button.Margin = new Padding(0, 0, 6, 3);
            button.FontChanged += (_, _) => FitLabel();
            hints.SetToolTip(button, label);
            composeFormatting.Controls.Add(button);
            FitLabel();

            void FitLabel()
            {
                button.AutoSize = false;
                button.Size = new Size(
                    Math.Max(34, TextRenderer.MeasureText(button.Text, button.Font).Width + 16),
                    Math.Max(26, TextRenderer.MeasureText("Ag", Font).Height + 8));
            }
        }

        var addressDetailRows = new List<(RowStyle Style, Control Label, Control Field)>();
        var moreRow = new RowStyle(SizeType.AutoSize);
        foreach (var field in new Control[] { composeTo, composeCc, composeFrom, composeBcc, composeReplyTo, composeSubject, composeBody })
        {
            if (field == composeFrom)
            {
                fields.RowStyles.Add(moreRow);
                fields.Controls.Add(composeAddressDetails, 1, fields.RowCount++);
            }
            if (field == composeBody)
            {
                fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                fields.Controls.Add(CreateActionRow(copy: true), 1, fields.RowCount++);
                fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                fields.Controls.Add(composeAttachmentPanel, 1, fields.RowCount++);
                fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                fields.Controls.Add(composeFormatting, 1, fields.RowCount++);
            }
            int row = fields.RowCount++;
            var rowStyle = field == composeBody
                ? editorRow
                : new RowStyle(SizeType.AutoSize);
            fields.RowStyles.Add(rowStyle);
            var label = new Label
            {
                Text = field.AccessibleName, AutoSize = true,
                Margin = new Padding(0, 6, 16, 0)
            };
            fields.Controls.Add(label, 0, row);
            var fieldFrame = field switch
            {
                TextBox address when address != composeSubject => CreateWrappingAddressField(address),
                TextBoxBase input => CreateTextField(input),
                WrappingAddressSelector selector => CreateAddressSelectorFrame(selector),
                _ => field
            };
            fieldFrame.Margin = new Padding(0, 3, 0, 9);
            fields.Controls.Add(fieldFrame, 1, row);
            if (field == composeBody)
            {
                composeQuoteHost.Margin = fieldFrame.Margin;
                composeQuoteHost.Controls.Add(composeQuoteFallback);
                var quoteActions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
                };
                quoteActions.Controls.Add(showComposeQuoteImages);
                composeQuoteHost.Controls.Add(quoteActions);
                showComposeQuoteImages.Click += (_, _) =>
                {
                    composeSession.ShowQuoteImages = true;
                    DisplayComposeQuote();
                };
                fields.RowStyles.Add(composeQuoteRow);
                composeQuoteLabel.Margin = label.Margin;
                fields.Controls.Add(composeQuoteLabel, 0, fields.RowCount);
                fields.Controls.Add(composeQuoteHost, 1, fields.RowCount++);
            }
            if (field == composeFrom || field == composeBcc || field == composeReplyTo)
                addressDetailRows.Add((rowStyle, label, fieldFrame));
        }
        composeAddressDetails.CheckedChanged += (_, _) => UpdateAddressDetails();
        UpdateAddressDetails();

        void UpdateAddressDetails()
        {
            bool expanded = composeAddressDetails.Checked;
            fields.SuspendLayout();
            moreRow.SizeType = expanded ? SizeType.Absolute : SizeType.AutoSize;
            moreRow.Height = 0;
            composeAddressDetails.Visible = !expanded;
            foreach (var (style, label, field) in addressDetailRows)
            {
                style.SizeType = expanded ? SizeType.AutoSize : SizeType.Absolute;
                style.Height = 0;
                label.Visible = field.Visible = expanded;
            }
            fields.ResumeLayout();
            if (expanded)
                composeBcc.Focus();
        }

        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = CreateActionRow(copy: false);
        fields.Controls.Add(actions, 1, fields.RowCount++);
        composeStatus.TextChanged += (_, _) => composeStatus.Visible = composeStatus.Text.Length > 0;
        composePage.ClientSizeChanged += (_, _) => composeStatus.MaximumSize =
            new Size(Math.Max(1, composePage.ClientSize.Width - composePage.Padding.Horizontal), 0);
        formScroll.Controls.Add(fields);
        composePage.Controls.Add(formScroll);
        composePage.Controls.Add(composeStatus);
        composePage.Controls.Add(addressHelp);
        composePage.Controls.Add(heading);
        if (database is not null)
            recipientAutocomplete = new RecipientAutocomplete(composePage, database, composeTo, composeCc, composeBcc);

        FlowLayoutPanel CreateActionRow(bool copy)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true, Dock = DockStyle.Fill,
                Margin = copy ? new Padding(0, 0, 0, 9) : Padding.Empty
            };
            AddButton(backFromCompose, new Padding(0, 0, 8, 0));
            AddButton(saveDraft, new Padding(0, 0, 8, 0));
            AddButton(attachFiles, new Padding(0, 0, 8, 0));
            AddButton(popOutCompose, Padding.Empty);
            row.Controls.Add(new Label
            {
                Text = "·", AutoSize = true, Anchor = AnchorStyles.None,
                Margin = new Padding(8, 0, 8, 0), AccessibleRole = AccessibleRole.Separator
            });
            AddButton(sendMail, Padding.Empty);
            return row;

            void AddButton(Button original, Padding margin)
            {
                Button button = original;
                if (copy)
                {
                    button = new Button
                    {
                        Text = original.Text, AutoSize = true, FlatStyle = original.FlatStyle,
                        Padding = original.Padding, Tag = original.Tag, Enabled = original.Enabled
                    };
                    button.Click += (_, _) => original.PerformClick();
                    original.EnabledChanged += (_, _) => button.Enabled = original.Enabled;
                    original.TextChanged += (_, _) => button.Text = original.Text;
                    composeActionCopies.Add(button);
                }
                button.Margin = margin;
                row.Controls.Add(button);
            }
        }
    }

    private static Panel CreateWrappingAddressField(TextBox input)
    {
        input.Multiline = true;
        input.WordWrap = true;
        var frame = CreateTextField(input);
        frame.Dock = DockStyle.Top;
        input.TextChanged += (_, _) => ResizeField();
        input.FontChanged += (_, _) => ResizeField();
        input.SizeChanged += (_, _) => ResizeField();
        input.VisibleChanged += (_, _) => ResizeField();
        ResizeField();
        return frame;

        void ResizeField()
        {
            if (input.Width <= 0)
                return;
            int lines = Math.Max(1, input.GetLineFromCharIndex(input.TextLength) + 1);
            frame.Height = input.PreferredHeight + (lines - 1) * input.Font.Height +
                frame.Padding.Vertical + 2;
        }
    }

    private static Panel CreateAddressSelectorFrame(WrappingAddressSelector selector)
    {
        var frame = new Panel { Dock = DockStyle.Top, Height = selector.Height };
        frame.Controls.Add(selector);
        selector.SizeChanged += (_, _) => frame.Height = selector.Height;
        return frame;
    }

    private MailContent GetComposeMessageContent(bool forDraft = false)
    {
        var reply = composeBody.GetMessageContent();
        return composeSession.HtmlQuote?.WithReply(reply, forDraft) ?? reply;
    }

    private void SetComposeQuoteVisible(bool visible)
    {
        composeQuoteRow.Height = visible ? LogicalToDeviceUnits(ComposePaneHeight) + composeQuoteHost.Margin.Vertical : 0;
        composeQuoteLabel.Visible = composeQuoteHost.Visible = visible;
    }

    private void DisplayComposeQuote()
    {
        if (composeSession.HtmlQuote is not { } quote)
        {
            SetComposeQuoteVisible(false);
            return;
        }

        SetComposeQuoteVisible(true);
        var source = composeSession.QuoteImageSource;
        bool showImages = composeSession.ShowQuoteImages && source is not null;
        showComposeQuoteImages.Visible = !showImages && source is not null;
        var displayQuote = showImages && source is { } original
            ? ComposeHtmlQuote.Create(original.Header, original.Message, includeImages: true,
                approvedImage: url => quote.Images.FirstOrDefault(image => image.ContentLocation == url),
                allowRemoteContent: true)
            : quote;
        composeQuoteFallback.Text = quote.Body;
        composeQuoteFallback.Show();
        if (composeQuotePreview is null)
        {
            composeQuotePreview = new HtmlMailPreview
            {
                Dock = DockStyle.Fill, Visible = false, AccessibleName = "Original HTML message",
                UseDarkScrollbars = darkTitleBar
            };
            composeQuoteHost.Controls.Add(composeQuotePreview);
            composeQuotePreview.BringToFront();
        }
        _ = ShowQuoteAsync();

        async Task ShowQuoteAsync()
        {
            var quotedContent = new MailContent(displayQuote.Body, Html: displayQuote.HtmlForPreview(), InlineImages: displayQuote.Images);
            bool loaded = await composeQuotePreview.DisplayAsync(quotedContent, showRemoteImages: showImages, showInlineImages: true);
            if (!ReferenceEquals(composeSession.HtmlQuote, quote) || !composePage.Visible)
                return;
            if (loaded)
                composeQuoteFallback.Hide();
            else if (showImages)
                showComposeQuoteImages.Show();
        }
    }

    private DraftEditorState? ReadDraftEditorState(MailAccount account, Guid draft)
    {
        try
        {
            return database?.LoadDraftEditorState(account.Id, account.ConnectionKey(), draft);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void CacheDraftEditorState(MailAccount account, MailSummary summary, DraftEditorState state, MailContent content)
    {
        if (database is null)
            return;
        string key = account.ConnectionKey();
        try
        {
            database.SaveDraftEditorState(account.Id, key, summary.Id, state);
            WriteCachedBody(account, summary, key, content);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // The server's draft remains available if the local editor snapshot cannot be saved.
        }
    }

    private void ClearDraftCache(MailAccount account, Guid draft)
    {
        try
        {
            database?.RemoveMessageCaches(account.Id, "Drafts", account.ConnectionKey(), [draft]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    private async Task RemoveDraftAfterSend(EditingDraft draft)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        CancelBackgroundCache(draft.Account);
        try
        {
            await draft.Mailbox.RemoveDraftAsync(draft.Id, timeout.Token);
            if (!IsDisposed)
            {
                draft.Account.CountsUpdatedAt = default;
                ClearDraftCache(draft.Account, draft.Id);
            }
        }
        catch (Exception)
        {
            if (!IsDisposed)
                ThemedPopup.Show(this, "Message sent. The previous draft could not be removed.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void CacheRecipientAddresses(MailAccount account, string to, string cc, string bcc)
    {
        if (database is null)
            return;
        try
        {
            database.SaveKnownAddresses(account.Id, account.ConnectionKey(), [to, cc, bcc]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Delivery succeeded even if the suggestion cache could not be updated.
        }
    }

    private void CacheSentMessage(FolderEntry folder, IMailbox mailbox, MailSummary summary, MailContent content)
    {
        if (database is null || mailbox is not ServerMailbox)
            return;

        const string name = "Sent";
        var key = folder.Account.ConnectionKey();
        var cached = ReadCachedFolder(folder, name, key);
        var rows = cached.Where(mail => mail.Id != summary.Id).Append(summary).ToArray();
        WriteCachedFolder(folder, name, key, rows);
        WriteCachedBody(folder.Account, summary, key, content);
    }

    private async Task MarkOriginalResponseAsync(ResponseOrigin response)
    {
        var account = accounts.FirstOrDefault(account => account.Id == response.AccountId &&
            account.ConnectionKey() == response.ConnectionKey);
        if (account?.Mailbox is not ServerMailbox mailbox)
            return;

        try
        {
            if (!await mailbox.MarkResponseAsync(response, mailboxLifetime.Token) || IsDisposed)
                return;

            if (SelectedFolder == new FolderEntry(account, response.Folder) && ReferenceEquals(account.Mailbox, mailbox))
            {
                foreach (DataGridViewRow row in messages.Rows)
                {
                    if (row.Tag is not MailSummary mail || mail.Id != response.MessageId)
                        continue;
                    var updated = response.IsReply ? mail with { Replied = true } : mail with { Forwarded = true };
                    row.Tag = updated;
                    row.Cells[3].Value = GridSubject(updated);
                    break;
                }
            }

            if (database is { } store)
                await Task.Run(() => store.UpdateCachedResponseFlag(response));
        }
        catch (OperationCanceledException) when (mailboxLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Could not mark the original message ({error.GetType().Name}).");
        }
    }
}
