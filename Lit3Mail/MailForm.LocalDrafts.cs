using Microsoft.Data.Sqlite;

namespace Lit3Mail;

public partial class MailForm
{
    private const string SavedMessagesStatus = "Could not refresh from the mail server. Showing saved messages.";

    private readonly Button localDrafts = new()
    {
        Text = "Local Drafts", Dock = DockStyle.Bottom, Height = 30, FlatStyle = FlatStyle.Flat
    };

    private int localDraftCountRequest;

    private async Task RefreshLocalDraftCountAsync()
    {
        if (database is null || IsDisposed || Disposing)
            return;
        int request = ++localDraftCountRequest;
        try
        {
            long count = await Task.Run(database.CountLocalDrafts);
            if (!IsDisposed && !Disposing && request == localDraftCountRequest)
            {
                localDrafts.Text = $"Local Drafts ({count:N0})";
                localDrafts.Font = count > 0 ? unreadFont : Font;
            }
        }
        catch (SqliteException)
        {
            if (!IsDisposed && !Disposing && request == localDraftCountRequest)
            {
                localDrafts.Text = "Local Drafts";
                localDrafts.Font = Font;
            }
        }
    }

    private async Task<Guid?> ChooseLocalDraftAsync(LocalDatabase store)
    {
        var drafts = await Task.Run(store.ListLocalDrafts, mailboxLifetime.Token);
        if (IsDisposed || Disposing)
            return null;
        using var picker = new Form
        {
            Text = "Local Drafts", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(530, 300), MinimizeBox = false, MaximizeBox = false
        };
        var list = new ListBox { Dock = DockStyle.Fill, AccessibleName = "Drafts saved on this computer", HorizontalScrollbar = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        var open = new Button { Text = "Open", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
        var delete = new Button { Text = "Delete", AutoSize = true, Enabled = false };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var description = new Label
        {
            Text = "Drafts saved on this computer. Open a draft to edit, save, or send it.",
            Dock = DockStyle.Top, Height = 32, Padding = new Padding(6)
        };
        actions.Controls.AddRange([open, delete, close]);
        picker.Controls.AddRange([list, actions, description]);
        ThemedPopup.ApplyTo(picker);
        picker.AcceptButton = open;
        picker.CancelButton = close;
        list.Items.AddRange(drafts.Cast<object>().ToArray());
        if (list.Items.Count == 0)
        {
            ThemedPopup.Show(this, "There are no drafts saved on this computer.", Text);
            return null;
        }
        list.SelectedIndexChanged += (_, _) => open.Enabled = delete.Enabled = list.SelectedItem is LocalDraftEntry;
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem is LocalDraftEntry)
                picker.DialogResult = DialogResult.OK;
        };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not LocalDraftEntry draft ||
                ThemedPopup.Show(picker, "Delete this local draft?", picker.Text, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            int index = list.SelectedIndex;
            list.Enabled = open.Enabled = delete.Enabled = false;
            try
            {
                await Task.Run(() => store.DeleteLocalDraft(draft.Id));
                if (picker.IsDisposed)
                    return;
                list.Items.RemoveAt(index);
                await RefreshLocalDraftCountAsync();
                if (picker.IsDisposed)
                    return;
                list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
            }
            catch (SqliteException)
            {
                if (!picker.IsDisposed)
                    ThemedPopup.Show(picker, "Could not delete the local draft. Try again.", picker.Text);
            }
            finally
            {
                if (!picker.IsDisposed)
                {
                    list.Enabled = true;
                    open.Enabled = delete.Enabled = list.SelectedItem is LocalDraftEntry;
                }
            }
        };
        list.SelectedIndex = 0;
        return picker.ShowDialog(ComposeDialogOwner) == DialogResult.OK && list.SelectedItem is LocalDraftEntry selected ? selected.Id : null;
    }

    private static bool NetworkUnavailable() => !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    private static string MailOperationError(Exception error, string fallback)
    {
        if (NetworkUnavailable())
            return "The computer appears to be offline. Reconnect and try again.";
        return error switch
        {
            MailKit.Security.AuthenticationException => "The server rejected the sign-in. Check your account settings.",
            MailKit.Security.SslHandshakeException => "Could not establish a secure connection. Check your server settings.",
            TimeoutException or System.Net.Sockets.SocketException or IOException =>
                "The mail server could not be reached or the connection was interrupted. Check your connection and try again.",
            _ => fallback
        };
    }
}
