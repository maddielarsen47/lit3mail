using Microsoft.Win32;

namespace Lit3Mail;

public partial class MailForm
{
    private const string CheckingForMailStatus = "Checking for mail...";
    private bool refreshingAutomatically;
    private bool recoveringFromSleep;
    private CancellationTokenSource? wakeRefresh;

    private void InitializeAutoRefresh()
    {
        var timer = new System.Windows.Forms.Timer(components!) { Interval = 60_000 };
        int minutesUntilFullRefresh = 10;
        timer.Tick += async (_, _) =>
        {
            bool fullRefresh = --minutesUntilFullRefresh <= 0;
            if (fullRefresh && refreshingAutomatically)
                return;
            if (fullRefresh)
                minutesUntilFullRefresh = 10;
            await RefreshMailAsync(fullRefresh);
        };
        Shown += (_, _) => timer.Start();
        PowerModeChangedEventHandler powerChanged = (_, args) => this.PostIfHandleAvailable(async () =>
        {
            if (args.Mode == PowerModes.Suspend)
            {
                wakeRefresh?.Cancel();
                wakeRefresh = null;
                recoveringFromSleep = true;
                folderLoad?.Cancel();
                previewLoad?.Cancel();
                PauseBackgroundCacheForFolderLoad();
            }
            else if (args.Mode == PowerModes.Resume)
                await RefreshAfterSleepAsync();
        });
        SystemEvents.PowerModeChanged += powerChanged;
        Disposed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= powerChanged;
            wakeRefresh?.Cancel();
        };
    }

    private async Task RefreshAfterSleepAsync()
    {
        if (backgroundShutdown)
            return;
        wakeRefresh?.Cancel();
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        wakeRefresh = recovery;
        recoveringFromSleep = true;
        folderLoad?.Cancel();
        previewLoad?.Cancel();
        PauseBackgroundCacheForFolderLoad();
        try
        {
            // Wi-Fi and VPN connections often need a few seconds after Windows resumes.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 5), recovery.Token);
                if (NetworkUnavailable() || refreshingAutomatically || updatingMessageFolders ||
                    exportCancellation is not null || choosingComposeFiles)
                    continue;
                await RefreshMailAsync(fullRefresh: true, resuming: true);
                recovery.Token.ThrowIfCancellationRequested();
                if (!accounts.Any(account => account.Mailbox is not null && account.ConnectionFailed))
                    break;
            }
        }
        catch (OperationCanceledException) when (recovery.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Mail refresh after sleep failed ({error.GetType().Name}).");
        }
        finally
        {
            if (ReferenceEquals(wakeRefresh, recovery))
            {
                wakeRefresh = null;
                recoveringFromSleep = false;
                if (!IsDisposed && !Disposing)
                {
                    UpdateOfflineIndicator();
                    ResumeBackgroundCache();
                    await CheckMailIndicatorsAsync();
                }
            }
        }
    }

    private async Task RefreshMailAsync(bool fullRefresh, bool manual = false, bool resuming = false)
    {
        if (backgroundShutdown || (!manual && (recoveringFromSleep && !resuming || refreshingAutomatically || updatingMessageFolders || exportCancellation is not null)) ||
            choosingComposeFiles || IsDisposed || Disposing ||
            !accounts.Any(account => account.Mailbox is not null))
            return;

        if (!manual)
            refreshingAutomatically = true;
        var activity = fullRefresh && !manual ? BeginBatchActivity(CheckingForMailStatus) : null;
        var token = mailboxLifetime.Token;
        try
        {
            var current = SelectedFolder;
            bool refreshList = current?.Account.Mailbox is not null && (manual || folderLoad is null) &&
                mailLayout.Visible && !settingsPage.Visible;
            var activeInbox = refreshList && current?.Folder == "Inbox" && string.IsNullOrWhiteSpace(search.Text)
                ? current.Account : null;
            bool reloadCurrent = refreshList && (manual || string.IsNullOrWhiteSpace(search.Text)) &&
                (fullRefresh || activeInbox is not null);
            if (reloadCurrent)
                await ShowFolder(SelectedMessage, refreshCounts: fullRefresh, showRefreshStatus: manual,
                    backgroundRefresh: !manual, prioritizeCurrentFolder: true);

            if (token.IsCancellationRequested || IsDisposed)
                return;
            if (unfocusedSince is null)
                _ = CheckMailIndicatorsAsync();
            QueueInboxRefresh(activeInbox);

            if (!fullRefresh)
            {
                await Task.WhenAll(accounts.Where(account => account != activeInbox)
                    .Select(account => RefreshInboxCountAsync(account, token)).ToArray());
                return;
            }

            foreach (var account in accounts.ToArray())
            {
                if (token.IsCancellationRequested)
                    return;
                if (!reloadCurrent || account != current?.Account)
                    await RefreshFolderCounts(account, account.Mailbox, true, token);
            }
        }
        finally
        {
            if (!manual)
                refreshingAutomatically = false;
            if (activity is not null)
                EndBatchActivity(activity);
        }
    }

    private async Task RefreshInboxCountAsync(MailAccount account, CancellationToken token)
    {
        if (account.Mailbox is not ServerMailbox mailbox)
            return;

        var previous = account.Counts.GetValueOrDefault("Inbox");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var count = await mailbox.GetInboxCountAsync(timeout.Token);
            if (!IsDisposed && !timeout.IsCancellationRequested && !updatingMessageFolders &&
                accounts.Contains(account) && ReferenceEquals(account.Mailbox, mailbox) &&
                ReferenceEquals(previous, account.Counts.GetValueOrDefault("Inbox")))
            {
                account.ConnectionFailed = false;
                UpdateOfflineIndicator();
                SetFolderCount(account, "Inbox", Math.Clamp(count.Unread + PendingUnreadDelta(account, "Inbox"), 0, count.Total), count.Total);
                if (previous is null || count.Total > previous.Total || count.Unread > previous.Unread)
                    QueueArrivalCache(account);
            }
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && !IsDisposed && ReferenceEquals(account.Mailbox, mailbox))
            {
                account.ConnectionFailed = true;
                UpdateOfflineIndicator();
            }
            // Keep the last count until the server responds.
        }
    }
}
