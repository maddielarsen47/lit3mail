using MimeKit;

namespace Lit3Mail;

internal static class ReplyRecipients
{
    public static (string To, string Cc) ReplyAll(MailSummary original, IEnumerable<string> ownAddresses)
    {
        var seen = ReadAddresses(ownAddresses).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string author = string.IsNullOrWhiteSpace(original.ReplyToAddress) ? original.FromAddress : original.ReplyToAddress;
        var to = ReadAddresses([author, original.ToAddresses]).Where(seen.Add).ToArray();
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
