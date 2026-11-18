using System.Runtime.InteropServices;

namespace Lit3Mail;

public partial class MailForm : Form, IMessageFilter
{
    private bool showButtonFocus = true;
    private const int UseImmersiveDarkMode = 20;
    private bool darkTitleBar;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private readonly CancellationTokenSource mailboxLifetime = new();
    private CancellationTokenSource? folderLoad;
    private bool showingCachedFolder;
    private FolderEntry? loadedUnfilteredFolder;
    private bool suppressSearchReload;
    private Guid? selectedMessageId;
    private bool selectionMade;
    private MailSummary? SelectedMessage => selectionMade && messages.SelectedRows.Count == 1
        ? messages.SelectedRows[0].Tag as MailSummary : null;
    private readonly SplitContainer mailPane = new()
    {
        Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
        Size = new Size(866, 650), SplitterDistance = 280, Panel2Collapsed = true
    };
    private CancellationTokenSource? previewLoad;
    private int previewRequest;
    private int htmlDisplayAttempt;
    private MailContent? previewContent;
    private HtmlMailPreview? htmlPreview;
    private bool rebuildingRows;

    private readonly ListBox folders = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28
    };
    private readonly ComboBox theme = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        AccessibleName = "App theme"
    };
    private readonly TextBox search = new()
    {
        PlaceholderText = "Search this folder", AccessibleName = "Search this folder"
    };
    private readonly Button clearSearch = new()
    {
        Text = "Clear", AccessibleName = "Clear search", Dock = DockStyle.Right,
        AutoSize = true, FlatStyle = FlatStyle.Flat, Visible = false
    };
    private readonly CheckBox exact = new()
    {
        Text = "Exact", Appearance = Appearance.Button, AutoCheck = false, Dock = DockStyle.Right,
        FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter
    };
    private sealed class SelectionCountLabel : Label
    {
        public SelectionCountLabel() => DoubleBuffered = true;
    }

    private readonly SelectionCountLabel messageSelectionCount = new()
    {
        Text = "0 selected", AccessibleName = "Message selection count",
        Dock = DockStyle.Bottom, AutoSize = false, Height = 26,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0)
    };
    private readonly Label searchStatus = new()
    {
        Text = "Searching...", Dock = DockStyle.Top, AutoSize = true,
        Padding = new Padding(12, 0, 0, 4), Visible = false
    };
    private readonly Panel searchBar = new()
    {
        Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 9, 12, 9)
    };
    private readonly SplitContainer mailLayout;
    private readonly Panel sidebar = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 16, 14, 16) };
    private readonly Panel preview = new() { Dock = DockStyle.Fill, Padding = new Padding(24, 10, 24, 10) };
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill, Padding = new Padding(24), Visible = false };
    private readonly Button editDraft = new()
    {
        Text = "Edit Draft", AutoSize = true, FlatStyle = FlatStyle.Flat, Visible = false
    };
    private readonly Button reply = new() { Text = "Reply", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Button replyAll = new() { Text = "Reply All", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Button forward = new() { Text = "Forward", AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Button deleteMail = new()
    {
        Text = "🗑 Delete", AccessibleName = "Delete", AutoSize = true, FlatStyle = FlatStyle.Flat, Enabled = false
    };
    private readonly Label deleteActionSeparator = new()
    {
        Text = "·", AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(6, 0, 6, 0)
    };
    private readonly Button showImages = new()
    {
        Text = "⚠ Show images", AccessibleName = "Show images", AutoSize = true,
        FlatStyle = FlatStyle.Flat, Visible = false
    };
    private readonly Label imageActionSeparator = new()
    {
        Text = "·", AutoSize = true, Anchor = AnchorStyles.None,
        Margin = new Padding(6, 0, 6, 0), Visible = false
    };
    private Guid? imagePermission;
    private readonly Label settingsHeading = new() { Text = "Settings", Dock = DockStyle.Top, Height = 44 };
    private readonly Button newEmail = new() { Text = "New Email", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private readonly Button checkMail = new()
    {
        Text = "↻", AccessibleName = "Check for Mail", Dock = DockStyle.Right,
        Width = 40, FlatStyle = FlatStyle.Flat
    };
    private readonly Button settings = new()
    {
        Text = "Settings", Dock = DockStyle.Bottom, Height = 36, FlatStyle = FlatStyle.Flat
    };
    private readonly Button backToMail = new()
    {
        Text = "Back to Mail", Dock = DockStyle.Top, Height = 36, FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleLeft, Tag = ToolbarIcon.Back, Padding = new Padding(22, 0, 0, 0)
    };
    private readonly Label folderHeading = new()
    {
        Text = "Folders", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 0, 6)
    };
    private readonly MessageGrid messages = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeight = 38,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        MultiSelect = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        EnableHeadersVisualStyles = false,
        RowHeadersVisible = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private sealed class PreviewHeaderTextBox : TextBox
    {
        private const int SetMargins = 0x00D3;
        private const int LeftMargin = 1;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Message message = Message.Create(Handle, SetMargins, LeftMargin, 0);
            DefWndProc(ref message);
        }
    }

    private readonly PreviewHeaderTextBox previewSubject = new()
    {
        AccessibleName = "Subject", Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        AutoSize = false, Margin = new Padding(0, 0, 8, 3),
        Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None
    };
    private readonly PreviewHeaderTextBox previewDetails = new()
    {
        AccessibleName = "Message addresses and details", Dock = DockStyle.Top, AutoSize = false,
        Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None
    };
    private readonly RichTextBox previewBody = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window, DetectUrls = false
    };
    private int sortColumn = 5;
    private readonly Font unreadFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Font flagIconFont = new("Segoe UI Symbol", 14);
    private bool descending = true;
    private Color selectedBackground;
    private Color selectedText;
    private const string DateFormat = "dd MMM yyyy h:mm tt";

    public MailForm()
    {
        database = LocalDatabase.ForCurrentUser();
        LoadAccountSettings(database);

        bool firstRun = accounts.Count == 0;
        if (firstRun)
            accounts.Add(new MailAccount("Your Name", "", null));

        InitializeComponent();
        using var iconStream = typeof(MailForm).Assembly.GetManifestResourceStream("Lit3Mail.Envelope.ico")!;
        var envelopeIcon = new Icon(iconStream);
        Icon = envelopeIcon;
        Text = "Lit3Mail";
        ClientSize = new Size(1040, 650);
        MinimumSize = new Size(720, 440);
        var uiFont = new Font("Segoe UI", 10);
        var headingFont = new Font("Segoe UI", 11, FontStyle.Bold);
        var subjectFont = new Font("Segoe UI", 18, FontStyle.Bold);
        var refreshFont = new Font("Segoe UI Symbol", 15);
        var hints = new ToolTip(components!);
        Font = uiFont;
        folderHeading.Font = headingFont;
        previewSubject.Font = subjectFont;
        settingsHeading.Font = subjectFont;
        var searchStatusFont = new Font(uiFont, FontStyle.Italic);
        searchStatus.Font = searchStatusFont;
        searchStatus.Disposed += (_, _) => searchStatusFont.Dispose();
        checkMail.Font = refreshFont;
        hints.SetToolTip(checkMail, "Check for Mail");
        EventHandler? releaseResources = null;
        releaseResources = (_, _) =>
        {
            Disposed -= releaseResources;
            Application.RemoveMessageFilter(this);
            composeWindow?.Dispose();
            envelopeIcon.Dispose();
            uiFont.Dispose();
            headingFont.Dispose();
            subjectFont.Dispose();
            refreshFont.Dispose();
            unreadFont.Dispose();
            flagIconFont.Dispose();
            mailboxLifetime.Cancel();
            mailboxLifetime.Dispose();
        };
        Disposed += releaseResources;

        mailLayout = new SplitContainer
        {
            Dock = DockStyle.Fill, Size = ClientSize, SplitterDistance = 186,
            FixedPanel = FixedPanel.Panel1
        };
        folders.AccessibleName = "Folders";
        messages.AccessibleName = "Email list";
        previewBody.AccessibleName = "Email body";
        var mailActions = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(0, 0, 0, 14) };
        mailActions.Controls.Add(newEmail);
        mailActions.Controls.Add(checkMail);
        sidebar.Controls.Add(folders);
        sidebar.Controls.Add(folderHeading);
        localDrafts.Enabled = database is not null;
        sidebar.Controls.Add(mailActions);
        sidebar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8 });
        sidebar.Controls.Add(exportMail);
        sidebar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 6 });
        sidebar.Controls.Add(localDrafts);
        sidebar.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 6 });
        sidebar.Controls.Add(settings);
        sidebar.Controls.Add(updateNotice);
        mailLayout.Panel1.Controls.Add(sidebar);
        mailLayout.Panel2.Controls.Add(mailPane);
        // Give the grid its own viewport between the docked search and status controls.
        var messageViewport = new Panel { Dock = DockStyle.Fill };
        messageViewport.Controls.Add(messages);
        mailPane.Panel1.Controls.Add(messageViewport);
        mailPane.Panel1.Controls.Add(batchBar);
        mailPane.Panel1.Controls.Add(composeOperationBar);
        mailPane.Panel1.Controls.Add(messageSelectionCount);
        messages.SelectionChanged += (_, _) =>
            messageSelectionCount.Text = $"{messages.SelectedRows.Count:N0} selected";
        searchBar.Controls.Add(CreateTextField(search));
        searchBar.Controls.Add(clearSearch);
        searchBar.Controls.Add(exact);
        hints.SetToolTip(clearSearch, "Clear search");
        mailPane.Panel1.Controls.Add(searchStatus);
        mailPane.Panel1.Controls.Add(searchBar);
        var previewMenu = new ContextMenuStrip();
        var copyPreview = previewMenu.Items.Add("Copy", null, (_, _) => previewBody.Copy());
        previewMenu.Opening += (_, _) => copyPreview.Enabled = previewBody.SelectionLength > 0;
        previewBody.ContextMenuStrip = previewMenu;
        previewBody.Disposed += (_, _) => previewMenu.Dispose();
        preview.Controls.Add(previewBody);
        InitializeAttachmentControls();
        preview.Controls.Add(previewAttachmentPanel);
        var previewActions = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, Margin = Padding.Empty
        };
        previewActions.Controls.Add(editDraft);
        previewActions.Controls.Add(reply);
        previewActions.Controls.Add(replyAll);
        previewActions.Controls.Add(forward);
        previewActions.Controls.Add(deleteActionSeparator);
        previewActions.Controls.Add(moveMail);
        previewActions.Controls.Add(deleteMail);
        previewActions.Controls.Add(imageActionSeparator);
        previewActions.Controls.Add(showImages);
        showImages.Click += (_, _) =>
        {
            if (SelectedMessage is not { } mail || previewContent is not { Html.Length: > 0 } content)
                return;

            imagePermission = mail.Id;
            _ = DisplayHtmlBodyAsync(content, previewRequest, showRemoteImages: true);
            UpdateImagePreview(mail);
        };
        var previewHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 2, Margin = Padding.Empty
        };
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        previewHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewHeader.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewHeader.Controls.Add(previewSubject, 0, 0);
        previewHeader.Controls.Add(previewActions, 1, 0);

        void LayoutPreviewHeader()
        {
            var buttons = previewActions.Controls.OfType<Button>().ToArray();
            int buttonHeight = buttons.Max(button => button.GetPreferredSize(Size.Empty).Height);
            foreach (var button in buttons)
                button.MinimumSize = new Size(ButtonLabelWidth(button), buttonHeight);

            int width = Math.Max(1, preview.ClientSize.Width - preview.Padding.Horizontal);
            int actionWidth = previewActions.Controls.Cast<Control>()
                .Where(action => action.Visible)
                .Sum(action => action.GetPreferredSize(Size.Empty).Width + action.Margin.Horizontal);
            bool stacked = width - actionWidth < 180;
            if (stacked != (previewHeader.GetRow(previewActions) == 1))
            {
                previewHeader.SuspendLayout();
                previewHeader.SetColumnSpan(previewSubject, stacked ? 2 : 1);
                previewHeader.SetCellPosition(previewActions, stacked
                    ? new TableLayoutPanelCellPosition(0, 1)
                    : new TableLayoutPanelCellPosition(1, 0));
                previewHeader.SetColumnSpan(previewActions, stacked ? 2 : 1);
                previewHeader.ResumeLayout();
            }

            int subjectWidth = (stacked ? width : width - actionWidth) - previewSubject.Margin.Horizontal;
            previewSubject.Height = PreviewTextHeight(previewSubject, subjectWidth);
            previewDetails.Height = PreviewTextHeight(previewDetails, width) + 6;
            int toolbarWidth = Math.Min(width, actionWidth);
            previewActions.MinimumSize = new Size(toolbarWidth, 0);
            previewActions.MaximumSize = stacked ? new Size(width, 0) : Size.Empty;
            previewActions.WrapContents = actionWidth > width;
        }

        foreach (Control action in previewActions.Controls)
        {
            if (action is Button button)
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            action.Margin = new Padding(action.Margin.Left, 0, action.Margin.Right, 0);
            action.VisibleChanged += (_, _) => LayoutPreviewHeader();
        }
        previewSubject.TextChanged += (_, _) => LayoutPreviewHeader();
        previewDetails.TextChanged += (_, _) => LayoutPreviewHeader();
        preview.SizeChanged += (_, _) => LayoutPreviewHeader();
        preview.Controls.Add(previewDetails);
        preview.Controls.Add(previewHeader);
        LayoutPreviewHeader();
        mailPane.Panel2.Controls.Add(preview);
        Controls.Add(mailLayout);
        InitializeAccountSettings();
        Controls.Add(settingsPage);
        InitializeCompose(subjectFont, hints);
        deleteMail.Click += async (_, _) => await DeleteSelectedMessages();

        void OpenSettings()
        {
            mailLayout.Hide();
            int defaultTab = accountTabs.Controls.Count > 1 ? 1 : 0;
            ((RadioButton)accountTabs.Controls[defaultTab]).Checked = true;
            settingsPage.Show();
            backToMail.Focus();
        }

        settings.Click += (_, _) => OpenSettings();
        if (firstRun)
            OpenSettings();
        void LeaveSettings()
        {
            if (!SaveAccountSettings())
                return;
            settingsPage.Hide();
            mailLayout.Show();
            settings.Focus();
            ReopenFailedCompose();
        }
        backToMail.Click += (_, _) => LeaveSettings();
        messages.RowTemplate.Height = 28;
        messages.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        messages.ColumnHeadersDefaultCellStyle.Font = headingFont;
        messages.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);

        AddFlagColumn("Read", "✉", 58, editable: true);
        AddFlagColumn("Starred", "★", 58, editable: true);
        AddFlagColumn("Attachments", "📎", 58);
        messages.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Subject", FillWeight = 60, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic
        });
        messages.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Correspondents", FillWeight = 40, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic
        });
        messages.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Date", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 180, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });

        messages.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0)
                return;
            messages.Columns[sortColumn].HeaderCell.SortGlyphDirection = SortOrder.None;
            descending = e.ColumnIndex == sortColumn ? !descending : e.ColumnIndex is 1 or 2 or 5;
            sortColumn = e.ColumnIndex;
            SortPublishedRows(preserveScrollAnchor: false);
            messages.Columns[sortColumn].HeaderCell.SortGlyphDirection =
                descending ? SortOrder.Descending : SortOrder.Ascending;
        };
        HashSet<Guid>? flagSelection = null;
        int flagColumn = -1;
        Guid? flagMessage = null;
        Guid? previewAtMouseDown = null;
        messages.BeforeMouseDown += (_, e) =>
        {
            previewAtMouseDown = null;
            flagSelection = null;
            if (e.Button == MouseButtons.Left && ModifierKeys == Keys.None && selectionMade &&
                messages.SelectedRows.Count > 1)
            {
                var flagHit = messages.HitTest(e.X, e.Y);
                if (flagHit.ColumnIndex is 0 or 1 && flagHit.RowIndex >= 0 &&
                    messages.Rows[flagHit.RowIndex] is { Selected: true, Tag: MailSummary target })
                {
                    flagSelection = SelectedMessageIds();
                    flagColumn = flagHit.ColumnIndex;
                    flagMessage = target.Id;
                }
            }

            if (e.Button != MouseButtons.Left || ModifierKeys != Keys.None || SelectedMessage is not { } selected)
                return;

            var hit = messages.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0 && hit.ColumnIndex >= 3 &&
                messages.Rows[hit.RowIndex].Tag is MailSummary row && row.Id == selected.Id)
                previewAtMouseDown = selected.Id;
        };
        messages.CellMouseClick += async (_, e) =>
        {
            var previous = previewAtMouseDown;
            previewAtMouseDown = null;
            if (previous is null || e.Button != MouseButtons.Left || ModifierKeys != Keys.None ||
                e.RowIndex < 0 || e.ColumnIndex < 3 || SelectedMessage?.Id != previous ||
                messages.Rows[e.RowIndex].Tag is not MailSummary row || row.Id != previous)
                return;

            messages.ClearSelection();
            await SelectCurrentMessage();
        };
        messages.CellClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
                await SelectCurrentMessage();
        };
        messages.KeyDown += async (_, e) =>
        {
            if (e.KeyData != Keys.Delete)
                return;
            e.SuppressKeyPress = true;
            await DeleteSelectedMessages();
        };
        messages.KeyUp += async (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown
                or Keys.Home or Keys.End or Keys.Enter or Keys.Space)
                await SelectCurrentMessage();
        };
        messages.GotFocus += (_, _) =>
        {
            if (!selectionMade)
                messages.ClearSelection();
        };
        messages.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (messages.IsCurrentCellDirty)
                messages.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        messages.KeyDown += (_, _) => flagSelection = null;
        messages.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == flagColumn &&
                messages.Rows[e.RowIndex].Tag is MailSummary target && target.Id == flagMessage &&
                flagSelection is { } selected)
            {
                flagSelection = null;
                foreach (DataGridViewRow row in messages.Rows)
                    if (row.Tag is MailSummary mail && selected.Contains(mail.Id))
                        row.Selected = true;
            }
            UpdateFlag(e);
        };
        messages.CellPainting += PaintSortIndicator;
        messages.CellPainting += PaintFlagIcon;
        search.TextChanged += async (_, _) =>
        {
            exact.Checked = IsQuoted(search.Text.Trim());
            clearSearch.Visible = search.TextLength > 0;
            if (!suppressSearchReload)
                await ShowFolder(SelectedMessage, debounce: search.TextLength > 0, clearingSearch: search.TextLength == 0);
        };
        clearSearch.Click += (_, _) =>
        {
            search.Clear();
            search.Focus();
        };
        exact.Click += (_, _) =>
        {
            string query = search.Text.Trim();
            search.Text = IsQuoted(query) ? query[1..^1] : "\"" + query + "\"";
            search.Focus();
            search.SelectionStart = search.TextLength;
        };
        checkMail.Click += async (_, _) =>
            await RefreshMailAsync(fullRefresh: true, manual: true);
        folders.DrawItem += DrawFolder;
        folders.ClientSizeChanged += (_, _) => folders.Invalidate();
        InitializeStatusBars();
        InitializeExport();
        theme.Items.AddRange(["System", "Light", "Dark"]);
        theme.SelectedIndexChanged += (_, _) => ApplyTheme();
        theme.SelectedIndex = 0;
        bool openingApp = false;
        folders.SelectedIndexChanged += async (_, _) =>
        {
            if (rebuildingFolders || folders.SelectedItem is not FolderEntry { Folder: not null } entry ||
                entry == selectedFolder)
                return;
            selectedFolder = entry;
            folders.Invalidate();
            loadedUnfilteredFolder = null;
            selectionMade = false;
            selectedMessageId = null;
            SetCachedFolder(false);
            messages.Rows.Clear();
            await ShowPreview();
            newEmail.Enabled = true;
            await ShowFolder(showRefreshStatus: openingApp);
        };
        folders.MouseClick += (_, e) =>
        {
            int index = folders.IndexFromPoint(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0 &&
                folders.Items[index] is FolderEntry { Folder: null } group)
                ToggleAccountFolders(group.Account);
        };
        folders.KeyDown += (_, e) =>
        {
            if (e.KeyCode is (Keys.Enter or Keys.Space) && folders.SelectedItem is FolderEntry { Folder: null } group)
            {
                e.SuppressKeyPress = true;
                ToggleAccountFolders(group.Account);
            }
        };
        Shown += (_, _) =>
        {
            openingApp = true;
            RebuildFolders(null, "Inbox");
            openingApp = false;
            if (SelectedFolder is null)
                QueueBackgroundCache();
            _ = RefreshOtherGmailFoldersAsync(SelectedFolder?.Account);
        };
        FormClosing += (_, e) =>
        {
            if (!e.Cancel && e.CloseReason == CloseReason.UserClosing)
            {
                var operations = new List<string>();
                if (composeSession.OperationPending)
                    operations.Add("sending mail or saving a draft");
                if (updatingMessageFolders)
                    operations.Add("moving or deleting messages");
                if (exportCancellation is not null)
                    operations.Add("exporting mail");

                if (operations.Count > 0 && ThemedPopup.Show(this,
                        $"Lit3Mail is still {string.Join(", ", operations)}. Closing now may interrupt this work. Close anyway?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                if (composePage.Visible && composeWindow is null)
                {
                    e.Cancel = true;
                    LeaveCompose();
                    return;
                }
                if (settingsPage.Visible && pendingUpdate is null)
                {
                    e.Cancel = true;
                    LeaveSettings();
                    return;
                }
            }

            if (!e.Cancel && HasUnsavedDrafts() && !ConfirmDiscardDrafts())
            {
                e.Cancel = true;
                return;
            }

            while (!e.Cancel && !SaveAccountSettings(showError: false))
            {
                var choice = ThemedPopup.Show(this,
                    "Could not save account settings. Select Yes to retry, No to close without saving, or Cancel to keep Lit3Mail open.",
                    Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error, MessageBoxDefaultButton.Button3);
                if (choice == DialogResult.No)
                    break;
                e.Cancel = choice != DialogResult.Yes;
            }

            if (!e.Cancel && pendingUpdate is { } update)
            {
                try { update.Start(); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    e.Cancel = true;
                    ThemedPopup.Show(this, $"Could not start the updater. {error.Message}", "Updates",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        };
        InitializeWindowLayout(mailLayout);
        InitializeUnreadBadge();
        InitializeDesktopNotifications();
        InitializeAutoRefresh();
        InitializeUpdateChecks();
        InitializeMessageMove();
        Application.AddMessageFilter(this);
    }

    private static Panel CreateTextField(TextBoxBase input)
    {
        var frame = new Panel
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = !input.Multiline,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        input.BorderStyle = BorderStyle.None;
        input.Dock = input.Multiline ? DockStyle.Fill : DockStyle.Top;
        frame.Controls.Add(input);
        frame.Click += (_, _) => input.Focus();
        return frame;
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyTheme();
        if (sidebar.Parent?.Parent is SplitContainer sidebarSplit)
            sidebarSplit.Panel1MinSize = sidebar.Padding.Horizontal + Math.Max(
                newEmail.MinimumSize.Width + checkMail.Width,
                new[] { exportMail, localDrafts, settings }.Max(button => button.MinimumSize.Width));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBar();
    }

    private void ApplyTitleBar() => ApplyWindowTitleBar(this, darkTitleBar);

    internal static void ApplyWindowTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated)
            return;

        int enabled = dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private void ApplyTheme()
    {
        bool dark = theme.SelectedIndex == 2 || (theme.SelectedIndex == 0 && ThemedPopup.WindowsUsesDarkTheme());
        darkTitleBar = dark && !SystemInformation.HighContrast;
        Application.SetColorMode(darkTitleBar ? SystemColorMode.Dark : SystemColorMode.Classic);
        ApplyTitleBar();
        ThemedPopup.UseDarkTheme = dark;

        Color surface = dark ? Color.FromArgb(24, 30, 42) : Color.White;
        Color chrome = dark ? Color.FromArgb(32, 41, 57) : Color.FromArgb(239, 244, 251);
        Color border = dark ? Color.FromArgb(52, 65, 85) : Color.FromArgb(215, 225, 238);
        Color ink = dark ? Color.FromArgb(232, 239, 249) : Color.FromArgb(25, 43, 69);
        Color muted = dark ? Color.FromArgb(170, 188, 214) : Color.FromArgb(76, 97, 126);
        selectedBackground = dark ? Color.FromArgb(49, 78, 119) : Color.FromArgb(216, 233, 255);
        selectedText = dark ? Color.White : Color.FromArgb(20, 63, 120);

        if (SystemInformation.HighContrast)
        {
            surface = SystemColors.Window;
            chrome = SystemColors.Control;
            border = SystemColors.WindowText;
            ink = muted = SystemColors.WindowText;
            selectedBackground = SystemColors.Highlight;
            selectedText = SystemColors.HighlightText;
        }

        BackColor = border;
        sidebar.BackColor = folders.BackColor = theme.BackColor = composeFrom.BackColor = chrome;
        folderHeading.ForeColor = folders.ForeColor = theme.ForeColor = composeFrom.ForeColor = ink;
        preview.BackColor = previewBody.BackColor = messages.BackgroundColor = surface;
        previewSubject.BackColor = previewDetails.BackColor = surface;
        previewSubject.ForeColor = previewBody.ForeColor = ink;
        previewDetails.ForeColor = muted;
        deleteActionSeparator.ForeColor = imageActionSeparator.ForeColor = muted;
        settingsPage.BackColor = surface;
        settingsHeading.ForeColor = ink;
        ApplyAccountTheme(surface, chrome, ink, muted, border, selectedBackground);
        composePage.BackColor = surface;
        composePage.ForeColor = ink;
        recipientAutocomplete?.SetColors(surface, ink);
        ApplyAttachmentTheme(surface, chrome, ink, border, selectedBackground);
        batchBar.BackColor = surface;
        batchStatus.ForeColor = ink;
        composeOperationBar.BackColor = surface;
        composeOperationStatus.ForeColor = ink;
        StyleButton(exportMail, chrome, ink, border, selectedBackground);
        exportMenu.Renderer = new ExportMenuRenderer(chrome, ink,
            SystemInformation.HighContrast ? SystemColors.GrayText : muted, border, selectedBackground, selectedText);
        StyleButton(cancelExport, chrome, ink, border, selectedBackground);
        StyleButton(composeAddressDetails, chrome, ink, border, selectedBackground);
        foreach (var field in new[] { search, composeTo, composeCc, composeBcc, composeReplyTo, composeSubject })
        {
            field.Parent!.BackColor = field.BackColor = surface;
            field.ForeColor = ink;
        }
        foreach (var button in new[] { newEmail, checkMail, localDrafts, settings, updateNotice, backToMail, addAccount, backFromCompose,
            sendMail, saveDraft, popOutCompose, attachFiles, editDraft, reply, replyAll, forward, moveMail, deleteMail, showImages, showComposeQuoteImages }.Concat(composeActionCopies))
        {
            StyleButton(button, chrome, ink, border, selectedBackground);
        }

        composeBody.Parent!.BackColor = composeBody.BackColor;
        foreach (var button in composeFormatting.Controls.OfType<ButtonBase>())
        {
            StyleButton(button, chrome, ink, border, selectedBackground, autoSize: false);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.FlatAppearance.CheckedBackColor = selectedBackground;
        }
        messages.GridColor = border;
        messages.DefaultCellStyle.BackColor = surface;
        messages.DefaultCellStyle.ForeColor = ink;
        messages.DefaultCellStyle.SelectionBackColor = selectedBackground;
        messages.DefaultCellStyle.SelectionForeColor = selectedText;
        messages.ColumnHeadersDefaultCellStyle.BackColor = chrome;
        messages.ColumnHeadersDefaultCellStyle.ForeColor = ink;
        messages.ColumnHeadersDefaultCellStyle.SelectionBackColor = chrome;
        messages.ColumnHeadersDefaultCellStyle.SelectionForeColor = ink;
        searchBar.BackColor = chrome;
        searchStatus.BackColor = chrome;
        mailPane.Panel1.BackColor = surface;
        if (messages.Parent is { } messageViewport)
            messageViewport.BackColor = surface;
        messageSelectionCount.BackColor = surface;
        searchStatus.ForeColor = messageSelectionCount.ForeColor = muted;
        StyleButton(exact, chrome, ink, border, selectedBackground);
        StyleButton(clearSearch, chrome, ink, border, selectedBackground);
        StyleButton(dismissComposeStatus, chrome, ink, border, selectedBackground);
        exact.TextAlign = ContentAlignment.MiddleCenter;
        exact.FlatAppearance.CheckedBackColor = selectedBackground;
        ScrollbarTheme.Apply(this, darkTitleBar);
        folders.Invalidate();
    }

    private static int PreviewTextHeight(TextBox field, int width) =>
        TextRenderer.MeasureText(field.Text.Length == 0 ? " " : field.Text, field.Font,
            new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;

    private void StyleButton(ButtonBase button, Color background, Color ink, Color border, Color hover, bool autoSize = true)
    {
        button.AutoSize = autoSize;
        button.TextChanged -= FitButtonLabel;
        button.TextChanged += FitButtonLabel;
        button.FontChanged -= FitButtonLabel;
        button.FontChanged += FitButtonLabel;
        FitButtonLabel(button, EventArgs.Empty);
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = background;
        button.ForeColor = ink;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        button.Paint -= PaintButton;
        button.Paint += PaintButton;
    }

    private static int ButtonLabelWidth(ButtonBase button)
    {
        if (button.Text.Length == 0)
            return 0;

        int textWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        int iconWidth = button.Tag is ToolbarIcon ? button.Padding.Left : 0;
        return textWidth + iconWidth + 16;
    }

    private void FitButtonLabel(object? sender, EventArgs e)
    {
        if (sender is ButtonBase button)
        {
            int width = ButtonLabelWidth(button);
            if (button == exact)
                width += 4;
            if (button.Tag is ToolbarIcon.Send)
                width *= 2;
            button.MinimumSize = new Size(width, button.MinimumSize.Height);
        }
    }

    private void PaintButton(object? sender, PaintEventArgs e)
    {
        if (sender is not ButtonBase button || button.ClientSize.Width < 2 || button.ClientSize.Height < 2)
            return;

        var bounds = button.ClientRectangle;
        var appearance = button.FlatAppearance;
        bool isChecked = button is CheckBox { Checked: true } or RadioButton { Checked: true };
        bool hovered = button.Enabled && bounds.Contains(button.PointToClient(Cursor.Position));
        Color background = hovered ? appearance.MouseOverBackColor
            : isChecked ? appearance.CheckedBackColor : button.BackColor;
        Color ink = button.Enabled ? button.ForeColor : SystemColors.GrayText;
        using var fill = new SolidBrush(background);
        using var border = new Pen(appearance.BorderColor);
        e.Graphics.FillRectangle(fill, bounds);
        e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);

        var textBounds = Rectangle.Inflate(bounds, -6, -2);
        var alignment = button.TextAlign == ContentAlignment.MiddleLeft
            ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        var textFlags = alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (button.Parent != composeFormatting)
            textFlags |= TextFormatFlags.EndEllipsis;
        if (button.Tag is ToolbarIcon icon)
        {
            if (button.Text.Length == 0)
                DrawToolbarIcon(e.Graphics, bounds, ink, icon);
            else
            {
                int iconWidth = button.Padding.Left;
                DrawToolbarIcon(e.Graphics, new Rectangle(textBounds.Left, bounds.Top, iconWidth, bounds.Height), ink, icon);
                textBounds.X += iconWidth;
                textBounds.Width = Math.Max(0, textBounds.Width - iconWidth);
            }
        }
        if (button.Text.Length > 0)
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, textBounds, ink, textFlags);

        if (showButtonFocus && button.Focused && bounds.Width > 8 && bounds.Height > 8)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -4), ink, background);
    }

    private enum ToolbarIcon { Palette, NumberedList, BulletedList, Back, Send, Save, PaperClip, PopOut }

    private static void DrawToolbarIcon(Graphics graphics, Rectangle bounds, Color ink, ToolbarIcon icon)
    {
        var state = graphics.Save();
        try
        {
            float scale = Math.Min(bounds.Height / 22f, graphics.DpiY / 96f);
            graphics.TranslateTransform(bounds.Left + bounds.Width / 2f - 8 * scale,
                bounds.Top + bounds.Height / 2f - 8 * scale);
            graphics.ScaleTransform(scale, scale);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(ink, 1.1f);
            if (icon == ToolbarIcon.Back)
            {
                graphics.DrawLine(pen, 2, 8, 14, 8);
                graphics.DrawLines(pen, [new Point(7, 3), new Point(2, 8), new Point(7, 13)]);
            }
            else if (icon == ToolbarIcon.Send)
            {
                graphics.DrawPolygon(pen, [new Point(1, 6), new Point(15, 1), new Point(11, 15), new Point(7, 10)]);
                graphics.DrawLine(pen, 7, 10, 15, 1);
            }
            else if (icon == ToolbarIcon.Save)
            {
                graphics.DrawPolygon(pen, [new Point(2, 1), new Point(11, 1), new Point(14, 4),
                    new Point(14, 15), new Point(2, 15)]);
                graphics.DrawRectangle(pen, 5, 1, 5, 5);
                graphics.DrawRectangle(pen, 5, 9, 6, 6);
            }
            else if (icon == ToolbarIcon.PopOut)
            {
                graphics.DrawLines(pen, [new Point(14, 8), new Point(14, 14), new Point(2, 14),
                    new Point(2, 2), new Point(8, 2)]);
                graphics.DrawLine(pen, 7, 9, 14, 2);
                graphics.DrawLines(pen, [new Point(10, 2), new Point(14, 2), new Point(14, 6)]);
            }
            else if (icon == ToolbarIcon.PaperClip)
            {
                using var clip = new System.Drawing.Drawing2D.GraphicsPath();
                clip.AddLine(9, 5, 4.5f, 9.5f);
                clip.AddBezier(4.5f, 9.5f, 3, 11, 5, 13, 6.5f, 11.5f);
                clip.AddLine(6.5f, 11.5f, 13, 5);
                clip.AddBezier(13, 5, 15.5f, 2.5f, 12, -1, 9.5f, 1.5f);
                clip.AddLine(9.5f, 1.5f, 3, 8);
                clip.AddBezier(3, 8, -1, 12, 4, 17, 8, 13);
                clip.AddLine(8, 13, 14, 7);
                graphics.DrawPath(pen, clip);
            }
            else if (icon == ToolbarIcon.Palette)
            {
                using var outline = new System.Drawing.Drawing2D.GraphicsPath();
                outline.AddBezier(15, 8, 15, 0, 3, 0, 1, 6);
                outline.AddBezier(1, 6, -1, 13, 7, 17, 10, 13);
                outline.AddBezier(10, 13, 11, 11, 8, 11, 10, 9);
                outline.AddBezier(10, 9, 12, 8, 15, 11, 15, 8);
                graphics.DrawPath(pen, outline);
                using var dot = new SolidBrush(ink);
                graphics.FillEllipse(dot, 3, 7, 2, 2);
                graphics.FillEllipse(dot, 5, 4, 2, 2);
                graphics.FillEllipse(dot, 9, 4, 2, 2);
            }
            else
            {
                if (icon == ToolbarIcon.NumberedList)
                {
                    graphics.DrawLine(pen, 3, 1, 3, 4);
                    graphics.DrawLines(pen, [new Point(1, 6), new Point(4, 6), new Point(1, 10), new Point(4, 10)]);
                    graphics.DrawLines(pen, [new Point(1, 12), new Point(4, 12), new Point(4, 15), new Point(1, 15)]);
                    graphics.DrawLine(pen, 2, 13.5f, 4, 13.5f);
                }
                using var dot = new SolidBrush(ink);
                foreach (int y in new[] { 2, 8, 14 })
                {
                    if (icon == ToolbarIcon.BulletedList)
                        graphics.FillEllipse(dot, 1.5f, y - 1.5f, 3, 3);
                    graphics.DrawLine(pen, 7, y, 15, y);
                }
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F) && FocusFolderSearch())
            return true;
        if (keyData == Keys.Delete && (messages.ContainsFocus || preview.ContainsFocus))
        {
            _ = DeleteSelectedMessages();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool FocusFolderSearch()
    {
        if (!search.CanFocus)
            return false;
        search.Focus();
        search.SelectAll();
        return true;
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (!IsHandleCreated || (ActiveForm != this && message.HWnd != Handle))
            return false;

        // Include both client-area and title-bar mouse button presses.
        bool mouse = message.Msg is 0x0201 or 0x0203 or 0x0204 or 0x0206 or 0x0207 or 0x0209 or 0x020B or 0x020D
            or 0x00A1 or 0x00A3 or 0x00A4 or 0x00A6 or 0x00A7 or 0x00A9 or 0x00AB or 0x00AD;
        bool keyboard = message.Msg is 0x0100 or 0x0104;
        if ((mouse || keyboard) && showButtonFocus != keyboard)
        {
            showButtonFocus = keyboard;
            Invalidate(true);
        }

        return false;
    }

    private void DrawFolder(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        bool selected = Equals(folders.Items[e.Index], SelectedFolder) || Equals(folders.Items[e.Index], highlightedDropFolder);
        using var background = new SolidBrush(selected ? selectedBackground : folders.BackColor);
        var rowBounds = new Rectangle(0, e.Bounds.Y, folders.ClientSize.Width, e.Bounds.Height);
        e.Graphics.FillRectangle(background, rowBounds);
        var textBounds = new Rectangle(10, rowBounds.Y, Math.Max(0, rowBounds.Width - 10), rowBounds.Height);
        var entry = (FolderEntry)folders.Items[e.Index];
        var count = entry.Folder is null ? null : entry.Account.Counts.GetValueOrDefault(entry.Folder);
        int unreadCount = count?.Unread ?? 0;
        var font = entry.Folder is null || unreadCount > 0 ? unreadFont : folders.Font;
        string label = entry.Folder is null
            ? $"{(entry.Account.Expanded ? "▾" : "▸")} {entry.Account.DisplayName}"
            : count is not { HasCounts: true } ? $"    {entry.FolderTitle}" :
                $"    {entry.FolderTitle} ({unreadCount})";
        TextRenderer.DrawText(e.Graphics, label, font, textBounds,
            selected ? selectedText : folders.ForeColor, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0 && (e.State & DrawItemState.NoFocusRect) == 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, rowBounds, folders.ForeColor, background.Color);
    }

    private void AddFlagColumn(string name, string icon, int width, bool editable = false)
    {
        var column = new DataGridViewCheckBoxColumn
        {
            Name = name, HeaderText = icon, ToolTipText = name, Width = width,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = !editable,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };
        column.HeaderCell.Style.Font = flagIconFont;
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        messages.Columns.Add(column);
    }

    private Dictionary<(Guid Id, bool ReadFlag), bool>? folderLoadFlagOverrides;

    private async Task ShowFolder(MailSummary? selected = null, bool debounce = false,
        bool refreshCounts = false, bool showRefreshStatus = false, bool backgroundRefresh = false,
        bool clearingSearch = false, bool prioritizeCurrentFolder = false)
    {
        if (SelectedFolder is not { Folder: { } folder } entry || IsDisposed)
            return;

        folderLoad?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        folderLoad = load;
        var token = load.Token;
        var mailbox = entry.Account.Mailbox;
        var flagOverrides = flagUpdates.Where(pair => ReferenceEquals(pair.Key.Mailbox, mailbox))
            .ToDictionary(pair => (pair.Key.Id, pair.Key.ReadFlag), pair => pair.Value.Value);
        folderLoadFlagOverrides = flagOverrides;
        var activity = showRefreshStatus && mailbox is not null ? BeginBatchActivity(CheckingForMailStatus) : null;
        searchStatus.Hide();
        bool receivedBatch = false;
        bool seededSearch = false;
        IReadOnlyList<MailSummary> cachedSearchMatches = [];
        bool mergedFirstBatch = false;
        Task cachedSearch = Task.CompletedTask;
        var publishedIds = new HashSet<Guid>();
        try
        {
            if (debounce)
                await Task.Delay(300, token);

            string query = search.Text;
            bool cacheable = database is not null && mailbox is ServerMailbox && string.IsNullOrWhiteSpace(query);
            string? connectionKey = cacheable ? entry.Account.ConnectionKey() : null;
            if (connectionKey is not null && (messages.Rows.Count == 0 || debounce || clearingSearch))
            {
                var cached = ReadCachedFolder(entry, folder, connectionKey);
                if (cached.Count > 0 || clearingSearch)
                {
                    SetCachedFolder(cached.Count > 0);
                    RenderFolder(cached.Where(IsVisibleDuringMove).Select(mail => ApplyFlagOverrides(mail)).ToArray(), selected);
                    _ = ShowPreview();
                }
            }

            if (database is { } store && mailbox is ServerMailbox && !string.IsNullOrWhiteSpace(query))
                cachedSearch = PublishCachedSearchAsync(store, entry.Account.ConnectionKey(), query);

            if (mailbox is ServerMailbox && NetworkUnavailable())
            {
                await cachedSearch;
                token.ThrowIfCancellationRequested();
                ShowFolderLoadError("The computer appears to be offline. Reconnect to refresh mail.");
                return;
            }

            searchStatus.Visible = mailbox is ServerMailbox && !string.IsNullOrWhiteSpace(query);
            var folderMail = mailbox switch
            {
                ServerMailbox server when !string.IsNullOrWhiteSpace(query) ||
                    messages.Rows.Count == 0 || showingCachedFolder || clearingSearch || prioritizeCurrentFolder =>
                    await server.ListAsync(folder, query, token, PublishBatch),
                null => [],
                _ => await mailbox.ListAsync(folder, query, token)
            };
            await cachedSearch;
            token.ThrowIfCancellationRequested();
            if (IsDisposed)
                return;

            searchStatus.Hide();
            if (batchStatus.Text == SavedMessagesStatus)
                batchBar.Hide();
            var current = SelectedMessage;
            bool wasCached = showingCachedFolder;
            int firstRow = messages.FirstDisplayedScrollingRowIndex;
            var scrollAnchor = backgroundRefresh && firstRow > 0 ? messages.Rows[firstRow].Tag as MailSummary : null;
            selected = current;
            if (mailbox is not null && string.IsNullOrWhiteSpace(query) && !IsPendingMoveSource())
                SetFolderCount(entry.Account, folder, folderMail.Count(mail => !mail.Read), folderMail.Count);
            SetCachedFolder(false);
            if (receivedBatch && cachedSearchMatches.Count == 0 && !mergedFirstBatch)
                SortPublishedRows();
            else
                RenderFolder(folderMail.Concat(cachedSearchMatches).DistinctBy(mail => mail.Id)
                    .Where(IsVisibleDuringMove).Select(mail => ApplyFlagOverrides(mail)).ToArray(), selected);
            if (scrollAnchor is not null)
            {
                var row = messages.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => row.Tag is MailSummary mail && mail.Id == scrollAnchor.Id);
                if (row is not null)
                    TryScrollMessagesTo(row.Index);
            }
            loadedUnfilteredFolder = string.IsNullOrWhiteSpace(query) ? entry : null;
            var displayed = SelectedMessage;
            bool keepPreview = !seededSearch && current?.Id == displayed?.Id && (receivedBatch || backgroundRefresh && !wasCached);
            var preview = keepPreview ? Task.CompletedTask : ShowPreview();
            if (connectionKey is not null && !IsPendingMoveSource())
            {
                var cacheMail = flagOverrides.Count > 0
                    ? folderMail.Select(mail => ApplyFlagOverrides(mail, confirmedOnly: true)).ToArray()
                    : folderMail;
                WriteCachedFolder(entry, folder, connectionKey, cacheMail);
                QueueBackgroundCache(entry, cacheMail);
            }
            await Task.WhenAll(preview, RefreshFolderCounts(entry.Account, mailbox, refreshCounts, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && !IsDisposed)
            {
                await cachedSearch;
                if (token.IsCancellationRequested || IsDisposed)
                    return;
                ShowFolderLoadError(MailOperationError(error,
                    receivedBatch ? "Could not load the remaining messages." : "Could not load the folder."));
            }
        }
        finally
        {
            EndBatchActivity(activity);
            if (ReferenceEquals(folderLoad, load))
            {
                folderLoad = null;
                folderLoadFlagOverrides = null;
                if (!IsDisposed)
                    searchStatus.Hide();
            }
        }

        async Task PublishCachedSearchAsync(LocalDatabase store, string key, string query)
        {
            IReadOnlyList<MailSummary> cached;
            try
            {
                cached = await Task.Run(() => store.SearchFolderCache(entry.Account.Id, folder, key, query, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            {
                cached = [];
            }

            if (token.IsCancellationRequested || IsDisposed || SelectedFolder != entry)
                return;

            cachedSearchMatches = cached;
            if (receivedBatch)
                return;

            var rows = cached.Where(IsVisibleDuringMove).Select(mail => ApplyFlagOverrides(mail)).ToArray();
            seededSearch = rows.Length > 0;
            SetCachedFolder(seededSearch);
            foreach (var mail in rows)
                publishedIds.Add(mail.Id);
            RenderFolder(rows, SelectedMessage);
            _ = ShowPreview();
        }

        void PublishBatch(IReadOnlyList<MailSummary> batch)
        {
            if (token.IsCancellationRequested || IsDisposed || SelectedFolder != entry || mergedFirstBatch)
                return;
            var newRows = batch.Where(mail => IsVisibleDuringMove(mail) && publishedIds.Add(mail.Id))
                .Select(mail => ApplyFlagOverrides(mail)).ToArray();
            if (!receivedBatch)
            {
                receivedBatch = true;
                if (!seededSearch)
                {
                    if (prioritizeCurrentFolder && loadedUnfilteredFolder == entry &&
                        string.IsNullOrWhiteSpace(search.Text) && messages.Rows.Count > 0)
                    {
                        mergedFirstBatch = true;
                        var existing = messages.Rows.Cast<DataGridViewRow>().Select(row => (MailSummary)row.Tag!);
                        RenderFolder(newRows.Concat(existing).DistinctBy(mail => mail.Id).ToArray(), SelectedMessage);
                        return;
                    }
                    SetCachedFolder(false);
                    RenderFolder(newRows, selected);
                    _ = ShowPreview();
                    return;
                }
            }

            rebuildingRows = true;
            try
            {
                foreach (var mail in SortFolder(newRows))
                    AddMessageRow(mail);
                if (!selectionMade)
                    messages.ClearSelection();
            }
            finally
            {
                rebuildingRows = false;
            }
        }

        bool IsPendingMoveSource() =>
            mailbox is not null && pendingMoves.ContainsKey((entry, mailbox));

        bool IsVisibleDuringMove(MailSummary mail) =>
            mailbox is null || !pendingMoves.TryGetValue((entry, mailbox), out var ids) || !ids.Contains(mail.Id);

        void ShowFolderLoadError(string message)
        {
            if (showingCachedFolder)
            {
                if (previewContent is null && SelectedMessage is not null)
                    SetPreviewBody("This message body is not saved on this computer. Connect to the mail server to read it.");
                if (!backgroundRefresh && !updatingMessageFolders)
                    ShowBatchStatus(SavedMessagesStatus);
            }
            else if (!backgroundRefresh)
                ThemedPopup.Show(this, message, Text);
        }

        MailSummary ApplyFlagOverrides(MailSummary mail, bool confirmedOnly = false)
        {
            if (flagOverrides.TryGetValue((mail.Id, true), out bool read) && CanApply(true))
                mail = mail with { Read = read };
            if (flagOverrides.TryGetValue((mail.Id, false), out bool star) && CanApply(false))
                mail = mail with { Star = star };
            return mail;

            bool CanApply(bool readFlag) => !confirmedOnly || mailbox is null ||
                !flagUpdates.TryGetValue((mailbox, mail.Id, readFlag), out var update) || update.Save.IsCompletedSuccessfully;
        }
    }

    private void TryScrollMessagesTo(int index)
    {
        if (!messages.Visible || index < 0 || index >= messages.Rows.Count ||
            messages.DisplayedRowCount(includePartialRow: true) == 0 ||
            messages.Rows[index] is not { Visible: true, Frozen: false })
            return;

        try
        {
            messages.FirstDisplayedScrollingRowIndex = index;
        }
        catch (InvalidOperationException)
        {
            // WinForms can reject scrolling while the grid is being laid out.
        }
    }

    private void SortPublishedRows(bool preserveScrollAnchor = true)
    {
        var rows = messages.Rows.Cast<DataGridViewRow>().ToArray();
        var ordered = SortFolder(rows.Select(row => (MailSummary)row.Tag!).ToArray()).ToArray();
        if (rows.Select(row => ((MailSummary)row.Tag!).Id).SequenceEqual(ordered.Select(mail => mail.Id)))
            return;

        var selected = selectionMade ? rows.Where(row => row.Selected).ToArray() : [];
        var current = messages.CurrentCell;
        int firstRow = messages.FirstDisplayedScrollingRowIndex;
        var top = preserveScrollAnchor && firstRow >= 0 ? rows[firstRow] : null;
        var positions = ordered.Select((mail, index) => (mail.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        rebuildingRows = true;
        try
        {
            messages.Sort(new PublishedRowOrder(positions));
            messages.CurrentCell = current;
            messages.ClearSelection();
            foreach (var row in selected)
                row.Selected = true;
            if (top is not null)
                TryScrollMessagesTo(top.Index);
        }
        finally
        {
            rebuildingRows = false;
        }
    }

    private sealed class PublishedRowOrder(Dictionary<Guid, int> positions) : System.Collections.IComparer
    {
        public int Compare(object? left, object? right)
        {
            var first = (MailSummary)((DataGridViewRow)left!).Tag!;
            var second = (MailSummary)((DataGridViewRow)right!).Tag!;
            return positions[first.Id].CompareTo(positions[second.Id]);
        }
    }

    private void SetCachedFolder(bool cached)
    {
        showingCachedFolder = cached;
    }

    private IReadOnlyList<MailSummary> ReadCachedFolder(FolderEntry entry, string folder, string connectionKey)
    {
        try
        {
            return database!.LoadFolderCache(entry.Account.Id, folder, connectionKey);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void WriteCachedFolder(FolderEntry entry, string folder, string connectionKey, IReadOnlyList<MailSummary> summaries)
    {
        try
        {
            database!.SaveFolderCache(entry.Account.Id, folder, connectionKey, summaries);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A cache write failure does not prevent the live folder from working.
        }
    }

    private void WriteCachedBody(MailAccount account, MailSummary summary, string connectionKey, MailContent content)
    {
        try
        {
            database!.SaveMessageBody(account.Id, summary.Folder, connectionKey, summary.Id, content);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // The message remains available from the server.
        }
    }

    private void SetFolderCount(MailAccount account, string folder, int unread, int total)
    {
        var count = account.Counts.GetValueOrDefault(folder);
        var updated = new FolderCount(Math.Clamp(unread, 0, total), total, count?.LabelName);
        if (count == updated)
            return;

        account.Counts = new Dictionary<string, FolderCount>(account.Counts) { [folder] = updated };
        folders.Invalidate();
    }

    private void AdjustFolderCount(MailAccount account, string folder, int unreadDelta, int totalDelta)
    {
        if (account.Counts.GetValueOrDefault(folder) is { HasCounts: true } count)
            SetFolderCount(account, folder, count.Unread + unreadDelta, Math.Max(0, count.Total + totalDelta));
    }

    private readonly Dictionary<(MailAccount, IMailbox), Task> folderCountRefreshes = new();

    private async Task RefreshFolderCounts(MailAccount account, IMailbox? mailbox, bool force, CancellationToken token)
    {
        if (mailbox is null || token.IsCancellationRequested ||
            (!force && DateTimeOffset.UtcNow - account.CountsUpdatedAt < TimeSpan.FromSeconds(30)))
            return;

        var key = (account, mailbox);
        if (!folderCountRefreshes.TryGetValue(key, out var refresh))
        {
            refresh = RefreshFolderCountsCoreAsync(account, mailbox, mailboxLifetime.Token);
            folderCountRefreshes.Add(key, refresh);
        }
        try
        {
            await refresh.WaitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RefreshFolderCountsCoreAsync(MailAccount account, IMailbox mailbox, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            var counts = await mailbox.GetFolderCountsAsync(token);
            if (!IsDisposed && !token.IsCancellationRequested && accounts.Contains(account) && ReferenceEquals(account.Mailbox, mailbox))
            {
                account.CountsUpdatedAt = DateTimeOffset.UtcNow;
                UpdateSidebarCounts(account, counts);
                if (database is not null && mailbox is ServerMailbox)
                {
                    string key = account.ConnectionKey();
                    await Task.Run(() => database.SaveCachedFolderCounts(account.Id, key, counts));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Keep the last counts if STATUS fails. Folder contents remain usable.
        }
        finally
        {
            folderCountRefreshes.Remove((account, mailbox));
        }
    }

    private IEnumerable<MailSummary> SortFolder(IReadOnlyList<MailSummary> folderMail) => sortColumn switch
        {
            0 => Sort(folderMail, mail => mail.Read),
            1 => Sort(folderMail, mail => mail.Star),
            2 => Sort(folderMail, mail => mail.Attachments),
            3 => Sort(folderMail, mail => mail.Subject),
            4 => Sort(folderMail, GridCorrespondents),
            _ => descending ? folderMail.OrderByDescending(mail => mail.Date) : folderMail.OrderBy(mail => mail.Date)
        };

    private void RenderFolder(IReadOnlyList<MailSummary> folderMail, MailSummary? selected)
    {
        var selectedIds = selectionMade
            ? messages.SelectedRows.Cast<DataGridViewRow>().Select(row => ((MailSummary)row.Tag!).Id).ToHashSet()
            : new HashSet<Guid>();
        if (selectedIds.Count == 0 && selected is not null)
            selectedIds.Add(selected.Id);

        rebuildingRows = true;
        messages.Rows.Clear();
        var selectedRows = new List<DataGridViewRow>();
        foreach (var mail in SortFolder(folderMail))
        {
            int index = AddMessageRow(mail);
            if (selectedIds.Contains(mail.Id))
                selectedRows.Add(messages.Rows[index]);
        }

        messages.Columns[sortColumn].HeaderCell.SortGlyphDirection =
            descending ? SortOrder.Descending : SortOrder.Ascending;
        messages.CurrentCell = selectedRows.Count > 0 ? selectedRows[0].Cells[3] : null;
        messages.ClearSelection();
        foreach (var row in selectedRows)
            row.Selected = true;
        selectionMade = selectedRows.Count > 0;
        selectedMessageId = SelectedMessage?.Id;

                rebuildingRows = false;
    }

    private string GridCorrespondents(MailSummary mail) =>
        mail.GetCorrespondentAddresses(SelectedFolder?.Account.Email.Trim());

    private int AddMessageRow(MailSummary mail, int? insertAt = null)
    {
        string correspondents = GridCorrespondents(mail);
        object[] values = [mail.Read, mail.Star, mail.Attachments, GridSubject(mail),
            correspondents, mail.Date.ToString(DateFormat)];
        int index;
        if (insertAt is { } position && position < messages.Rows.Count)
        {
            messages.Rows.Insert(position, values);
            index = position;
        }
        else
        {
            index = messages.Rows.Add(values);
        }
        var row = messages.Rows[index];
        row.Tag = mail;
        row.Cells[4].ToolTipText = correspondents;
        row.DefaultCellStyle.Font = mail.Read ? null : unreadFont;
        return index;
    }

    private static string GridSubject(MailSummary mail) =>
        (mail.Replied ? "↩︎ " : "") + (mail.Forwarded ? "↪︎ " : "") + mail.Subject;

    private sealed class FlagUpdateBaseline(bool value)
    {
        public bool ConfirmedValue { get; set; } = value;
    }

    private readonly Dictionary<(IMailbox Mailbox, Guid Id, bool ReadFlag),
        (Task Save, bool Value, FlagUpdateBaseline Baseline)> flagUpdates = new();

    private void UpdateFlag(DataGridViewCellEventArgs e)
    {
        if (rebuildingRows || e.RowIndex < 0 || e.ColumnIndex is not (0 or 1))
            return;

        var row = messages.Rows[e.RowIndex];
        if (SelectedFolder is not { Account.Mailbox: { } mailbox } entry || row.Tag is not MailSummary mail || row.Cells[e.ColumnIndex].Value is not bool value ||
            value == (e.ColumnIndex == 0 ? mail.Read : mail.Star))
            return;

        bool readFlag = e.ColumnIndex == 0;
        var targets = row.Selected && selectionMade
            ? messages.SelectedRows.Cast<DataGridViewRow>().ToArray()
            : new[] { row };
        foreach (var target in targets)
        {
            if (target.Tag is MailSummary summary && value != (readFlag ? summary.Read : summary.Star))
                _ = SaveMessageFlagAsync(entry, mailbox, target, summary, readFlag, value);
        }
    }

    private async Task SaveMessageFlagAsync(FolderEntry entry, IMailbox mailbox, DataGridViewRow row,
        MailSummary mail, bool readFlag, bool value)
    {
        var updated = readFlag ? mail with { Read = value } : mail with { Star = value };
        row.Tag = updated;
        row.Cells[readFlag ? 0 : 1].Value = value;
        row.DefaultCellStyle.Font = updated.Read ? null : unreadFont;
        if (folderLoadFlagOverrides is { } overrides)
            overrides[(mail.Id, readFlag)] = value;
        var key = (mailbox, mail.Id, readFlag);
        flagUpdates.TryGetValue(key, out var previous);
        var baseline = previous.Baseline ?? new FlagUpdateBaseline(readFlag ? mail.Read : mail.Star);
        string? connectionKey = database is not null && mailbox is ServerMailbox ? entry.Account.ConnectionKey() : null;
        var token = mailboxLifetime.Token;
        var update = SaveFlagAsync(previous.Save);
        flagUpdates[key] = (update, value, baseline);
        try
        {
            await update;
            if (readFlag && !IsDisposed && ReferenceEquals(entry.Account.Mailbox, mailbox) && IsLatestUpdate())
                _ = RefreshFolderCounts(entry.Account, mailbox, true, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsDisposed && IsLatestUpdate())
            {
                flagUpdates.Remove(key);
                bool originalFolderOpen = SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox);
                if (originalFolderOpen)
                {
                    if (folderLoadFlagOverrides is { } pendingOverrides)
                        pendingOverrides[(mail.Id, readFlag)] = baseline.ConfirmedValue;

                    var visibleRow = messages.Rows.Cast<DataGridViewRow>()
                        .FirstOrDefault(candidate => candidate.Tag is MailSummary summary && summary.Id == mail.Id);
                    if (visibleRow?.Tag is MailSummary visibleMail)
                    {
                        var restored = readFlag ? visibleMail with { Read = baseline.ConfirmedValue } :
                            visibleMail with { Star = baseline.ConfirmedValue };
                        visibleRow.Tag = restored;
                        visibleRow.Cells[readFlag ? 0 : 1].Value = baseline.ConfirmedValue;
                        visibleRow.DefaultCellStyle.Font = restored.Read ? null : unreadFont;
                    }
                }
                ThemedPopup.Show(this, MailOperationError(error, "Could not update the message flag."), Text);
                if (SelectedFolder == entry && ReferenceEquals(entry.Account.Mailbox, mailbox))
                    await ShowFolder(SelectedMessage, backgroundRefresh: true);
            }
        }
        finally
        {
            if (IsLatestUpdate())
                flagUpdates.Remove(key);
        }

        bool IsLatestUpdate() => flagUpdates.TryGetValue(key, out var latest) && latest.Save == update;

        async Task SaveFlagAsync(Task? preceding)
        {
            // Exit the grid commit event before reporting a failure or refreshing counts.
            await Task.Yield();
            if (preceding is not null)
            {
                try { await preceding; }
                catch (Exception) { } // A later click still needs to send its own value.
            }
            token.ThrowIfCancellationRequested();
            await mailbox.UpdateFlagsAsync(mail.Folder, mail.Id, readFlag ? value : null, readFlag ? null : value, token);
            baseline.ConfirmedValue = value;
            if (connectionKey is not null)
            {
                try
                {
                    await Task.Run(() => database!.UpdateCachedMessageFlag(
                        entry.Account.Id, mail.Folder, connectionKey, mail.Id, readFlag, value));
                }
                catch (Microsoft.Data.Sqlite.SqliteException) { }
            }
        }
    }

    private void PaintSortIndicator(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (ThemedPopup.UseDarkTheme != true || e.RowIndex != -1 || e.ColumnIndex < 0 ||
            e.Graphics is not { } graphics || e.CellStyle is not { } style)
            return;

        var direction = messages.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection;
        if (direction == SortOrder.None)
            return;

        var padding = style.Padding;
        var bounds = Rectangle.FromLTRB(e.CellBounds.Left + padding.Left + 2,
            e.CellBounds.Top, e.CellBounds.Right - padding.Right - 2, e.CellBounds.Bottom);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;
        var font = style.Font ?? messages.Font;
        var arrowFont = messages.ColumnHeadersDefaultCellStyle.Font ?? messages.Font;
        var ink = style.ForeColor;
        string arrow = direction == SortOrder.Ascending ? "▲" : "▼";
        int arrowWidth = TextRenderer.MeasureText(graphics, arrow, arrowFont, Size.Empty, flags).Width;
        if (bounds.Width < arrowWidth)
            return;

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        var state = graphics.Save();
        try
        {
            graphics.IntersectClip(Rectangle.Intersect(e.CellBounds, e.ClipBounds));
            var arrowBounds = new Rectangle(bounds.Right - arrowWidth, bounds.Top, arrowWidth, bounds.Height);
            TextRenderer.DrawText(graphics, arrow, arrowFont, arrowBounds, ink, flags);
            bounds.Width -= arrowWidth + padding.Right;
            if (bounds.Width > 0)
            {
                if (style.Alignment == DataGridViewContentAlignment.MiddleCenter)
                    flags |= TextFormatFlags.HorizontalCenter;
                TextRenderer.DrawText(graphics, e.FormattedValue as string ?? "", font,
                    bounds, ink, flags | TextFormatFlags.EndEllipsis);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
        e.Handled = true;
    }

    private void PaintFlagIcon(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex is not (1 or 2) ||
            e.Value is not bool enabled || e.Graphics is null || e.CellStyle is null)
            return;

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        string icon = e.ColumnIndex == 1 ? (enabled ? "★" : "☆") : (enabled ? "📎" : "");
        if (icon.Length > 0)
        {
            Color ink = (e.State & DataGridViewElementStates.Selected) != 0
                ? e.CellStyle.SelectionForeColor
                : e.CellStyle.ForeColor;
            var clip = Rectangle.Intersect(e.CellBounds, e.ClipBounds);
            if (!clip.IsEmpty)
            {
                var state = e.Graphics.Save();
                try
                {
                    e.Graphics.IntersectClip(clip);
                    TextRenderer.DrawText(e.Graphics, icon, flagIconFont, e.CellBounds, ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
                }
                finally
                {
                    e.Graphics.Restore(state);
                }
            }
        }

        e.Handled = true;
    }

    private static bool IsQuoted(string query) =>
        query.Length >= 2 && query[0] == '"' && query[^1] == '"';

    private IEnumerable<MailSummary> Sort<TKey>(IEnumerable<MailSummary> mail, Func<MailSummary, TKey> key)
    {
        var ordered = descending ? mail.OrderByDescending(key) : mail.OrderBy(key);
        return ordered.ThenByDescending(item => item.Date);
    }

    private async Task SelectCurrentMessage()
    {
        if (rebuildingRows)
            return;

        selectionMade = messages.SelectedRows.Count > 0;
        var id = SelectedMessage?.Id;
        if (id == selectedMessageId)
            return;
        selectedMessageId = id;
        await ShowPreview();
    }

    private async Task ShowPreview()
    {
        previewLoad?.Cancel();
        ResetPreviewAttachments();
        int request = ++previewRequest;
        previewContent = null;
        htmlPreview?.ClearDisplay();
        editDraft.Hide();
        moveMail.Enabled = deleteMail.Enabled = false;
        reply.Enabled = replyAll.Enabled = forward.Enabled = false;
        var selected = SelectedMessage;
        UpdateImagePreview(selected);
        if (SelectedFolder is not { Account.Mailbox: { } mailbox } entry || selected is not { } mail)
        {
            mailPane.Panel2Collapsed = true;
            previewSubject.Text = "";
            previewDetails.Text = "";
            previewBody.Clear();
            return;
        }
        mailPane.Panel2Collapsed = false;
        editDraft.Visible = entry.Folder == "Drafts" && !showingCachedFolder;
        moveMail.Enabled = deleteMail.Enabled = true;

        previewSubject.Text = mail.Subject;
        var details = new List<string>();
        if (mail.HasPreviewHeaders)
        {
            details.Add($"From: {mail.FromAddress}");
            details.Add($"To: {mail.ToAddresses}");
            if (mail.CcAddresses.Length > 0)
                details.Add($"Cc: {mail.CcAddresses}");
        }
        details.Add(mail.Date.ToString(DateFormat));
        if (mail.Attachments)
            details.Add("Has attachments");
        previewDetails.Text = string.Join("  ·  ", details);
        if (mail.Attachments && !showingCachedFolder)
            ShowPreviewAttachmentsLoading();

        string? connectionKey = database is not null && mailbox is ServerMailbox ? entry.Account.ConnectionKey() : null;
        bool displayedCachedBody = false;
        if (connectionKey is not null)
        {
            try
            {
                if (database!.LoadMessageBody(entry.Account.Id, mail.Folder, connectionKey, mail.Id) is { } cached &&
                    (!string.IsNullOrWhiteSpace(cached.Body) || !string.IsNullOrWhiteSpace(cached.Html)))
                {
                    DisplayMessageBody(mail, cached);
                    displayedCachedBody = true;
                    // Empty HTML marks a known plain-text message. NULL belongs to older cache entries.
                    if (showingCachedFolder ||
                        !mail.HasStandaloneImages && cached.Html is { } html && MailContent.ReferencedContentIds(html).Count == 0 &&
                        MailContent.ReferencedImageSources(html).Count == 0)
                    {
                        if (mail.Attachments && !showingCachedFolder)
                            _ = LoadPreviewAttachmentsAsync(mailbox, mail.Id, request);
                        return;
                    }
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // The preview can still use the server if the cache is unavailable.
            }
        }

        if (showingCachedFolder)
        {
            SetPreviewBody("This message body is not saved on this computer. Connect to the mail server to read it.");
            return;
        }

        if (!displayedCachedBody)
            SetPreviewBody("Loading...", italic: true);
        using var load = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        previewLoad = load;
        try
        {
            var content = await mailbox.LoadContentAsync(mail.Id, load.Token);
            if (request != previewRequest || IsDisposed)
                return;

            DisplayMessageBody(mail, content);
            if (content.AttachmentList is { } attachments)
                DisplayPreviewAttachments(attachments);
            else if (mail.Attachments)
                _ = LoadPreviewAttachmentsAsync(mailbox, mail.Id, request);
            if (connectionKey is not null && !content.PreviewTooLarge)
                WriteCachedBody(entry.Account, mail, connectionKey, content);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!displayedCachedBody && request == previewRequest && !IsDisposed)
                SetPreviewBody(MailOperationError(error, "Could not load the message."));
            if (mail.Attachments && request == previewRequest && !IsDisposed)
                _ = LoadPreviewAttachmentsAsync(mailbox, mail.Id, request);
        }
        finally
        {
            if (ReferenceEquals(previewLoad, load))
                previewLoad = null;
        }
    }

    private void DisplayMessageBody(MailSummary mail, MailContent content)
    {
        var previous = previewContent;
        previewContent = content;
        bool showImagesForMessage = imagePermission == mail.Id;
        if (string.IsNullOrEmpty(content.Html))
        {
            if (previous is null || !string.IsNullOrEmpty(previous.Html) || previous.Body != content.Body ||
                previous.PreviewTooLarge != content.PreviewTooLarge)
                SetPreviewBody(content.PreviewTooLarge ? "This message body is too large to preview." :
                    string.IsNullOrWhiteSpace(content.Body) ? "No message body was available to preview." : content.Body);
        }
        else if (content.Html != previous?.Html || previous?.Body != content.Body && previewBody.Visible ||
            showImagesForMessage && !(content.InlineImages ?? []).SequenceEqual(previous?.InlineImages ?? []))
        {
            _ = DisplayHtmlBodyAsync(content, previewRequest, showImagesForMessage);
        }
        reply.Enabled = replyAll.Enabled = forward.Enabled = !showingCachedFolder;
        UpdateImagePreview(mail);
    }

    private async Task DisplayHtmlBodyAsync(MailContent content, int request, bool showRemoteImages)
    {
        int attempt = ++htmlDisplayAttempt;
        if (string.IsNullOrEmpty(content.Html))
        {
            SetPreviewBody(content.Body);
            return;
        }

        if (htmlPreview is null)
        {
            htmlPreview = new HtmlMailPreview
            {
                Dock = DockStyle.Fill, Visible = false, AccessibleName = "HTML email body",
                UseDarkScrollbars = darkTitleBar
            };
            htmlPreview.KeyDown += (_, e) =>
            {
                if (e.KeyData == (Keys.Control | Keys.F) && FocusFolderSearch())
                {
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyData != Keys.Delete)
                    return;
                e.SuppressKeyPress = true;
                this.PostIfHandleAvailable(() => _ = DeleteSelectedMessages());
            };
            preview.Controls.Add(htmlPreview);
            preview.Controls.SetChildIndex(htmlPreview, preview.Controls.GetChildIndex(previewBody));
        }
        SetPreviewBody("Loading...", italic: true);
        bool loaded = await htmlPreview.DisplayAsync(content, showRemoteImages);
        if (IsDisposed || request != previewRequest || attempt != htmlDisplayAttempt ||
            previewContent is not { Html.Length: > 0 } currentContent)
            return;

        if (loaded)
            previewBody.Hide();
        else
        {
            SetPreviewBody(string.IsNullOrWhiteSpace(currentContent.Body)
                ? "The HTML body could not be displayed. Try Show images if the message uses remote content."
                : currentContent.Body);
            if (showRemoteImages)
            {
                imagePermission = null;
                UpdateImagePreview(SelectedMessage);
            }
        }
    }

    private void SetPreviewBody(string text, bool italic = false)
    {
        htmlPreview?.ClearDisplay();
        previewBody.Show();
        previewBody.Clear();
        using var italicFont = italic ? new Font(previewBody.Font, previewBody.Font.Style | FontStyle.Italic) : null;
        previewBody.SelectionFont = italicFont ?? previewBody.Font;
        previewBody.SelectedText = text;
        previewBody.Select(0, 0);
    }

    private void UpdateImagePreview(MailSummary? mail)
    {
        if (imagePermission != mail?.Id)
            imagePermission = null;

        bool hasHtml = mail is not null && previewContent?.Html is { Length: > 0 };
        imageActionSeparator.Visible = showImages.Visible = hasHtml && imagePermission is null;
    }
}









