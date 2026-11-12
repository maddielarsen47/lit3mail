namespace Lit3Mail;

public partial class MailForm
{
    private const string CheckingForMailStatus = "Checking for mail...";
    private bool refreshingAutomatically;

    private void InitializeAutoRefresh()
    {
        var timer = new System.Windows.Forms.Timer(components!) { Interval = 10 * 60 * 1000 };
        timer.Tick += async (_, _) => await RefreshAutomaticallyAsync();
        Shown += (_, _) => timer.Start();
    }

    private async Task RefreshAutomaticallyAsync()
    {
        if (refreshingAutomatically || updatingMessageFolders || exportCancellation is not null || IsDisposed || Disposing ||
            !accounts.Any(account => account.Mailbox is not null))
            return;

        refreshingAutomatically = true;
        var activity = BeginBatchActivity(CheckingForMailStatus);
        var token = mailboxLifetime.Token;
        try
        {
            var current = SelectedFolder;
            bool refreshList = current?.Account.Mailbox is not null && folderLoad is null &&
                !composePage.Visible && !settingsPage.Visible;
            var activeInbox = refreshList && current?.Folder == "Inbox" && string.IsNullOrWhiteSpace(search.Text)
                ? current.Account : null;
            QueueInboxRefresh(activeInbox);
            if (refreshList)
                await ShowFolder(SelectedMessage, refreshCounts: true, backgroundRefresh: true);

            foreach (var account in accounts.ToArray())
            {
                if (token.IsCancellationRequested)
                    return;
                if (!refreshList || account != current?.Account)
                    await RefreshFolderCounts(account, account.Mailbox, true, token);
            }
        }
        finally
        {
            refreshingAutomatically = false;
            EndBatchActivity(activity);
        }
    }
}
