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
        this.PostIfHandleAvailable(() =>
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            var account = accounts.FirstOrDefault(item => item.Id == accountId && item.DesktopNotifications && item.Mailbox is not null);
            if (!composePage.Visible && !settingsPage.Visible && account is not null)
                SelectFolder(account, "Inbox");
        });
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
        foreach (var account in accounts)
        {
            if (!account.DesktopNotifications || account.Mailbox is not ServerMailbox mailbox)
                continue;
            if (!notificationCursors.TryGetValue(account, out var cursor))
                notificationCursors.Add(account, cursor = new(mailbox));
            checks.Add(CheckAccountAsync(account, cursor, mailboxLifetime.Token));
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
            var preview = arrivals.Select(result => result.Preview).OfType<ServerMailbox.InboxArrivalPreview>()
                .OrderByDescending(item => item.ArrivalTime).FirstOrDefault();
            if (preview is not null)
            {
                string subject = LimitNotificationText(preview.Subject, 48);
                if (subject.Length > 0)
                    text += "\n" + subject;
                string excerpt = LimitNotificationText(preview.Text, 199 - text.Length);
                if (excerpt.Length > 0)
                    text += "\n" + excerpt;
            }
            var payload = CreateMailReminder(text, notificationAccount?.Id);
            manager.Show(new AppNotification(payload));
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show a Windows notification ({error.GetType().Name}).");
        }
    }

    private static string CreateMailReminder(string summary, Guid? accountId)
    {
        string arguments = accountId is { } id ? $"action=open&account={id:D}" : "action=open";
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
        MailAccount account, InboxNotificationCursor cursor, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var (position, arrivals, preview) = await cursor.Mailbox.CheckInboxArrivalsAsync(cursor.Position, timeout.Token);
            if (!timeout.IsCancellationRequested && !IsDisposed && IsCurrent(account, cursor))
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
