namespace Lit3Mail;

public partial class MailForm
{
    private DateTimeOffset? unfocusedSince;
    private CancellationTokenSource? badgeCheck;
    private readonly HashSet<MailAccount> accountsWithNewUnread = [];
    private bool unreadBadgeVisible;
    private bool checkingMailIndicators;
    private int mailFocusGeneration;
    private System.Windows.Forms.Timer mailIndicatorTimer = null!;

    private void InitializeUnreadBadge()
    {
        mailIndicatorTimer = new System.Windows.Forms.Timer(components!) { Interval = 60_000 };
        mailIndicatorTimer.Tick += async (_, _) => await CheckMailIndicatorsAsync();
        Activated += (_, _) => OnMailWindowActivated();
        Deactivate += (_, _) => CheckMailWindowFocus();
        Shown += (_, _) =>
        {
            if (ActiveForm != this)
                BeginUnfocused();
            else
                mailIndicatorTimer.Start();
        };
    }

    private void OnMailWindowActivated()
    {
        if (unfocusedSince is not null)
            mailFocusGeneration++;
        mailIndicatorTimer.Interval = 60_000;
        unfocusedSince = null;
        badgeCheck?.Cancel();
        accountsWithNewUnread.Clear();
        SetUnreadBadge(false);
    }

    private void CheckMailWindowFocus()
    {
        if (choosingComposeFiles)
            return;
        this.PostIfHandleAvailable(() =>
        {
            if (!choosingComposeFiles && ActiveForm != this && (composeWindow is null || ActiveForm != composeWindow))
                BeginUnfocused();
        });
    }

    private void BeginUnfocused()
    {
        unfocusedSince ??= DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        mailIndicatorTimer.Interval = 30_000;
        mailIndicatorTimer.Start();
    }

    private async Task CheckMailIndicatorsAsync()
    {
        if (choosingComposeFiles || checkingMailIndicators || IsDisposed || Disposing)
            return;

        checkingMailIndicators = true;
        int generation = mailFocusGeneration;
        try
        {
            var badge = CheckUnreadBadgeAsync();
            var reminders = CheckDesktopNotificationsAsync();
            await Task.WhenAll(badge, reminders);
            if (IsDisposed || Disposing || generation != mailFocusGeneration)
                return;

            SetUnreadBadge(unfocusedSince is not null && accountsWithNewUnread.Count > 0);
            if (unfocusedSince is not null)
                ShowMailReminders(await reminders);
        }
        catch (OperationCanceledException)
        {
            // A canceled background check can be retried on the next tick.
        }
        finally
        {
            checkingMailIndicators = false;
        }
    }

    private async Task CheckUnreadBadgeAsync()
    {
        if (unfocusedSince is not { } since || badgeCheck is not null || IsDisposed)
            return;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
        badgeCheck = check;
        try
        {
            await Task.WhenAll(accounts.ToArray().Select(CheckAccountAsync));
            check.Token.ThrowIfCancellationRequested();
            accountsWithNewUnread.RemoveWhere(account => !accounts.Contains(account) || account.Mailbox is null);

            async Task CheckAccountAsync(MailAccount account)
            {
                check.Token.ThrowIfCancellationRequested();
                if (account.Mailbox is not { } mailbox)
                    return;

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(check.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    bool hasNewMail = await mailbox.HasUnreadArrivalsAsync(since, timeout.Token);
                    check.Token.ThrowIfCancellationRequested();
                    if (ReferenceEquals(account.Mailbox, mailbox))
                    {
                        if (hasNewMail)
                            accountsWithNewUnread.Add(account);
                        else
                            accountsWithNewUnread.Remove(account);
                    }
                }
                catch (Exception) when (!check.IsCancellationRequested)
                {
                    // Keep the previous result while a server is unavailable.
                }
            }

        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        finally
        {
            badgeCheck = null;
        }
    }

    private void SetUnreadBadge(bool visible)
    {
        if (unreadBadgeVisible == visible || IsDisposed)
            return;
        unreadBadgeVisible = visible;
        if (IsHandleCreated)
            TaskbarUnreadBadge.Apply(Handle, visible);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (unreadBadgeVisible && message.Msg == TaskbarUnreadBadge.ButtonCreatedMessage)
            TaskbarUnreadBadge.Apply(message.HWnd, true);
    }
}
