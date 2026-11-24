namespace Lit3Mail;

public partial class MailForm
{
    private readonly Label offlineIndicator = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 6, 12, 6),
        AccessibleName = "Mail connection status", Visible = false
    };

    private void UpdateOfflineIndicator()
    {
        if (IsDisposed || Disposing)
            return;
        var disconnected = accounts.Where(account => account.Mailbox is null || account.ConnectionFailed).ToArray();
        offlineIndicator.Text = NetworkUnavailable() ? "Offline — no network connection. Showing saved mail."
            : disconnected.Length > 0
                ? "Offline — " + string.Join(", ", disconnected.Select(account =>
                    $"{account.DisplayName}: {(account.Mailbox is null ? "sign-in required" : "mail server unavailable")}"))
                : "";
        offlineIndicator.Visible = offlineIndicator.Text.Length > 0;
    }

    private void PromptForStartupPassword(MailAccount account, Func<SavedSignIn, CancellationToken, Task> connect)
    {
        using var dialog = new Form
        {
            Text = $"Sign in — {account.DisplayName}", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 310), FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false
        };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        var description = new Label { AutoSize = true, MaximumSize = new Size(440, 0),
            Text = $"Enter the password for {account.Email}. For Gmail, use an app password." };
        var incoming = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Top, AccessibleName = "IMAP password" };
        var same = new CheckBox { Text = "Use the same password for sending (SMTP)", Checked = true, AutoSize = true };
        var outgoing = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Top, AccessibleName = "SMTP password", Enabled = false };
        var status = new Label { AutoSize = true, MaximumSize = new Size(440, 0) };
        var actions = new FlowLayoutPanel { AutoSize = true };
        var signIn = new Button { Text = "Sign in", AutoSize = true };
        var offline = new Button { Text = "Work offline", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([signIn, offline]);
        fields.Controls.Add(description);
        fields.Controls.Add(new Label { Text = "IMAP password", AutoSize = true });
        fields.Controls.Add(incoming);
        fields.Controls.Add(same);
        fields.Controls.Add(outgoing);
        fields.Controls.Add(status);
        fields.Controls.Add(actions);
        dialog.Controls.Add(fields);
        dialog.AcceptButton = signIn;
        dialog.CancelButton = offline;
        same.CheckedChanged += (_, _) => outgoing.Enabled = !same.Checked;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        dialog.FormClosing += (_, _) => cancellation.Cancel();
        signIn.Click += async (_, _) =>
        {
            if (account.ValidateConnection() is { } invalid)
            {
                status.Text = invalid.Message + " Update this account in Settings.";
                return;
            }
            if (incoming.Text.Length == 0 || !same.Checked && outgoing.Text.Length == 0)
            {
                status.Text = "Enter a password to sign in, or choose Work offline.";
                return;
            }
            signIn.Enabled = false;
            status.Text = "Signing in…";
            try
            {
                await connect(new SavedSignIn(account.ConnectionKey(), incoming.Text,
                    same.Checked ? incoming.Text : outgoing.Text), cancellation.Token);
                if (!dialog.IsDisposed && !cancellation.IsCancellationRequested)
                    dialog.DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!dialog.IsDisposed)
                    status.Text = ConnectionError(error);
            }
            finally
            {
                if (!dialog.IsDisposed)
                    signIn.Enabled = true;
                UpdateOfflineIndicator();
            }
        };
        ThemedPopup.ApplyTo(dialog);
        dialog.ShowDialog(this);
    }
}
