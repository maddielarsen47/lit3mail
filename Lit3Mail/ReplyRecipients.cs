using MimeKit;

namespace Lit3Mail;

internal static class ReplyRecipients
{
    public static string ReplyTarget(MailSummary original) =>
        !string.IsNullOrWhiteSpace(original.ExplicitReplyToAddresses) ? original.ExplicitReplyToAddresses :
        !string.IsNullOrWhiteSpace(original.ReplyToAddress) ? original.ReplyToAddress : original.FromAddress;

    public static (string To, string Cc) ReplyAll(MailSummary original, IEnumerable<string> ownAddresses)
    {
        var seen = ReadAddresses(ownAddresses).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var to = ReadAddresses([ReplyTarget(original), original.ToAddresses]).Where(seen.Add).ToArray();
        var cc = ReadAddresses([original.CcAddresses]).Where(seen.Add).ToArray();
        return (string.Join("; ", to), string.Join("; ", cc));
    }

    private static IEnumerable<string> ReadAddresses(IEnumerable<string> fields)
    {
        foreach (string field in fields)
        {
            foreach (string part in field.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (InternetAddressList.TryParse(part, out var addresses))
                    foreach (var address in addresses.Mailboxes)
                        yield return address.Address;
            }
        }
    }
}
