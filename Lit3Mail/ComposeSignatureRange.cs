namespace Lit3Mail;

internal readonly record struct ComposeSignatureRange(int Start, int Length)
{
    public int End => Start + Length;

    public ComposeSignatureRange TrackEdit(string before, string after, int caret)
    {
        if (before == after)
            return this;

        int prefix = 0;
        int commonLength = Math.Min(before.Length, after.Length);
        while (prefix < commonLength && before[prefix] == after[prefix])
            prefix++;

        int suffix = 0;
        while (suffix < commonLength - prefix && before[^(suffix + 1)] == after[^(suffix + 1)])
            suffix++;

        int oldEnd = before.Length - suffix;
        int newEnd = after.Length - suffix;
        int delta = newEnd - oldEnd;
        int editStart = caret - Math.Max(0, delta);
        int priorEnd = caret - Math.Min(0, delta);
        if (delta != 0 && editStart >= 0 && editStart < prefix && priorEnd <= before.Length && caret <= after.Length &&
            before.AsSpan(priorEnd).SequenceEqual(after.AsSpan(caret)))
        {
            prefix = editStart;
            oldEnd = priorEnd;
            newEnd = caret;
        }
        if (oldEnd <= Start)
            return new(Start + delta, Length);
        if (prefix >= End)
            return this;
        if (prefix < Start)
            return new(newEnd, Math.Max(0, End - oldEnd));
        if (oldEnd > End)
            return new(Start, prefix - Start);
        return new(Start, Length + delta);
    }
}
