using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Lit3Mail;

public partial class MailForm
{
    private sealed class InboxNotificationCursor(ServerMailbox mailbox)
    {
        public ServerMailbox Mailbox { get; } = mailbox;
        public ServerMailbox.InboxPosition? Position { get; set; }
    }
    private sealed record NotificationCheck(MailAccount Account, InboxNotificationCursor Cursor, int Count,
        ServerMailbox.InboxArrivalPreview? Preview = null);

    private readonly Dictionary<MailAccount, InboxNotificationCursor> notificationCursors = new();
    private readonly Dictionary<MailAccount, NotificationCheck> pendingMailReminders = new();
    private bool preparingMailReminders;
    private AppNotificationManager? notificationManager;
    private (FolderEntry Folder, Guid MessageId)? pendingReminderMessage;

    private void InitializeDesktopNotifications()
    {
        Activated += async (_, _) => await DismissMailRemindersAsync();
        Shown += async (_, _) =>
        {
            try
            {
                if (!AppNotificationManager.IsSupported())
                    return;
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnNotificationInvoked;
                try
                {
                    manager.Register();
                    notificationManager = manager;
                }
                catch
                {
                    manager.NotificationInvoked -= OnNotificationInvoked;
                    throw;
                }

                if (AppInstance.GetCurrent().GetActivatedEventArgs().Data is AppNotificationActivatedEventArgs activation)
                    OnNotificationInvoked(manager, activation);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not start Windows notifications ({error.GetType().Name}).");
            }
            if (ActiveForm == this)
                await DismissMailRemindersAsync();
            await CheckMailIndicatorsAsync();
        };
        FormClosed += (_, _) =>
        {
            if (notificationManager is not { } manager)
                return;
            notificationManager = null;
            manager.NotificationInvoked -= OnNotificationInvoked;
            try { manager.Unregister(); }
            catch (COMException) { }
        };
    }

    private async Task DismissMailRemindersAsync()
    {
        if (notificationManager is not { } manager)
            return;
        try
        {
            await manager.RemoveAllAsync();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not dismiss Windows notifications ({error.GetType().Name}).");
        }
    }

    private async Task<NotificationCheck[]> PreparePendingMailRemindersAsync()
    {
        if (pendingMailReminders.Count == 0 || updatingMessageFolders || exportCancellation is not null)
            return [];
        preparingMailReminders = true;
        PauseBackgroundCacheForFolderLoad();
        try
        {
            var ready = new List<NotificationCheck>();
            foreach (var check in pendingMailReminders.Values.ToArray())
            {
                if (!IsCurrent(check.Account, check.Cursor))
                {
                    pendingMailReminders.Remove(check.Account);
                    continue;
                }
                if (backgroundShutdown || unfocusedSince is null || recoveringFromSleep || IsDisposed)
                    break;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(mailboxLifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    string key = check.Account.ConnectionKey();
                    var prepared = await Task.Run(async () =>
                    {
                        if (database is not { } store)
                            return (NotificationCheck?)null;
                        string? revision = store.GetFolderCacheRevision(check.Account.Id, "Inbox");
                        var summaries = await check.Cursor.Mailbox.ListAsync("Inbox", "", timeout.Token,
                            null, LocalDatabase.FolderCacheLimit);
                        timeout.Token.ThrowIfCancellationRequested();
                        if (check.Preview is { } preview && !summaries.Any(mail => mail.Id == preview.MessageId))
                            return check with { Count = 0 }; // The notified mail has since left the Inbox.
                        store.SaveFolderCache(check.Account.Id, "Inbox", key, summaries,
                            onlyIfUnchanged: true, expectedRevision: revision);
                        if (check.Preview is { } target)
                        {
                            if (!store.LoadFolderCache(check.Account.Id, "Inbox", key).Any(mail => mail.Id == target.MessageId))
                                return null; // A concurrent folder update won; retry against its new revision.
                            if (!store.GetCachedMessageBodies(check.Account.Id, "Inbox", key).Contains(target.MessageId))
                            {
                                var content = await check.Cursor.Mailbox.LoadContentAsync(target.MessageId, timeout.Token);
                                timeout.Token.ThrowIfCancellationRequested();
                                if (!content.PreviewTooLarge)
                                    store.SaveMessageBody(check.Account.Id, "Inbox", key, target.MessageId, content,
                                        completePreview: true);
                            }
                        }
                        return check;
                    }, timeout.Token);
                    if (prepared is not null && !timeout.IsCancellationRequested && IsCurrent(check.Account, check.Cursor) &&
                        ReferenceEquals(pendingMailReminders.GetValueOrDefault(check.Account), check) &&
                        unfocusedSince is not null && !recoveringFromSleep)
                    {
                        if (prepared.Count > 0)
                            ready.Add(prepared);
                        else
                            pendingMailReminders.Remove(check.Account);
                    }
                }
                catch (Exception error)
                {
                    // Retain the reminder and retry after connectivity or the cache recovers.
                    System.Diagnostics.Debug.WriteLine($"Preparing mail reminder failed ({error.GetType().Name}).");
                }
            }
            return ready.ToArray();
        }
        finally
        {
            preparingMailReminders = false;
            ResumeBackgroundCache();
        }
    }

    private void OnNotificationInvoked(AppNotificationManager _, AppNotificationActivatedEventArgs args)
    {
        var (accountId, messageId) = MailReminderArguments.Parse(args.Argument);
        this.PostIfHandleAvailable(async () =>
        {
            if (backgroundShutdown)
                return;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            var account = accounts.FirstOrDefault(item => item.Id == accountId);
            if (account is not null && settingsPage.Visible)
                backToMail.PerformClick();
            if (!mailLayout.Visible || settingsPage.Visible || account is null)
                return;

            suppressSearchReload = true;
            try { search.Clear(); }
            finally { suppressSearchReload = false; }

            var inbox = new FolderEntry(account, "Inbox");
            pendingReminderMessage = messageId is { } target ? (inbox, target) : null;
            if (messageId is not null)
            {
                messages.CurrentCell = null;
                messages.ClearSelection();
                selectionMade = false;
                selectedMessageId = null;
            }
            if (SelectedFolder == inbox)
                await ShowFolder(SelectedMessage, showRefreshStatus: true, prioritizeCurrentFolder: true);
            else
                SelectFolder(account, "Inbox");
        });
    }

    private async Task SelectReminderMessageAsync(Guid messageId)
    {
        var row = messages.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(item => item.Tag is MailSummary mail && mail.Id == messageId);
        if (row is null)
            return;

        if (mailPane.Panel1Collapsed)
            toggleMessageList.PerformClick();
        rebuildingRows = true;
        try
        {
            messages.ClearSelection();
            messages.CurrentCell = row.Cells[3];
            row.Selected = true;
            TryScrollMessagesTo(row.Index);
        }
        finally { rebuildingRows = false; }
        messages.Focus();
        // Rendering cached or refreshed rows may already have recorded this ID without loading its live preview.
        selectedMessageId = null;
        await SelectCurrentMessage();
    }

    private void ResetDesktopNotificationCursor(MailAccount account)
    {
        notificationCursors.Remove(account);
        if (account.DesktopNotifications && account.Mailbox is not null)
            _ = CheckMailIndicatorsAsync();
    }

    private async Task<NotificationCheck[]> CheckDesktopNotificationsAsync()
    {
        if (backgroundShutdown || notificationManager is null)
            return [];

        foreach (var account in notificationCursors.Keys.ToArray())
            if (!accounts.Contains(account) || !account.DesktopNotifications ||
                !ReferenceEquals(account.Mailbox, notificationCursors[account].Mailbox))
                notificationCursors.Remove(account);

        var checks = new List<Task<NotificationCheck>>();
        var notifyAfter = unfocusedSince;
        foreach (var account in accounts)
        {
            if (!account.DesktopNotifications || account.Mailbox is not ServerMailbox mailbox)
                continue;
            if (!notificationCursors.TryGetValue(account, out var cursor))
                notificationCursors.Add(account, cursor = new(mailbox));
            checks.Add(CheckAccountAsync(account, cursor, notifyAfter, mailboxLifetime.Token));
        }

        return await Task.WhenAll(checks);
    }

    private void ShowMailReminders(NotificationCheck[] results)
    {
        if (notificationManager is not { } manager)
            return;
        var arrivals = results.Where(result => result.Count > 0 && IsCurrent(result.Account, result.Cursor)).ToArray();
        if (arrivals.Length == 0)
            return;

        try
        {
            long count = arrivals.Sum(result => (long)result.Count);
            string messages = count == 1 ? "1 new email" : $"{count:N0} new emails";
            var notificationAccount = arrivals.Length == 1 ? arrivals[0].Account : null;
            string text = notificationAccount is { } inbox
                ? $"{messages} in {inbox.DisplayName}."
                : $"{messages} across {arrivals.Length:N0} accounts.";
            text = LimitNotificationText(text, 80);
            var previewArrival = arrivals.Where(result => result.Preview is not null)
                .OrderByDescending(result => result.Preview!.ArrivalTime).FirstOrDefault();
            var preview = previewArrival?.Preview;
            if (preview is not null)
            {
                string subject = LimitNotificationText(ServerMailbox.ReadReminderText(preview.Subject), 48);
                if (subject.Length > 0)
                    text += "\n" + subject;
                string excerpt = LimitNotificationText(preview.Text, 199 - text.Length);
                if (excerpt.Length > 0)
                    text += "\n" + excerpt;
            }
            var payload = CreateMailReminder(text, previewArrival?.Account.Id ?? notificationAccount?.Id, preview?.MessageId);
            manager.Show(new AppNotification(payload));
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show a Windows notification ({error.GetType().Name}).");
        }
    }

    private static string CreateMailReminder(string summary, Guid? accountId, Guid? messageId)
    {
        string arguments = MailReminderArguments.Create(accountId, messageId);
        var visual = new XElement("binding", new XAttribute("template", "ToastGeneric"),
            summary.Split('\n').Select(line => new XElement("text", line)));
        return new XElement("toast", new XAttribute("scenario", "reminder"), new XAttribute("launch", arguments),
            new XElement("visual", visual),
            new XElement("actions",
                new XElement("action", new XAttribute("content", accountId is null ? "Open Lit3Mail" : "Open Inbox"),
                    new XAttribute("arguments", arguments)),
                new XElement("action", new XAttribute("content", "Dismiss"),
                    new XAttribute("arguments", "dismiss"), new XAttribute("activationType", "system"))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private bool IsCurrent(MailAccount account, InboxNotificationCursor cursor) =>
        accounts.Contains(account) && account.DesktopNotifications && ReferenceEquals(account.Mailbox, cursor.Mailbox) &&
        ReferenceEquals(notificationCursors.GetValueOrDefault(account), cursor);

    private async Task<NotificationCheck> CheckAccountAsync(
        MailAccount account, InboxNotificationCursor cursor, DateTimeOffset? notifyAfter, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var (position, arrivals, preview) = await cursor.Mailbox.CheckInboxArrivalsAsync(cursor.Position, notifyAfter, timeout.Token);
            if (!timeout.IsCancellationRequested && !IsDisposed && IsCurrent(account, cursor) &&
                (notifyAfter is not null || unfocusedSince is null))
            {
                cursor.Position = position;
                if (arrivals > 0)
                    QueueArrivalCache(account);
                return new(account, cursor, arrivals, preview);
            }
        }
        catch (Exception error)
        {
            // Keep the last confirmed position and retry on the next refresh.
            System.Diagnostics.Debug.WriteLine($"Desktop notification check failed ({error.GetType().Name}).");
        }
        return new(account, cursor, 0);
    }

    private static string LimitNotificationText(string? value, int limit)
    {
        string text = Regex.Replace(value ?? "", @"[\s\p{Cc}]+", " ").Trim();
        if (text.Length <= limit)
            return text;
        int end = limit - 1;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end].TrimEnd() + "…";
    }
}
