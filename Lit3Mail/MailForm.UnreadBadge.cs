namespace Lit3Mail;

public partial class MailForm
{
    private DateTimeOffset? unfocusedSince;
    private CancellationTokenSource? badgeCheck;
    private readonly HashSet<MailAccount> accountsWithNewUnread = [];
    private bool unreadBadgeVisible;

    private void InitializeUnreadBadge()
    {
        var timer = new System.Windows.Forms.Timer(components!) { Interval = 30_000 };
        timer.Tick += async (_, _) => await CheckUnreadBadgeAsync();
        Activated += (_, _) =>
        {
            timer.Stop();
            unfocusedSince = null;
            badgeCheck?.Cancel();
            accountsWithNewUnread.Clear();
            SetUnreadBadge(false);
        };
        Deactivate += (_, _) => BeginUnfocused();
        Shown += (_, _) =>
        {
            if (ActiveForm != this)
                BeginUnfocused();
        };

        void BeginUnfocused()
        {
            unfocusedSince ??= DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            timer.Start();
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
            foreach (var account in accounts.ToArray())
            {
                check.Token.ThrowIfCancellationRequested();
                if (account.Mailbox is not { } mailbox)
                    continue;

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

            check.Token.ThrowIfCancellationRequested();
            accountsWithNewUnread.RemoveWhere(account => !accounts.Contains(account) || account.Mailbox is null);
            SetUnreadBadge(accountsWithNewUnread.Count > 0);
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
