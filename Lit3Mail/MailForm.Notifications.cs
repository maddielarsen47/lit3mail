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

    private void OnNotificationInvoked(AppNotificationManager _, AppNotificationActivatedEventArgs args)
    {
        Guid? accountId = args.Arguments.TryGetValue("account", out string? value) && Guid.TryParse(value, out var id)
            ? id : null;
        Guid? messageId = args.Arguments.TryGetValue("message", out string? messageValue) && Guid.TryParse(messageValue, out var parsedMessage)
            ? parsedMessage : null;
        this.PostIfHandleAvailable(async () =>
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            var account = accounts.FirstOrDefault(item => item.Id == accountId && item.DesktopNotifications && item.Mailbox is not null);
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
        if (notificationManager is null)
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
        string arguments = accountId is { } id ? $"action=open&account={id:D}" : "action=open";
        if (accountId is not null && messageId is { } message)
            arguments += $"&message={message:D}";
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
