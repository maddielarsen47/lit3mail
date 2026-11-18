using MimeKit;

namespace Lit3Mail;

internal static class RecipientSeparators
{
    public static bool TryValidate(string input, out bool hasRecipient)
    {
        hasRecipient = false;
        foreach (string segment in Split(input))
        {
            if (!InternetAddressList.TryParse(segment, out var recipients))
                return false;

            foreach (var mailbox in recipients.Mailboxes)
            {
                int separator = mailbox.Address.LastIndexOf('@');
                if (separator <= 0 || separator == mailbox.Address.Length - 1)
                    return false;
                hasRecipient = true;
            }
        }
        return true;
    }

    public static IEnumerable<int> Find(string text)
    {
        bool quoted = false;
        bool inAddress = false;
        int commentDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\\' && (quoted || commentDepth > 0))
                i++;
            else if (character == '"' && commentDepth == 0)
                quoted = !quoted;
            else if (!quoted && character == '(')
                commentDepth++;
            else if (!quoted && character == ')' && commentDepth > 0)
                commentDepth--;
            else if (!quoted && commentDepth == 0)
            {
                if (character == '<')
                    inAddress = true;
                else if (character == '>')
                    inAddress = false;
                else if (!inAddress && character is ';' or ',')
                    yield return i;
            }
        }
    }

    public static IEnumerable<string> Split(string text)
    {
        int start = 0;
        foreach (int end in Find(text).Append(text.Length))
        {
            string segment = text[start..end].Trim();
            if (segment.Length > 0)
                yield return segment;
            start = end + 1;
        }
    }
}
