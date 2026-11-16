namespace Lit3Mail;

internal sealed class RecipientAutocomplete
{
    private readonly Control host;
    private readonly LocalDatabase database;
    private readonly ListBox suggestions = new()
    {
        Visible = false, TabStop = false, IntegralHeight = false, AccessibleName = "Email address suggestions"
    };
    private TextBox? activeField;
    private (Guid Id, string Key)? account;
    private CancellationTokenSource? lookup;
    private bool accepting;

    public RecipientAutocomplete(Control host, LocalDatabase database, params TextBox[] fields)
    {
        this.host = host;
        this.database = database;
        host.Controls.Add(suggestions);
        host.Disposed += (_, _) => lookup?.Cancel();
        host.VisibleChanged += (_, _) => { if (!host.Visible) HideSuggestions(); };
        suggestions.MouseDown += (_, e) =>
        {
            int index = suggestions.IndexFromPoint(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0)
                Accept(index);
        };

        foreach (var field in fields)
        {
            field.GotFocus += (_, _) => _ = UpdateAsync(field);
            field.TextChanged += (_, _) => _ = UpdateAsync(field);
            field.MouseClick += (_, _) => _ = UpdateAsync(field);
            field.KeyUp += (_, e) =>
            {
                if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End)
                    _ = UpdateAsync(field);
            };
            field.PreviewKeyDown += (_, e) =>
            {
                if (field == activeField && suggestions.Visible && e.Modifiers == Keys.None &&
                    e.KeyCode is Keys.Tab or Keys.Enter or Keys.Up or Keys.Down or Keys.Escape)
                    e.IsInputKey = true;
            };
            field.KeyDown += (_, e) => HandleKey(field, e);
            field.Leave += (_, _) =>
            {
                host.PostIfHandleAvailable(() =>
                {
                    if (activeField == field && !field.Focused && !suggestions.ContainsFocus)
                        HideSuggestions();
                });
            };
        }
    }

    public void SetColors(Color surface, Color ink)
    {
        suggestions.BackColor = surface;
        suggestions.ForeColor = ink;
    }

    public void SetAccount(Guid? id, string? connectionKey)
    {
        HideSuggestions();
        account = id is { } accountId && connectionKey is not null ? (accountId, connectionKey) : null;
    }

    private async Task UpdateAsync(TextBox field)
    {
        if (accepting || !field.Focused || account is not { } context || host.IsDisposed)
            return;

        HideSuggestions();
        var token = ReadToken(field.Text, field.SelectionStart);
        if (string.IsNullOrWhiteSpace(token.Prefix))
            return;

        using var pending = new CancellationTokenSource();
        lookup = pending;
        activeField = field;
        try
        {
            await Task.Delay(100, pending.Token);
            var matches = await Task.Run(() => database.FindKnownAddresses(context.Id, context.Key, token.Prefix), pending.Token);
            if (pending.IsCancellationRequested || host.IsDisposed || !field.Focused ||
                ReadToken(field.Text, field.SelectionStart) != token)
                return;

            var otherAddresses = RecipientSeparators.Split(field.Text.Remove(token.Start, token.End - token.Start))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = matches.Where(address => !otherAddresses.Contains(address)).Take(8).ToArray();
            if (options.Length == 0)
                return;

            suggestions.Items.Clear();
            suggestions.Items.AddRange(options);
            suggestions.SelectedIndex = 0;
            var position = host.PointToClient(field.PointToScreen(new Point(0, field.Height + 2)));
            suggestions.Bounds = new Rectangle(position.X, position.Y, field.Width,
                options.Length * suggestions.ItemHeight + 4);
            suggestions.BringToFront();
            suggestions.Show();
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        finally
        {
            if (ReferenceEquals(lookup, pending))
                lookup = null;
        }
    }

    private void HandleKey(TextBox field, KeyEventArgs e)
    {
        if (field != activeField)
            return;
        if (e.KeyCode == Keys.Escape && (suggestions.Visible || lookup is not null))
        {
            HideSuggestions();
            e.SuppressKeyPress = true;
            return;
        }
        if (!suggestions.Visible || e.Modifiers != Keys.None)
            return;

        switch (e.KeyCode)
        {
            case Keys.Up:
            case Keys.Down:
                int change = e.KeyCode == Keys.Down ? 1 : -1;
                suggestions.SelectedIndex = Math.Clamp(suggestions.SelectedIndex + change, 0, suggestions.Items.Count - 1);
                break;
            case Keys.Tab:
            case Keys.Enter:
                Accept(suggestions.SelectedIndex);
                break;
            default:
                return;
        }
        e.SuppressKeyPress = true;
    }

    private void Accept(int index)
    {
        if (activeField is not { } field || index < 0 || suggestions.Items[index] is not string address)
            return;

        var completion = CompleteAddress(field.Text, field.SelectionStart, address);
        accepting = true;
        try
        {
            HideSuggestions();
            field.Text = completion.Text;
            field.SelectionStart = completion.Caret;
            field.Focus();
        }
        finally
        {
            accepting = false;
        }
    }

    private void HideSuggestions()
    {
        lookup?.Cancel();
        lookup = null;
        activeField = null;
        if (!suggestions.IsDisposed)
            suggestions.Hide();
    }

    private static (int Start, int End, string Prefix) ReadToken(string text, int caret)
    {
        int start = 0;
        int end = text.Length;
        foreach (int separator in RecipientSeparators.Find(text))
        {
            if (separator >= caret)
            {
                end = separator;
                break;
            }
            start = separator + 1;
        }
        return (start, end, text[start..caret].Trim());
    }

    internal static (string Text, int Caret) CompleteAddress(string text, int caret, string address)
    {
        var token = ReadToken(text, caret);
        string prefix = text[..token.Start];
        if (prefix.Length > 0)
            prefix += " ";
        char separator = token.End < text.Length ? text[token.End] :
            token.Start > 0 ? text[token.Start - 1] : ';';
        string inserted = prefix + address + separator + " ";
        string suffix = token.End < text.Length ? text[(token.End + 1)..].TrimStart() : "";
        return (inserted + suffix, inserted.Length);
    }
}

