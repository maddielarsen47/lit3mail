using System;

namespace Lit3Mail;

internal static class MailReminderArguments
{
    public static string Create(Guid? accountId, Guid? messageId)
    {
        string arguments = accountId is { } account ? $"action=open;account={account:D}" : "action=open";
        if (accountId is not null && messageId is { } message)
            arguments += $";message={message:D}";
        return arguments;
    }

    public static (Guid? AccountId, Guid? MessageId) Parse(string arguments)
    {
        Guid? account = null;
        Guid? message = null;
        // Accept the old query-string format too, for reminders already in Windows notification history.
        foreach (string pair in arguments.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            if (separator < 0 || !Guid.TryParse(pair[(separator + 1)..], out var id))
                continue;
            if (pair[..separator] == "account")
                account = id;
            else if (pair[..separator] == "message")
                message = id;
        }
        return (account, message);
    }
}
