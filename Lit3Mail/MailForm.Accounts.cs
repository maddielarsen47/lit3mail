using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using static Lit3Mail.MailAccountValidation;

namespace Lit3Mail;

public partial class MailForm
{
    private sealed class MailAccount(string name, string email, IMailbox? mailbox)
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = name;
        public string ProfileName { get; set; } = name;
        public bool ProfileUsesAccountName { get; set; } = true;
        public bool IncomingUsesEmail { get; set; } = true;
        public bool OutgoingUsesEmail { get; set; } = true;
        public bool DesktopNotifications { get; set; } = true;
        public string Email { get; set; } = email;
        public string ReplyTo { get; set; } = "";
        public string Organisation { get; set; } = "";
        public string Signature { get; set; } = "";
        public string IncomingHost { get; set; } = MailDefaults.IncomingHost;
        public string IncomingUser { get; set; } = "";
        public int IncomingPort { get; set; } = 993;
        public string OutgoingHost { get; set; } = MailDefaults.OutgoingHost;
        public string OutgoingUser { get; set; } = "";
        public int OutgoingPort { get; set; } = 465;
        public bool IncomingStartTls { get; set; }
        public bool OutgoingStartTls { get; set; }
        public IMailbox? Mailbox { get; set; } = mailbox;
        public bool HasSavedSignIn { get; set; }

        private static string Username(string user, string email) =>
            string.IsNullOrWhiteSpace(user) ? email.Trim() : user.Trim();

        public string EffectiveIncomingUser => Username(IncomingUsesEmail ? Email : IncomingUser, Email);
        public string EffectiveOutgoingUser => Username(OutgoingUsesEmail ? Email : OutgoingUser, Email);

        public bool RequiresOutlookSignIn =>
            MailAccountValidation.RequiresOutlookSignIn(Email, IncomingHost, EffectiveIncomingUser, OutgoingHost, EffectiveOutgoingUser);

        public (Field Field, string Message)? ValidateConnection() =>
            MailAccountValidation.Validate(Email, IncomingHost, IncomingPort, EffectiveIncomingUser, OutgoingHost, OutgoingPort, EffectiveOutgoingUser);

        public string ConnectionKey() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Email = Email.Trim(),
            IncomingHost = IncomingHost.Trim(),
            IncomingPort,
            IncomingUser = EffectiveIncomingUser,
            IncomingStartTls,
            OutgoingHost = OutgoingHost.Trim(),
            OutgoingPort,
            OutgoingUser = EffectiveOutgoingUser,
            OutgoingStartTls
        })));

        public ServerMailbox CreateMailbox(SavedSignIn signIn) =>
            new ServerMailbox(
                new(IncomingHost.Trim(), IncomingPort, EffectiveIncomingUser, signIn.IncomingPassword, IncomingStartTls),
                new(OutgoingHost.Trim(), OutgoingPort, EffectiveOutgoingUser, signIn.OutgoingPassword, OutgoingStartTls));
        public bool Expanded { get; set; } = true;
        private string EffectiveProfileName => ProfileUsesAccountName ? Name : ProfileName;
        public string DisplayName => string.IsNullOrWhiteSpace(EffectiveProfileName) ? "Unnamed account" : EffectiveProfileName.Trim();
        public DateTimeOffset CountsUpdatedAt { get; set; }
        public IReadOnlyDictionary<string, FolderCount> Counts { get; set; } = new Dictionary<string, FolderCount>();
    }

    private sealed record FolderEntry(MailAccount Account, string? Folder)
    {
        public string? FolderTitle => Folder is { } folder && Account.Counts.GetValueOrDefault(folder)?.LabelName is { } label
            ? $"* {label}" : Folder;

        public override string ToString() => Folder is null
            ? $"{Account.DisplayName}, {(Account.Expanded ? "expanded" : "collapsed")}"
            : $"{Account.DisplayName}, {FolderTitle}";
    }

    private static readonly string[] DefaultFolders = ["Inbox", "Sent", "Drafts", "Archive", "Trash"];
    private readonly List<MailAccount> accounts = [];
    private readonly LocalDatabase? database;
    private Dictionary<Guid, AccountSettings> savedAccounts = new();

    private void LoadAccountSettings(LocalDatabase database)
    {
        var settings = database.LoadAccounts();
        savedAccounts = settings.ToDictionary(account => account.Id);
        foreach (var account in settings)
        {
            var restored = new MailAccount(account.Name, account.Email, null)
            {
                Id = account.Id,
                ProfileName = account.ProfileName,
                ProfileUsesAccountName = account.ProfileUsesAccountName,
                IncomingUsesEmail = account.IncomingUsesEmail,
                OutgoingUsesEmail = account.OutgoingUsesEmail,
                DesktopNotifications = account.DesktopNotifications,
                ReplyTo = account.ReplyTo,
                Organisation = account.Organisation,
                Signature = account.Signature,
                IncomingHost = account.IncomingHost,
                IncomingPort = account.IncomingPort,
                IncomingUser = account.IncomingUser,
                IncomingStartTls = account.IncomingStartTls,
                OutgoingHost = account.OutgoingHost,
                OutgoingPort = account.OutgoingPort,
                OutgoingUser = account.OutgoingUser,
                OutgoingStartTls = account.OutgoingStartTls,
                Expanded = account.Expanded
            };
            try
            {
                var signIn = WindowsCredentialStore.Read(restored.Id);
                restored.HasSavedSignIn = signIn is not null;
                if (signIn?.ConnectionKey == restored.ConnectionKey() && restored.ValidateConnection() is null &&
                    !string.IsNullOrEmpty(signIn.IncomingPassword) && !string.IsNullOrEmpty(signIn.OutgoingPassword))
                    restored.Mailbox = restored.CreateMailbox(signIn);
            }
            catch (Exception error) when (error is Win32Exception or JsonException)
            {
                restored.HasSavedSignIn = true;
            }
            if (restored.Mailbox is ServerMailbox)
            {
                try
                {
                    restored.Counts = database.LoadCachedFolderCounts(restored.Id, restored.ConnectionKey());
                }
                catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or JsonException) { }
            }
            accounts.Add(restored);
        }
    }

    private bool SaveAccountSettings(bool showError = true)
    {
        if (database is null)
            return true;

        var current = accounts.Select((account, position) => new AccountSettings(
            account.Id, account.Name, account.Email, account.ReplyTo, account.Organisation, account.Signature,
            account.IncomingHost, account.IncomingPort, account.IncomingUser, account.IncomingStartTls,
            account.OutgoingHost, account.OutgoingPort, account.OutgoingUser, account.OutgoingStartTls, account.Expanded,
            position, account.ProfileName, account.ProfileUsesAccountName, account.IncomingUsesEmail, account.OutgoingUsesEmail,
            account.DesktopNotifications))
            .ToDictionary(account => account.Id);
        var updated = current.Values.Where(account =>
            !savedAccounts.TryGetValue(account.Id, out var previous) || previous != account).ToArray();
        var removed = savedAccounts.Keys.Except(current.Keys).ToArray();
        if (updated.Length == 0 && removed.Length == 0)
            return true;

        try
        {
            database.SaveAccounts(updated, removed);
            savedAccounts = current;
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            if (showError)
                ThemedPopup.Show(this, "Could not save account settings to the local database. Try again.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
    private readonly FlowLayoutPanel accountTabs = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, AllowDrop = true, AccessibleName = "Account tabs"
    };
    private const string AccountDragFormat = "Lit3Mail.AccountTab";
    private RadioButton? draggedAccountTab;
    private readonly Panel accountPages = new() { Dock = DockStyle.Fill, Padding = new Padding(1) };
    private readonly Button addAccount = new() { Text = "Add Account", Height = 36, FlatStyle = FlatStyle.Flat, };
    private bool rebuildingFolders;

    private FolderEntry? selectedFolder;
    private FolderEntry? SelectedFolder => selectedFolder;

    private void RebuildFolders(MailAccount? preferred, string? folder, bool preserveMessageList = false)
    {
        if (!preserveMessageList && folder is not null && accounts.Count > 0)
        {
            preferred ??= accounts.FirstOrDefault(item => item.Expanded) ?? accounts[0];
            preferred.Expanded = true;
        }

        rebuildingFolders = true;
        folders.BeginUpdate();
        folders.Items.Clear();
        foreach (var account in accounts)
        {
            folders.Items.Add(new FolderEntry(account, null));
            if (account.Expanded)
            {
                foreach (string name in DefaultFolders)
                    folders.Items.Add(new FolderEntry(account, name));
                foreach (var label in LabelFolders(account.Counts))
                    folders.Items.Add(new FolderEntry(account, label.Key));
            }
        }
        if (preserveMessageList && preferred is not null)
            folders.SelectedItem = new FolderEntry(preferred, preferred.Expanded ? folder : null);
        folders.EndUpdate();
        rebuildingFolders = false;
        if (preserveMessageList)
            return;

        if (accounts.Count > 0)
        {
            var account = preferred ?? accounts.FirstOrDefault(item => item.Expanded) ?? accounts[0];
            folders.SelectedItem = new FolderEntry(account, folder);
        }
        else
        {
            selectedFolder = null;
            newEmail.Enabled = false;
            folderLoad?.Cancel();
            searchStatus.Hide();
            selectionMade = false;
            selectedMessageId = null;
            SetCachedFolder(false);
            messages.Rows.Clear();
            _ = ShowPreview();
        }
    }

    private void ToggleAccountFolders(MailAccount account)
    {
        account.Expanded = !account.Expanded;
        RebuildFolders(SelectedFolder?.Account ?? account, SelectedFolder?.Folder, preserveMessageList: true);
        if (account.Expanded && account.Mailbox is ServerMailbox { IsGmail: true } mailbox)
            _ = RefreshFolderCounts(account, mailbox, false, mailboxLifetime.Token);
    }

    private static IEnumerable<KeyValuePair<string, FolderCount>> LabelFolders(IReadOnlyDictionary<string, FolderCount> counts) =>
        counts.Where(item => item.Value.LabelName is not null)
            .OrderBy(item => item.Value.LabelName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal);

    private void UpdateSidebarCounts(MailAccount account, IReadOnlyDictionary<string, FolderCount> counts)
    {
        bool labelsChanged = !LabelFolders(account.Counts).Select(item => item.Key)
            .SequenceEqual(LabelFolders(counts).Select(item => item.Key));
        account.Counts = counts;
        if (account.Expanded && labelsChanged)
        {
            var selection = SelectedFolder;
            if (selection is { Folder: { } current } && selection.Account == account &&
                !DefaultFolders.Contains(current) && !counts.ContainsKey(current))
                RebuildFolders(account, "Inbox");
            else
                RebuildFolders(selection?.Account, selection?.Folder, preserveMessageList: true);
        }
        folders.Invalidate();
    }

    private async Task RefreshOtherGmailFoldersAsync(MailAccount? selected)
    {
        foreach (var account in accounts.Where(account => account.Expanded && account != selected).ToArray())
        {
            if (IsDisposed)
                return;
            if (account.Mailbox is ServerMailbox { IsGmail: true } mailbox)
                await RefreshFolderCounts(account, mailbox, false, mailboxLifetime.Token);
        }
    }

    private void SelectFolder(MailAccount account, string folder)
    {
        if (account.Expanded)
            folders.SelectedItem = new FolderEntry(account, folder);
        else
        {
            account.Expanded = true;
            RebuildFolders(account, folder);
        }
    }

    private void InitializeAccountSettings()
    {
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(backToMail);
        actions.Controls.Add(addAccount);
        settingsPage.Controls.Add(accountPages);
        settingsPage.Controls.Add(accountTabs);
        settingsPage.Controls.Add(actions);
        settingsPage.Controls.Add(settingsHeading);
        AddGeneralSettingsTab();
        foreach (var account in accounts)
            AddAccountTab(account);
        accountTabs.DragEnter += (_, e) => UpdateAccountDrop(e);
        accountTabs.DragOver += (_, e) => UpdateAccountDrop(e);
        accountTabs.DragDrop += (_, e) =>
        {
            if (draggedAccountTab is not { Tag: MailAccount account } tab || AccountDropIndex(e) is not { } index)
                return;

            accounts.Remove(account);
            accounts.Insert(index - 1, account);
            accountTabs.Controls.SetChildIndex(tab, index);
            var selection = folders.SelectedItem as FolderEntry;
            RebuildFolders(selection?.Account, selection?.Folder, preserveMessageList: true);
        };

        addAccount.Click += (_, _) =>
        {
            var account = new MailAccount("New account", "", null);
            var selected = SelectedFolder;
            accounts.Add(account);
            AddAccountTab(account);
            ((RadioButton)accountTabs.Controls[accountTabs.Controls.Count - 1]).Checked = true;
            RebuildFolders(selected?.Account ?? account, selected?.Folder ?? "Inbox");
            ApplyTheme();
        };
    }

    private void UpdateAccountDrop(DragEventArgs e) =>
        e.Effect = AccountDropIndex(e) is null ? DragDropEffects.None : DragDropEffects.Move;

    private int? AccountDropIndex(DragEventArgs e)
    {
        if (draggedAccountTab is null || e.Data?.GetDataPresent(AccountDragFormat, false) != true)
            return null;
        var point = accountTabs.PointToClient(new Point(e.X, e.Y));
        if (accountTabs.GetChildAtPoint(point) is not RadioButton { Tag: MailAccount } target)
            return null;

        int source = accountTabs.Controls.GetChildIndex(draggedAccountTab);
        int index = accountTabs.Controls.GetChildIndex(target);
        if (point.X >= target.Left + target.Width / 2)
            index++;
        if (index > source)
            index--;
        return index;
    }

    private (Panel Page, RadioButton Tab, TableLayoutPanel Fields) AddSettingsTab(string title)
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14), Visible = false
        };
        var tab = new RadioButton
        {
            Text = title, Appearance = Appearance.Button, AutoSize = true,
            Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 4, 3)
        };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(fields);
        accountPages.Controls.Add(page);
        accountTabs.Controls.Add(tab);
        tab.CheckedChanged += (_, _) => page.Visible = tab.Checked;
        tab.Checked = accountTabs.Controls.Count == 1;

        return (page, tab, fields);
    }

    private static void AddSettingsRow(TableLayoutPanel fields, string label, Control field)
    {
        int row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 8, 0) }, 0, row);
        field.Margin = new Padding(0, 2, 0, 8);
        fields.Controls.Add(field, 1, row);
    }

    private void AddGeneralSettingsTab()
    {
        var (_, _, fields) = AddSettingsTab("General");
        AddSettingsRow(fields, "App theme", theme);

        if (database is { } store)
        {
            var location = new TextBox { ReadOnly = true, AccessibleName = "Local data folder" };
            AddSettingsRow(fields, "Local data folder", CreateTextField(location));
            var changeFolder = new Button { Text = "Choose folder...", AutoSize = true };
            var resetFolder = new Button { Text = "Reset to default", AutoSize = true };
            var folderActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            folderActions.Controls.AddRange([changeFolder, resetFolder]);
            AddSettingsRow(fields, "", folderActions);
            var locationStatus = new Label { AutoSize = true };
            AddSettingsRow(fields, "", locationStatus);

            void RefreshDataFolder()
            {
                string? pending = store.PendingDataFolder;
                location.Text = pending ?? store.DataFolder;
                resetFolder.Enabled = !string.Equals(location.Text, LocalDatabase.DefaultFolder,
                    StringComparison.OrdinalIgnoreCase);
                locationStatus.Text = pending is null
                    ? "Shared by all accounts."
                    : "Shared by all accounts. Changes apply the next time Lit3Mail opens.";
            }

            RefreshDataFolder();
            changeFolder.Click += (_, _) =>
            {
                using var picker = new FolderBrowserDialog
                {
                    Description = "Choose the local data folder for all accounts",
                    UseDescriptionForTitle = true,
                    SelectedPath = location.Text
                };
                if (picker.ShowDialog(this) == DialogResult.OK)
                    SetDataFolder(picker.SelectedPath);
            };
            resetFolder.Click += (_, _) => SetDataFolder(LocalDatabase.DefaultFolder);

            void SetDataFolder(string folder)
            {
                try
                {
                    string? previousFolder = store.PendingDataFolder;
                    store.SetDataFolder(folder);
                    RefreshDataFolder();
                    if (store.PendingDataFolder is { } pending &&
                        !string.Equals(pending, previousFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        ThemedPopup.Show(this, "Restart Lit3Mail to apply the new local data folder.",
                            "Restart required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ThemedPopup.Show(this, error.Message, "Local data folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        string version = typeof(MailForm).Assembly.GetName().Version?.ToString(3) ?? Application.ProductVersion;
        AddSettingsRow(fields, "App version", new Label { AutoSize = true, Text = version });

        const string repositoryUrl = "https://github.com/maddielarsen47/lit3mail/";
        var repository = new LinkLabel { AutoSize = true, Text = repositoryUrl };
        repository.LinkClicked += (_, _) =>
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true });
            }
            catch (Exception credentialError) when (credentialError is Win32Exception or InvalidOperationException)
            {
                ThemedPopup.Show(this, "Could not open the link in your default browser.", "Lit3Mail");
            }
        };
        AddSettingsRow(fields, "Repository", repository);
    }

    private void AddAccountTab(MailAccount account)
    {
        var (page, tab, fields) = AddSettingsTab(account.DisplayName);
        tab.Tag = account;
        Point? dragStart = null;
        tab.MouseDown += (_, e) => dragStart = e.Button == MouseButtons.Left ? e.Location : null;
        tab.MouseUp += (_, _) => dragStart = null;
        tab.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || dragStart is not { } start)
                return;
            var threshold = SystemInformation.DragSize;
            if (Math.Abs(e.X - start.X) < threshold.Width / 2 &&
                Math.Abs(e.Y - start.Y) < threshold.Height / 2)
                return;

            dragStart = null;
            var data = new DataObject();
            data.SetData(AccountDragFormat, false, "move");
            draggedAccountTab = tab;
            try
            {
                tab.DoDragDrop(data, DragDropEffects.Move);
            }
            finally
            {
                draggedAccountTab = null;
            }
        };

        void AddRow(string label, Control field) => AddSettingsRow(fields, label, field);

        void AddSection(string title)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var headingFont = new Font(fields.Font.FontFamily, fields.Font.Size + 4, FontStyle.Bold);
            var heading = new Label
            {
                Text = title, AutoSize = true, Font = headingFont,
                Margin = new Padding(0, row == 0 ? 0 : 22, 0, 12)
            };
            heading.Disposed += (_, _) => headingFont.Dispose();
            fields.Controls.Add(heading, 0, row);
            fields.SetColumnSpan(heading, 2);
        }

        TextBox AddText(string label, string value, Action<string> save, bool multiline = false, bool password = false)
        {
            var input = new TextBox { AccessibleName = label, Text = value, Multiline = multiline, UseSystemPasswordChar = password };
            if (multiline)
                input.ScrollBars = ScrollBars.Vertical;
            input.TextChanged += (_, _) => save(input.Text);
            var frame = CreateTextField(input);
            if (multiline)
                frame.Height = 156;
            AddRow(label, frame);
            return input;
        }

        TextBox AddLinkedText(string label, TextBox source, string checkboxLabel, bool linked,
            Func<string> getValue, Action<string> save, Action<bool> saveLinked)
        {
            var input = AddText(label, linked ? source.Text : getValue(), value =>
            {
                if (!linked)
                    save(value);
            });
            var checkbox = new CheckBox { Text = checkboxLabel, AutoSize = true, Checked = linked };
            var readOnlyFont = new Font(input.Font, input.Font.Style | FontStyle.Italic);
            input.Disposed += (_, _) => readOnlyFont.Dispose();
            input.ReadOnly = linked;
            input.Font = linked ? readOnlyFont : null;
            checkbox.CheckedChanged += (_, _) =>
            {
                linked = checkbox.Checked;
                saveLinked(linked);
                input.ReadOnly = linked;
                input.Font = linked ? readOnlyFont : null;
                ApplyTheme();
                input.Text = linked ? source.Text : getValue();
            };
            source.TextChanged += (_, _) =>
            {
                if (linked)
                    input.Text = source.Text;
            };
            AddRow("", checkbox);
            return input;
        }

        NumericUpDown AddPort(string label, int value, Action<int> save)
        {
            var port = new NumericUpDown
            {
                AccessibleName = label, Minimum = 1, Maximum = 65535,
                Value = Math.Clamp(value, 1, 65535), Width = 100
            };
            if (port.Value != value)
                save((int)port.Value);
            port.MouseWheel += (_, e) =>
            {
                if (e is HandledMouseEventArgs wheel)
                    wheel.Handled = true;
            };
            port.ValueChanged += (_, _) => save((int)port.Value);
            AddRow(label, port);
            return port;
        }

        void RefreshProfileName()
        {
            tab.Text = account.DisplayName;
            folders.Invalidate();
        }

        AddSection("Account");
        var accountName = AddText("Account Name", account.Name, value =>
        {
            account.Name = value;
            RefreshProfileName();
        });
        AddLinkedText("Profile Name", accountName, "Same as Account Name", account.ProfileUsesAccountName,
            () => account.ProfileName, value =>
            {
                account.ProfileName = value;
                RefreshProfileName();
            }, linked =>
            {
                account.ProfileUsesAccountName = linked;
                RefreshProfileName();
            });
        var email = AddText("Email Address", account.Email, value => account.Email = value);
        AddText("Reply-To Address", account.ReplyTo, value => account.ReplyTo = value);
        AddText("Organisation", account.Organisation, value => account.Organisation = value);
        AddText("Signature Text/HTML", account.Signature, value => account.Signature = value, multiline: true);
        var notifications = new CheckBox
        {
            Text = "Notify me when new mail arrives", AutoSize = true, Checked = account.DesktopNotifications
        };
        notifications.CheckedChanged += (_, _) =>
        {
            account.DesktopNotifications = notifications.Checked;
            ResetDesktopNotificationCursor(account);
        };
        AddRow("Desktop notifications", notifications);

        AddSection("Connection");
        var connectionStateFont = new Font(Font, FontStyle.Bold);
        var connectionState = new Label { AutoSize = true, Font = connectionStateFont };
        connectionState.Disposed += (_, _) => connectionStateFont.Dispose();
        AddRow("Connection status", connectionState);
        var status = new Label
        {
            AutoSize = true,
            Text = account.RequiresOutlookSignIn ? OutlookSignInError : account.HasSavedSignIn
                ? account.Mailbox is null ? "Saved sign-in unavailable. Sign in again or forget it." : "Using a saved Windows sign-in."
                : ""
        };
        var gmailHelp = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "Enable 2-Step Verification (2FA) in your Google Account, then generate an App Password. " +
                "Enter that App Password in both password fields below."
        };
        AddRow("Gmail", gmailHelp);
        fields.SizeChanged += (_, _) => WrapGmailHelp();
        WrapGmailHelp();

        void WrapGmailHelp()
        {
            int width = Math.Max(1, fields.ClientSize.Width - (int)fields.ColumnStyles[0].Width);
            gmailHelp.MaximumSize = new Size(width, 0);
        }
        AddRow("", status);

        var imapServer = AddText("IMAP Server", account.IncomingHost, value => account.IncomingHost = value);
        var imapPort = AddPort("IMAP Port", account.IncomingPort, value => account.IncomingPort = value);
        var imapUser = AddLinkedText("IMAP Username", email, "Same as Email Address", account.IncomingUsesEmail,
            () => account.IncomingUser, value => account.IncomingUser = value, linked => account.IncomingUsesEmail = linked);

        const string passwordMask = "••••••••";
        var imapPassword = AddText("IMAP Password", account.Mailbox is null ? "" : passwordMask, _ => { }, password: true);
        var imapStartTls = new CheckBox { Text = "Use STARTTLS", AutoSize = true, Checked = account.IncomingStartTls };
        imapStartTls.CheckedChanged += (_, _) => account.IncomingStartTls = imapStartTls.Checked;
        AddRow("IMAP Security", imapStartTls);
        var smtpServer = AddText("SMTP Server", account.OutgoingHost, value => account.OutgoingHost = value);
        var smtpPort = AddPort("SMTP Port", account.OutgoingPort, value => account.OutgoingPort = value);
        var smtpUser = AddLinkedText("SMTP Username", email, "Same as Email Address", account.OutgoingUsesEmail,
            () => account.OutgoingUser, value => account.OutgoingUser = value, linked => account.OutgoingUsesEmail = linked);

        var smtpPassword = AddText("SMTP Password", account.Mailbox is null ? "" : passwordMask, _ => { }, password: true);
        var samePassword = new CheckBox { Text = "Same as IMAP Password", AutoSize = true, Checked = true };
        AddRow("", samePassword);
        smtpPassword.Enabled = !samePassword.Checked;
        samePassword.CheckedChanged += (_, _) => smtpPassword.Enabled = !samePassword.Checked;
        foreach (var input in new[] { imapPassword, smtpPassword })
            input.Enter += (_, _) =>
            {
                if (input.Text == passwordMask)
                    input.SelectAll();
            };
        var smtpStartTls = new CheckBox { Text = "Use STARTTLS", AutoSize = true, Checked = account.OutgoingStartTls };
        smtpStartTls.CheckedChanged += (_, _) => account.OutgoingStartTls = smtpStartTls.Checked;
        AddRow("SMTP Security", smtpStartTls);
        var connect = new Button { Text = "Connect to mail servers", AutoSize = true };
        AddRow("", connect);

        var forget = new Button { Text = "Forget saved sign-in", AutoSize = true, Enabled = account.HasSavedSignIn };
        AddRow("", forget);

        AddSection("Data");
        var remove = new Button { Text = "Remove Account", AutoSize = true, FlatStyle = FlatStyle.Flat };
        AddRow("", remove);

        string? activeKey = account.Mailbox is null ? null : account.ConnectionKey();

        SavedSignIn? sessionSignIn = null;

        void UpdateConnectionState()
        {
            connectionState.Text = account.Mailbox is null ? "Not connected" : $"Connected as {account.Email.Trim()}";
            connect.Text = account.Mailbox is null ? "Connect to mail servers" : "Reconnect to mail servers";
        }

        UpdateConnectionState();

        void ClearConnection()
        {
            CancelBackgroundCache(account);
            activeKey = null;
            account.Mailbox = null;
            sessionSignIn = null;
            if (imapPassword.Text == passwordMask) imapPassword.Clear();
            if (smtpPassword.Text == passwordMask) smtpPassword.Clear();
            UpdateConnectionState();
            account.Counts = new Dictionary<string, FolderCount>();
            account.CountsUpdatedAt = default;
            var selected = SelectedFolder;
            RebuildFolders(selected?.Account, selected?.Folder ?? "Inbox");
        }

        void ConnectionEdited(object? sender, EventArgs args)
        {
            if (activeKey is not null && activeKey != account.ConnectionKey())
            {
                ClearConnection();
                status.Text = "Connection settings changed. Sign in to use the new settings.";
            }
            if (account.RequiresOutlookSignIn)
                status.Text = OutlookSignInError;
            else if (status.Text == OutlookSignInError)
                status.Text = "";
        }

        foreach (var input in new[] { email, imapServer, imapUser, smtpServer, smtpUser })
            input.TextChanged += ConnectionEdited;
        imapPort.ValueChanged += ConnectionEdited;
        smtpPort.ValueChanged += ConnectionEdited;
        imapStartTls.CheckedChanged += ConnectionEdited;
        smtpStartTls.CheckedChanged += ConnectionEdited;

        bool ForgetSignIn()
        {
            try
            {
                if (database is not null)
                    WindowsCredentialStore.Delete(account.Id);
                account.HasSavedSignIn = false;
                return true;
            }
            catch (Win32Exception)
            {
                status.Text = "Windows could not delete this saved sign-in. Try again.";
                return false;
            }
        }

        forget.Click += (_, _) =>
        {
            if (!ForgetSignIn())
                return;
            ClearConnection();
            imapPassword.Clear();
            smtpPassword.Clear();
            status.Text = "Saved sign-in removed.";
            forget.Enabled = account.HasSavedSignIn;
        };

        async Task ConnectAccountAsync(SavedSignIn signIn, CancellationToken token)
        {
            var mailbox = account.CreateMailbox(signIn);
            status.Text = "Checking IMAP and SMTP sign-in...";
            await mailbox.TestAsync(token);
            token.ThrowIfCancellationRequested();
            if (IsDisposed)
                return;
            if (signIn.ConnectionKey != account.ConnectionKey())
            {
                status.Text = "Connection settings changed during sign-in. Try again.";
                return;
            }

            activeKey = signIn.ConnectionKey;
            CancelBackgroundCache(account);
            account.Mailbox = mailbox;
            sessionSignIn = signIn;
            imapPassword.Text = smtpPassword.Text = passwordMask;
            UpdateConnectionState();
            account.Counts = new Dictionary<string, FolderCount>();
            account.CountsUpdatedAt = default;
            account.Expanded = true;
            status.Text = $"Connected as {account.Email.Trim()} for this session.";
            if (database is not null && SaveAccountSettings())
            {
                try
                {
                    WindowsCredentialStore.Write(account.Id, signIn);
                    account.HasSavedSignIn = true;
                    status.Text = "Connected. Sign-in saved in Windows Credential Manager.";
                }
                catch (Exception credentialError) when (credentialError is Win32Exception or InvalidOperationException)
                {
                    status.Text = "Connected for this session. Windows could not save the new sign-in.";
                }
            }
            RebuildFolders(account, "Inbox");
        }

        connect.Click += async (_, _) =>
        {
            if (account.ValidateConnection() is { } error)
            {
                status.Text = error.Message;
                Control? invalidInput = error.Field switch
                {
                    Field.Email => email,
                    Field.ImapHost => imapServer,
                    Field.ImapPort => imapPort,
                    Field.SmtpHost => smtpServer,
                    Field.SmtpPort => smtpPort,
                    _ => null
                };
                invalidInput?.Focus();
                return;
            }
            string key = account.ConnectionKey();
            SavedSignIn? existing = sessionSignIn;
            if (existing is null && (imapPassword.Text == passwordMask ||
                !samePassword.Checked && smtpPassword.Text == passwordMask))
            {
                try
                {
                    existing = WindowsCredentialStore.Read(account.Id);
                }
                catch (Exception credentialError) when (credentialError is Win32Exception or InvalidOperationException)
                {
                    status.Text = "Windows could not read the saved sign-in. Enter the passwords again.";
                    return;
                }
            }
            if (existing?.ConnectionKey != key)
                existing = null;

            string? incomingPassword = imapPassword.Text == passwordMask ? existing?.IncomingPassword : imapPassword.Text;
            string? outgoingPassword = samePassword.Checked ? incomingPassword :
                smtpPassword.Text == passwordMask ? existing?.OutgoingPassword : smtpPassword.Text;
            if (string.IsNullOrEmpty(incomingPassword) || string.IsNullOrEmpty(outgoingPassword))
            {
                status.Text = "Enter both IMAP and SMTP passwords.";
                (string.IsNullOrEmpty(incomingPassword) ? imapPassword : smtpPassword).Focus();
                return;
            }

            var signIn = new SavedSignIn(key, incomingPassword, outgoingPassword);
            connect.Enabled = remove.Enabled = forget.Enabled = false;
            try
            {
                await ConnectAccountAsync(signIn, mailboxLifetime.Token);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    status.Text = ConnectionError(ex);
            }
            finally
            {
                if (!IsDisposed)
                {
                    connect.Enabled = remove.Enabled = true;
                    forget.Enabled = account.HasSavedSignIn;
                }
            }
        };

        remove.Click += (_, _) =>
        {
            var selected = SelectedFolder;
            if (!ForgetSignIn())
                return;
            CancelBackgroundCache(account);
            accounts.Remove(account);
            accountTabs.Controls.Remove(tab);
            tab.Dispose();
            accountPages.Controls.Remove(page);
            page.Dispose();
            if (accountTabs.Controls.Count > 0)
                ((RadioButton)accountTabs.Controls[0]).Checked = true;
            bool keepSelection = selected is not null && selected.Account != account;
            RebuildFolders(keepSelection ? selected!.Account : null, keepSelection ? selected!.Folder! : "Inbox");
            ApplyTheme();
        };
    }

    private static string ConnectionError(Exception error) => error switch
    {
        MailKit.Security.AuthenticationException => "Sign-in was rejected. Check your username and password or app password.",
        MailKit.Security.SslHandshakeException => "The TLS handshake failed. Check the server name, port, and STARTTLS setting.",
        TimeoutException => "The mail server took too long to respond. Check the server settings and try again.",
        System.Net.Sockets.SocketException => "Could not reach the mail server. Check the hostname, port, and network connection.",
        OperationCanceledException => "The connection check was canceled.",
        _ => "Could not connect to the mail servers. Check the server settings and network connection."
    };

    private void ApplyAccountTheme(Color surface, Color chrome, Color ink, Color muted, Color border, Color hover)
    {
        accountPages.BackColor = accountTabs.Controls.Count > 0 ? border : surface;
        foreach (RadioButton tab in accountTabs.Controls)
        {
            StyleButton(tab, chrome, ink, border, hover);
            tab.FlatAppearance.CheckedBackColor = hover;
        }
        StyleButton(addAccount, chrome, ink, border, hover);
        foreach (Panel page in accountPages.Controls)
        {
            page.BackColor = surface;
            page.ForeColor = ink;
            foreach (Control field in page.Controls[0].Controls)
            {
                if (field is Panel { Controls.Count: 1 } frame && frame.Controls[0] is TextBox input)
                {
                    frame.BackColor = input.BackColor = surface;
                    input.ForeColor = input.ReadOnly ? muted : ink;
                }
                else if (field is NumericUpDown port)
                {
                    port.BackColor = surface;
                    port.ForeColor = ink;
                }
                else if (field is LinkLabel link)
                {
                    link.LinkColor = link.VisitedLinkColor = ink;
                    link.ActiveLinkColor = muted;
                }
                else if (field is Button button)
                    StyleButton(button, chrome, ink, border, hover);
            }
        }

    }
}













