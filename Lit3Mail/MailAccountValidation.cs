using MimeKit;

namespace Lit3Mail;

internal static class MailAccountValidation
{
    internal const string OutlookSignInError =
        "Outlook.com requires OAuth2 (Modern Authentication). Lit3Mail supports password sign-in only.";

    internal enum Field { Account, Email, ImapHost, ImapPort, SmtpHost, SmtpPort }

    internal static (Field Field, string Message)? Validate(string email,
        string imapHost, int imapPort, string imapUser, string smtpHost, int smtpPort, string smtpUser)
    {
        if (RequiresOutlookSignIn(email, imapHost, imapUser, smtpHost, smtpUser))
            return (Field.Account, OutlookSignInError);

        string address = email.Trim();
        if (!MailboxAddress.TryParse(address, out var mailbox) || !mailbox.Address.Contains('@') ||
            !string.Equals(address, mailbox.Address, StringComparison.Ordinal))
            return (Field.Email, "Enter a valid email address without a display name.");
        if (Uri.CheckHostName(imapHost.Trim()) == UriHostNameType.Unknown)
            return (Field.ImapHost, "Enter an IMAP hostname or IP address. Use the separate port field.");
        if (Uri.CheckHostName(smtpHost.Trim()) == UriHostNameType.Unknown)
            return (Field.SmtpHost, "Enter an SMTP hostname or IP address. Use the separate port field.");
        if (imapPort is < 1 or > 65535)
            return (Field.ImapPort, "Enter an IMAP port between 1 and 65535.");
        if (smtpPort is < 1 or > 65535)
            return (Field.SmtpPort, "Enter an SMTP port between 1 and 65535.");
        return null;
    }

    internal static bool RequiresOutlookSignIn(string email, string imapHost, string imapUser, string smtpHost, string smtpUser) =>
        imapHost.Trim().TrimEnd('.').ToLowerInvariant() is "outlook.office365.com" or "imap-mail.outlook.com" ||
        smtpHost.Trim().TrimEnd('.').Equals("smtp-mail.outlook.com", StringComparison.OrdinalIgnoreCase) ||
        IsOutlookAddress(email) || IsOutlookAddress(imapUser) || IsOutlookAddress(smtpUser);

    private static bool IsOutlookAddress(string address)
    {
        if (!MailboxAddress.TryParse(address.Trim(), out var mailbox))
            return false;

        int separator = mailbox.Address.LastIndexOf('@');
        return separator >= 0 && mailbox.Address[(separator + 1)..].TrimEnd('.').ToLowerInvariant()
            is "outlook.com" or "hotmail.com" or "live.com" or "msn.com";
    }
}
