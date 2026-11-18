using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lit3Mail;

internal static class MailDefaults
{
    public const string IncomingHost = "imap.gmail.com";
    public const string OutgoingHost = "smtp.gmail.com";
}

internal sealed record AccountSettings(
    Guid Id, string Name, string Email, string ReplyTo, string Organisation, string Signature,
    string IncomingHost, int IncomingPort, string IncomingUser, bool IncomingStartTls,
    string OutgoingHost, int OutgoingPort, string OutgoingUser, bool OutgoingStartTls, bool Expanded, int Position,
    string ProfileName, bool ProfileUsesAccountName, bool IncomingUsesEmail, bool OutgoingUsesEmail,
    bool DesktopNotifications, bool EnableHtmlSignature);

internal sealed record DraftEditorState(string To, string Cc, string Bcc, string ReplyTo, string Subject,
    bool UsesDefaultReplyTo, string BodyRtf, ComposeSignatureRange Signature, ComposeHtmlQuote? HtmlQuote = null,
    ResponseOrigin? Response = null);

internal sealed record LocalDraft(Guid? SenderId, DraftEditorState Editor, MailFileAttachment[] Files,
    Guid? ServerDraftId, string? ServerConnectionKey);

internal sealed record LocalDraftEntry(Guid Id, string Subject, string Sender, DateTimeOffset Updated)
{
    public override string ToString() =>
        $"{(string.IsNullOrWhiteSpace(Subject) ? "(No subject)" : Subject)} · {Sender} · {Updated.LocalDateTime:g}";
}

internal sealed class LocalDatabase(string path)
{
    internal const int FolderCacheLimit = 1000;
    private const int MaxCachedBodyPartBytes = 4 * 1024 * 1024;
    private const int MaxOpenedPreviewCacheBytes = 128 * 1024 * 1024;
    private const int SchemaVersion = 19;

    private sealed record CachedMessage(Guid Id, bool Read, bool Star, bool Attachments,
        string Subject, string Correspondents, DateTime Date,
        string? From = null, string? To = null, string? Cc = null, string? Bcc = null,
        string? ReplyTarget = null, string? ExplicitReplyTo = null, bool Replied = false, bool Forwarded = false,
        bool HasStandaloneImages = false)
    {
        public static CachedMessage FromSummary(MailSummary summary) =>
            new(summary.Id, summary.Read, summary.Star, summary.Attachments,
                summary.Subject, summary.CorrespondentAddresses, summary.Date,
                summary.HasPreviewHeaders ? summary.FromAddress : null,
                summary.HasPreviewHeaders ? summary.ToAddresses : null,
                summary.HasPreviewHeaders ? summary.CcAddresses : null,
                summary.HasPreviewHeaders ? summary.BccAddresses : null,
                summary.HasPreviewHeaders ? summary.ReplyToAddress : null,
                summary.HasPreviewHeaders ? summary.ExplicitReplyToAddresses : null, summary.Replied, summary.Forwarded,
                summary.HasStandaloneImages);

        public MailSummary ToSummary(string folder)
        {
            if (From is not null && To is not null && Cc is not null)
                return new(Id, folder, Read, Star, Attachments, Subject, "", Date, From, To, Cc, ReplyTarget ?? From,
                    BccAddresses: Bcc ?? "", ExplicitReplyToAddresses: ExplicitReplyTo ?? "",
                    Replied: Replied, Forwarded: Forwarded, HasStandaloneImages: HasStandaloneImages);

            bool outgoing = folder is "Sent" or "Drafts";
            return new(Id, folder, Read, Star, Attachments, Subject, "", Date,
                outgoing ? "" : Correspondents, outgoing ? Correspondents : "", "", "", HasPreviewHeaders: false,
                Replied: Replied, Forwarded: Forwarded, HasStandaloneImages: HasStandaloneImages);
        }
    }

    private const string DatabaseName = "lit3mail.db";
    internal static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lit3Mail");
    private static string FolderSetting => Path.Combine(DefaultFolder, "data-folder.txt");
    private static string PendingFolderSetting => Path.Combine(DefaultFolder, "pending-data-folder.txt");

    public string DataFolder => Path.GetDirectoryName(path)!;

    public static LocalDatabase ForCurrentUser()
    {
        var current = new LocalDatabase(Path.Combine(ReadFolder(FolderSetting) ?? DefaultFolder, DatabaseName));
        try
        {
            var pending = ReadFolder(PendingFolderSetting);
            if (pending is null)
                return current;

            if (!Path.IsPathFullyQualified(pending))
                throw new IOException("The pending data folder is not an absolute path.");

            var selected = new LocalDatabase(Path.Combine(pending, DatabaseName));
            selected.LoadAccounts();
            File.Move(PendingFolderSetting, FolderSetting, overwrite: true);
            return selected;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException
            or InvalidOperationException or FormatException or ArgumentException)
        {
            ThemedPopup.Show($"Could not open the selected local data folder. Lit3Mail will use {current.DataFolder} and retry next time it opens.\n\n{error.Message}",
                "Lit3Mail", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return current;
        }
    }

    public string? PendingDataFolder
    {
        get
        {
            try { return ReadFolder(PendingFolderSetting); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public void SetDataFolder(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(folder, DataFolder, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(PendingFolderSetting);
            return;
        }

        Directory.CreateDirectory(DefaultFolder);
        string temporary = PendingFolderSetting + ".tmp";
        File.WriteAllText(temporary, folder);
        File.Move(temporary, PendingFolderSetting, overwrite: true);
    }

    private static string? ReadFolder(string setting) =>
        File.Exists(setting) ? File.ReadAllText(setting) : null;

    public IReadOnlyList<AccountSettings> LoadAccounts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        long version = (long)command.ExecuteScalar()!;
        if (version == 0)
        {
            command.CommandText = """
                CREATE TABLE accounts (
                    id TEXT PRIMARY KEY NOT NULL,
                    name TEXT NOT NULL,
                    email TEXT NOT NULL,
                    reply_to TEXT NOT NULL,
                    organisation TEXT NOT NULL,
                    signature TEXT NOT NULL,
                    incoming_host TEXT NOT NULL,
                    incoming_port INTEGER NOT NULL CHECK (incoming_port BETWEEN 1 AND 65535),
                    incoming_user TEXT NOT NULL,
                    incoming_starttls INTEGER NOT NULL,
                    outgoing_host TEXT NOT NULL,
                    outgoing_port INTEGER NOT NULL CHECK (outgoing_port BETWEEN 1 AND 65535),
                    outgoing_user TEXT NOT NULL,
                    outgoing_starttls INTEGER NOT NULL,
                    expanded INTEGER NOT NULL
                );
                INSERT INTO accounts VALUES (
                    $id, 'Your Name', '', '', '', '', $incoming, 993, '', 0, $outgoing, 465, '', 0, 1
                );
                PRAGMA user_version = 2;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            AddDefaultHosts(command);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
        else if (version == 1)
        {
            command.CommandText = """
                UPDATE accounts SET incoming_host = $incoming WHERE trim(incoming_host) = '';
                UPDATE accounts SET outgoing_host = $outgoing WHERE trim(outgoing_host) = '';
                PRAGMA user_version = 2;
                """;
            AddDefaultHosts(command);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
        else if (version > SchemaVersion)
            throw new InvalidOperationException("This database was created by a newer version of Lit3Mail.");

        if (version < 3)
        {
            command.CommandText = """
                CREATE TABLE folder_cache (
                    account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    folder TEXT NOT NULL,
                    connection_key TEXT NOT NULL,
                    summaries TEXT NOT NULL,
                    PRIMARY KEY (account_id, folder)
                );
                PRAGMA user_version = 3;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 4)
        {
            command.CommandText = """
                CREATE TABLE message_body_cache (
                    account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    folder TEXT NOT NULL,
                    connection_key TEXT NOT NULL,
                    message_id TEXT NOT NULL,
                    body TEXT NOT NULL,
                    html TEXT,
                    PRIMARY KEY (account_id, folder, connection_key, message_id)
                );
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }

        else if (version == 4)
        {
            command.CommandText = """
                ALTER TABLE message_body_cache ADD COLUMN html TEXT;
                PRAGMA user_version = 5;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 6)
        {
            command.CommandText = """
                ALTER TABLE folder_cache ADD COLUMN revision TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 6;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 7)
        {
            command.CommandText = """
                CREATE TABLE known_addresses (
                    account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    connection_key TEXT NOT NULL,
                    address TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (account_id, connection_key, address)
                );
                PRAGMA user_version = 7;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 8)
        {
            command.CommandText = """
                CREATE TABLE draft_editor_cache (
                    account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    connection_key TEXT NOT NULL,
                    draft_id TEXT NOT NULL,
                    editor_json TEXT NOT NULL,
                    PRIMARY KEY (account_id, connection_key, draft_id)
                );
                PRAGMA user_version = 8;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 9)
        {
            command.CommandText = """
                CREATE TABLE local_drafts (
                    id TEXT PRIMARY KEY NOT NULL,
                    subject TEXT NOT NULL,
                    sender TEXT NOT NULL,
                    updated INTEGER NOT NULL,
                    data BLOB NOT NULL
                );
                PRAGMA user_version = 9;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 10)
        {
            command.CommandText = """
                ALTER TABLE accounts ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 10;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 11)
        {
            command.CommandText = """
                ALTER TABLE accounts ADD COLUMN profile_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE accounts ADD COLUMN profile_uses_account_name INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE accounts ADD COLUMN incoming_uses_email INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE accounts ADD COLUMN outgoing_uses_email INTEGER NOT NULL DEFAULT 1;
                UPDATE accounts SET profile_name = name;
                PRAGMA user_version = 11;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 12)
        {
            command.CommandText = """
                CREATE TABLE folder_counts_cache (
                    account_id TEXT PRIMARY KEY NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    connection_key TEXT NOT NULL,
                    counts TEXT NOT NULL
                );
                PRAGMA user_version = 12;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 13)
        {
            command.CommandText = """
                ALTER TABLE accounts ADD COLUMN desktop_notifications INTEGER NOT NULL DEFAULT 1;
                PRAGMA user_version = 13;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 14)
        {
            command.CommandText = """
                CREATE TABLE update_check (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    last_checked_utc INTEGER NOT NULL,
                    available_version TEXT
                );
                PRAGMA user_version = 14;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 15)
        {
            command.CommandText = """
                ALTER TABLE accounts ADD COLUMN automatically_show_remote_content INTEGER NOT NULL DEFAULT 1;
                PRAGMA user_version = 15;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 16)
        {
            command.CommandText = """
                ALTER TABLE accounts ADD COLUMN enable_html_signature INTEGER NOT NULL DEFAULT 1;
                PRAGMA user_version = 16;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 17)
        {
            command.CommandText = """
                ALTER TABLE message_body_cache ADD COLUMN inline_images BLOB;
                ALTER TABLE message_body_cache ADD COLUMN preview_cached_at INTEGER;
                PRAGMA user_version = 17;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 18)
        {
            command.CommandText = """
                CREATE TABLE app_preferences (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    automatically_show_remote_content INTEGER NOT NULL DEFAULT 1
                );
                INSERT INTO app_preferences (id, automatically_show_remote_content)
                SELECT 1, NOT EXISTS (SELECT 1 FROM accounts WHERE automatically_show_remote_content = 0);
                ALTER TABLE accounts DROP COLUMN automatically_show_remote_content;
                PRAGMA user_version = 18;
                """;
            command.ExecuteNonQuery();
        }

        if (version < 19)
        {
            command.CommandText = """
                ALTER TABLE app_preferences ADD COLUMN theme INTEGER NOT NULL DEFAULT 0 CHECK (theme BETWEEN 0 AND 2);
                PRAGMA user_version = 19;
                """;
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            SELECT id, name, email, reply_to, organisation, signature,
                   incoming_host, incoming_port, incoming_user, incoming_starttls,
                   outgoing_host, outgoing_port, outgoing_user, outgoing_starttls, expanded, position,
                   profile_name, profile_uses_account_name, incoming_uses_email, outgoing_uses_email, desktop_notifications,
                   enable_html_signature
            FROM accounts ORDER BY position, rowid
            """;
        var accounts = new List<AccountSettings>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                accounts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt32(7), reader.GetString(8), reader.GetBoolean(9), reader.GetString(10),
                    reader.GetInt32(11), reader.GetString(12), reader.GetBoolean(13), reader.GetBoolean(14),
                    reader.GetInt32(15), reader.GetString(16), reader.GetBoolean(17),
                    reader.GetBoolean(18), reader.GetBoolean(19), reader.GetBoolean(20), reader.GetBoolean(21)));
        }
        transaction.Commit();
        return accounts;
    }

    public int LoadTheme()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT theme FROM app_preferences WHERE id = 1";
        return command.ExecuteScalar() is long value && value is >= 0 and <= 2 ? (int)value : 0;
    }

    public void SaveTheme(int theme)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(theme);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(theme, 2);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_preferences SET theme = $theme WHERE id = 1";
        command.Parameters.AddWithValue("$theme", theme);
        command.ExecuteNonQuery();
    }

    public bool LoadAutomaticRemoteContent()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT automatically_show_remote_content FROM app_preferences WHERE id = 1";
        return command.ExecuteScalar() is not long value || value != 0;
    }

    public void SaveAutomaticRemoteContent(bool enabled)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_preferences SET automatically_show_remote_content = $enabled WHERE id = 1";
        command.Parameters.AddWithValue("$enabled", enabled);
        command.ExecuteNonQuery();
    }

    public (DateTimeOffset LastChecked, Version? AvailableVersion) LoadUpdateCheck()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_checked_utc, available_version FROM update_check WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (DateTimeOffset.MinValue, null);
        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0));
        Version? version = !reader.IsDBNull(1) && Version.TryParse(reader.GetString(1), out var parsed) ? parsed : null;
        return (checkedAt, version);
    }

    public void SaveUpdateCheck(DateTimeOffset checkedAt, Version? availableVersion)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO update_check (id, last_checked_utc, available_version) VALUES (1, $checked, $version)
            ON CONFLICT(id) DO UPDATE SET last_checked_utc = excluded.last_checked_utc,
                                          available_version = excluded.available_version
            """;
        command.Parameters.AddWithValue("$checked", checkedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$version", (object?)availableVersion?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void SaveAccounts(IEnumerable<AccountSettings> updated, IEnumerable<Guid> removed)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        foreach (Guid id in removed)
        {
            command.CommandText = "DELETE FROM accounts WHERE id = $id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }

        foreach (var account in updated)
        {
            command.CommandText = """
                INSERT INTO accounts (
                    id, name, email, reply_to, organisation, signature,
                    incoming_host, incoming_port, incoming_user, incoming_starttls,
                    outgoing_host, outgoing_port, outgoing_user, outgoing_starttls, expanded, position,
                    profile_name, profile_uses_account_name, incoming_uses_email, outgoing_uses_email, desktop_notifications,
                   enable_html_signature
                ) VALUES (
                    $id, $name, $email, $replyTo, $organisation, $signature,
                    $incomingHost, $incomingPort, $incomingUser, $incomingStartTls,
                    $outgoingHost, $outgoingPort, $outgoingUser, $outgoingStartTls, $expanded, $position,
                    $profileName, $profileUsesAccountName, $incomingUsesEmail, $outgoingUsesEmail, $desktopNotifications,
                    $enableHtmlSignature
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, email = excluded.email, reply_to = excluded.reply_to,
                    organisation = excluded.organisation, signature = excluded.signature,
                    incoming_host = excluded.incoming_host, incoming_port = excluded.incoming_port,
                    incoming_user = excluded.incoming_user, incoming_starttls = excluded.incoming_starttls,
                    outgoing_host = excluded.outgoing_host, outgoing_port = excluded.outgoing_port,
                    outgoing_user = excluded.outgoing_user, outgoing_starttls = excluded.outgoing_starttls,
                    expanded = excluded.expanded, position = excluded.position,
                    profile_name = excluded.profile_name, profile_uses_account_name = excluded.profile_uses_account_name,
                    incoming_uses_email = excluded.incoming_uses_email, outgoing_uses_email = excluded.outgoing_uses_email,
                    desktop_notifications = excluded.desktop_notifications,
                    enable_html_signature = excluded.enable_html_signature
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", account.Id.ToString());
            command.Parameters.AddWithValue("$name", account.Name);
            command.Parameters.AddWithValue("$email", account.Email);
            command.Parameters.AddWithValue("$replyTo", account.ReplyTo);
            command.Parameters.AddWithValue("$organisation", account.Organisation);
            command.Parameters.AddWithValue("$signature", account.Signature);
            command.Parameters.AddWithValue("$incomingHost", account.IncomingHost);
            command.Parameters.AddWithValue("$incomingPort", account.IncomingPort);
            command.Parameters.AddWithValue("$incomingUser", account.IncomingUser);
            command.Parameters.AddWithValue("$incomingStartTls", account.IncomingStartTls);
            command.Parameters.AddWithValue("$outgoingHost", account.OutgoingHost);
            command.Parameters.AddWithValue("$outgoingPort", account.OutgoingPort);
            command.Parameters.AddWithValue("$outgoingUser", account.OutgoingUser);
            command.Parameters.AddWithValue("$outgoingStartTls", account.OutgoingStartTls);
            command.Parameters.AddWithValue("$expanded", account.Expanded);
            command.Parameters.AddWithValue("$position", account.Position);
            command.Parameters.AddWithValue("$profileName", account.ProfileName);
            command.Parameters.AddWithValue("$profileUsesAccountName", account.ProfileUsesAccountName);
            command.Parameters.AddWithValue("$incomingUsesEmail", account.IncomingUsesEmail);
            command.Parameters.AddWithValue("$outgoingUsesEmail", account.OutgoingUsesEmail);
            command.Parameters.AddWithValue("$desktopNotifications", account.DesktopNotifications);
            command.Parameters.AddWithValue("$enableHtmlSignature", account.EnableHtmlSignature);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<MailSummary> LoadFolderCache(Guid account, string folder, string connectionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT summaries FROM folder_cache
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        return command.ExecuteScalar() is string json
            ? (JsonSerializer.Deserialize<CachedMessage[]>(json) ?? []).Select(item => item.ToSummary(folder)).ToArray()
            : [];
    }

    public IReadOnlyList<MailSummary> SearchFolderCache(Guid account, string folder, string connectionKey,
        string query, CancellationToken token)
    {
        var terms = System.Text.RegularExpressions.Regex.Matches(query, "\"([^\"]*)\"|[^\\s\"]+")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Value)
            .Where(term => !string.IsNullOrWhiteSpace(term)).ToArray();
        var matches = new List<MailSummary>();
        var remaining = new Dictionary<Guid, (MailSummary Mail, string[] Terms)>();
        foreach (var mail in LoadFolderCache(account, folder, connectionKey))
        {
            token.ThrowIfCancellationRequested();
            string[] missing = terms.Where(term => !Contains(mail.Subject, term) &&
                !Contains(mail.CorrespondentName, term) && !Contains(mail.CorrespondentAddresses, term) &&
                !Contains(mail.FromAddress, term) && !Contains(mail.ToAddresses, term) &&
                !Contains(mail.CcAddresses, term) && !Contains(mail.ReplyToAddress, term)).ToArray();
            if (missing.Length == 0)
                matches.Add(mail);
            else
                remaining[mail.Id] = (mail, missing);
        }

        if (remaining.Count == 0)
            return matches;

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT message_id, body FROM message_body_cache
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection
              AND octet_length(body) <= $maxPartBytes
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        command.Parameters.AddWithValue("$maxPartBytes", MaxCachedBodyPartBytes);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (Guid.TryParse(reader.GetString(0), out var id) && remaining.TryGetValue(id, out var candidate))
            {
                string body = reader.GetString(1);
                if (candidate.Terms.All(term => Contains(body, term)))
                    matches.Add(candidate.Mail);
            }
        }
        return matches;

        static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    public void SaveKnownAddresses(Guid account, string connectionKey, IEnumerable<string> addressLists)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string list in addressLists)
        {
            foreach (string part in list.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!MimeKit.InternetAddressList.TryParse(part, out var parsed))
                    continue;
                foreach (var address in parsed.Mailboxes)
                    if (address.Address.Contains('@'))
                        addresses.Add(address.Address);
            }
        }
        if (addresses.Count == 0)
            return;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO known_addresses (account_id, connection_key, address)
            VALUES ($account, $connection, $address)
            """;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        var parameter = command.Parameters.AddWithValue("$address", "");
        command.Prepare();
        foreach (string address in addresses)
        {
            parameter.Value = address;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<string> FindKnownAddresses(Guid account, string connectionKey, string prefix)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT address FROM known_addresses
            WHERE account_id = $account AND connection_key = $connection AND address LIKE $prefix ESCAPE '\'
            ORDER BY address COLLATE NOCASE LIMIT 30
            """;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        command.Parameters.AddWithValue("$prefix", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        using var reader = command.ExecuteReader();
        var addresses = new List<string>();
        while (reader.Read())
            addresses.Add(reader.GetString(0));
        return addresses;
    }

    public string? GetFolderCacheRevision(Guid account, string folder)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT revision FROM folder_cache WHERE account_id = $account AND folder = $folder";
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$folder", folder);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyDictionary<string, FolderCount> LoadCachedFolderCounts(Guid account, string connectionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT counts FROM folder_counts_cache WHERE account_id = $account AND connection_key = $connection
            """;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        if (command.ExecuteScalar() is string json &&
            JsonSerializer.Deserialize<Dictionary<string, FolderCount>>(json) is { } counts)
            return counts;

        return LoadCachedGmailLabels(account, connectionKey);
    }

    public void SaveCachedFolderCounts(Guid account, string connectionKey, IReadOnlyDictionary<string, FolderCount> counts)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            INSERT INTO folder_counts_cache (account_id, connection_key, counts)
            VALUES ($account, $connection, $counts)
            ON CONFLICT(account_id) DO UPDATE SET connection_key = excluded.connection_key, counts = excluded.counts
            """;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(counts));
        command.ExecuteNonQuery();
    }

    private IReadOnlyDictionary<string, FolderCount> LoadCachedGmailLabels(Guid account, string connectionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT folder FROM folder_cache
            WHERE account_id = $account AND connection_key = $connection AND folder LIKE $prefix
            """;
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        command.Parameters.AddWithValue("$prefix", ServerMailbox.GmailLabelPrefix + "%");
        using var reader = command.ExecuteReader();
        var labels = new Dictionary<string, FolderCount>();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            string name = key[ServerMailbox.GmailLabelPrefix.Length..];
            if (!ServerMailbox.IsExcludedGmailLabel(name))
                labels[key] = new(0, 0, name, HasCounts: false);
        }
        return labels;
    }

    public void SaveFolderCache(Guid account, string folder, string connectionKey, IReadOnlyList<MailSummary> summaries,
        bool onlyIfUnchanged = false, string? expectedRevision = null)
    {
        var newest = summaries.OrderByDescending(summary => summary.Date).Take(FolderCacheLimit)
            .Select(CachedMessage.FromSummary).ToArray();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            INSERT INTO folder_cache (account_id, folder, connection_key, summaries, revision)
            SELECT $account, $folder, $connection, $summaries, $newRevision
            WHERE NOT $onlyIfUnchanged OR $expectedRevision IS NULL OR EXISTS (
                SELECT 1 FROM folder_cache
                WHERE account_id = $account AND folder = $folder AND revision = $expectedRevision
            )
            ON CONFLICT(account_id, folder) DO UPDATE SET
                connection_key = excluded.connection_key, summaries = excluded.summaries, revision = excluded.revision
            WHERE NOT $onlyIfUnchanged OR folder_cache.revision = $expectedRevision
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        command.Parameters.AddWithValue("$summaries", JsonSerializer.Serialize(newest));
        command.Parameters.AddWithValue("$newRevision", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$onlyIfUnchanged", onlyIfUnchanged);
        command.Parameters.AddWithValue("$expectedRevision", (object?)expectedRevision ?? DBNull.Value);
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() > 0)
        {
            command.CommandText = """
                DELETE FROM message_body_cache
                WHERE account_id = $account AND folder = $folder AND
                    (connection_key <> $connection OR
                     (inline_images IS NULL AND message_id NOT IN (SELECT value FROM json_each($retained))));
                """;
            command.Parameters.AddWithValue("$retained", JsonSerializer.Serialize(newest.Select(message => message.Id)));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void UpdateCachedMessageFlag(Guid account, string folder, string connectionKey, Guid id, bool readFlag, bool value) =>
        UpdateCachedMessageFlag(account, folder, connectionKey, id, readFlag ? "Read" : "Star", value);

    public void UpdateCachedResponseFlag(ResponseOrigin response) =>
        UpdateCachedMessageFlag(response.AccountId, response.Folder, response.ConnectionKey, response.MessageId,
            response.IsReply ? "Replied" : "Forwarded", value: true);

    private void UpdateCachedMessageFlag(Guid account, string folder, string connectionKey, Guid id, string flag, bool value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            UPDATE folder_cache
            SET summaries = json_set(summaries, (
                SELECT '$[' || item.key || '].' || $flag
                FROM json_each(summaries) AS item
                WHERE json_extract(item.value, '$.Id') = $message
                LIMIT 1
            ), json($value)), revision = $revision
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection
              AND json_valid(summaries)
              AND EXISTS (SELECT 1 FROM json_each(summaries) WHERE json_extract(value, '$.Id') = $message)
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        command.Parameters.AddWithValue("$message", id.ToString());
        command.Parameters.AddWithValue("$flag", flag);
        command.Parameters.AddWithValue("$value", value ? "true" : "false");
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
        command.ExecuteNonQuery();
    }

    public void RemoveMessageCaches(Guid account, string folder, string connectionKey, IReadOnlyCollection<Guid> ids,
        string? destination = null, IReadOnlyList<MailSummary>? movedMessages = null,
        IReadOnlyDictionary<Guid, Guid>? destinationIds = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 1;
        AddFolderParameters(command, account, folder, connectionKey);
        command.Parameters.AddWithValue("$destination", (object?)destination ?? DBNull.Value);
        if (destination is not null && destinationIds is { Count: > 0 })
        {
            command.CommandText = """
                INSERT INTO message_body_cache (account_id, folder, connection_key, message_id, body, html,
                                                inline_images, preview_cached_at)
                SELECT source.account_id, $destination, source.connection_key, moved.value, source.body, source.html,
                       source.inline_images, source.preview_cached_at
                FROM json_each($destinationIds) AS moved
                JOIN message_body_cache AS source ON source.message_id = moved.key
                WHERE source.account_id = $account AND source.folder = $folder AND source.connection_key = $connection
                ON CONFLICT(account_id, folder, connection_key, message_id) DO UPDATE SET
                    body = excluded.body, html = excluded.html, inline_images = excluded.inline_images,
                    preview_cached_at = excluded.preview_cached_at
                WHERE message_body_cache.inline_images IS NULL
                  AND (excluded.inline_images IS NOT NULL OR
                       (message_body_cache.html IS NULL AND excluded.html IS NOT NULL));
                """;
            command.Parameters.AddWithValue("$destinationIds", JsonSerializer.Serialize(destinationIds));
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            UPDATE folder_cache
            SET revision = $revision,
                summaries = CASE WHEN folder = $folder THEN (
                    SELECT json_group_array(json(message.value))
                    FROM json_each(CASE WHEN json_valid(summaries) THEN summaries ELSE '[]' END) AS message
                    WHERE json_extract(message.value, '$.Id') NOT IN (SELECT value FROM json_each($ids))
                ) ELSE summaries END
            WHERE account_id = $account AND connection_key = $connection AND folder IN ($folder, $destination);
            DELETE FROM message_body_cache
            WHERE account_id = $account AND connection_key = $connection AND folder = $folder
              AND message_id IN (SELECT value FROM json_each($ids));
            DELETE FROM draft_editor_cache
            WHERE account_id = $account AND connection_key = $connection AND $folder = 'Drafts'
              AND draft_id IN (SELECT value FROM json_each($ids))
            """;
        command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        command.ExecuteNonQuery();
        if (destination is not null && movedMessages is { Count: > 0 })
        {
            command.CommandText = """
                INSERT INTO folder_cache (account_id, folder, connection_key, summaries, revision)
                VALUES ($account, $destination, $connection, $moved, $revision)
                ON CONFLICT(account_id, folder) DO UPDATE SET
                    connection_key = excluded.connection_key,
                    revision = excluded.revision,
                    summaries = (
                        SELECT json_group_array(json(value)) FROM (
                            SELECT value FROM (
                                SELECT value FROM json_each($moved)
                                UNION ALL
                                SELECT value FROM json_each(CASE
                                    WHEN folder_cache.connection_key = $connection AND json_valid(folder_cache.summaries)
                                    THEN folder_cache.summaries ELSE '[]' END)
                                WHERE json_extract(value, '$.Id') NOT IN (
                                    SELECT json_extract(value, '$.Id') FROM json_each($moved)
                                )
                            )
                            ORDER BY julianday(json_extract(value, '$.Date')) DESC
                            LIMIT $limit
                        )
                    );
                """;
            var newest = movedMessages.OrderByDescending(mail => mail.Date).Take(FolderCacheLimit)
                .Select(CachedMessage.FromSummary);
            command.Parameters.AddWithValue("$moved", JsonSerializer.Serialize(newest));
            command.Parameters.AddWithValue("$limit", FolderCacheLimit);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<LocalDraftEntry> ListLocalDrafts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, subject, sender, updated FROM local_drafts ORDER BY updated DESC, id";
        using var reader = command.ExecuteReader();
        var drafts = new List<LocalDraftEntry>();
        while (reader.Read())
            drafts.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        return drafts;
    }

    public LocalDraft? LoadLocalDraft(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM local_drafts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is byte[] data ? JsonSerializer.Deserialize<LocalDraft>(data) : null;
    }

    public void SaveLocalDraft(Guid id, string sender, LocalDraft draft)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(draft);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_drafts (id, subject, sender, updated, data)
            VALUES ($id, $subject, $sender, $updated, $data)
            ON CONFLICT(id) DO UPDATE SET subject = excluded.subject, sender = excluded.sender,
                updated = excluded.updated, data = excluded.data
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$subject", draft.Editor.Subject);
        command.Parameters.AddWithValue("$sender", sender);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$data", data);
        command.ExecuteNonQuery();
    }

    public void DeleteLocalDraft(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_drafts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }

    public DraftEditorState? LoadDraftEditorState(Guid account, string connectionKey, Guid draft)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT editor_json FROM draft_editor_cache
            WHERE account_id = $account AND connection_key = $connection AND draft_id = $draft
            """;
        AddDraftParameters(command, account, connectionKey, draft);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<DraftEditorState>(json) : null;
    }

    public void SaveDraftEditorState(Guid account, string connectionKey, Guid draft, DraftEditorState state)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO draft_editor_cache (account_id, connection_key, draft_id, editor_json)
            VALUES ($account, $connection, $draft, $json)
            ON CONFLICT(account_id, connection_key, draft_id) DO UPDATE SET editor_json = excluded.editor_json
            """;
        AddDraftParameters(command, account, connectionKey, draft);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
    }

    private static void AddDraftParameters(SqliteCommand command, Guid account, string connectionKey, Guid draft)
    {
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$connection", connectionKey);
        command.Parameters.AddWithValue("$draft", draft.ToString());
    }

    public HashSet<Guid> GetCachedMessageBodies(Guid account, string folder, string connectionKey)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT message_id FROM message_body_cache
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection AND html IS NOT NULL
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        using var reader = command.ExecuteReader();
        var ids = new HashSet<Guid>();
        while (reader.Read())
        {
            if (Guid.TryParse(reader.GetString(0), out Guid id))
                ids.Add(id);
        }
        return ids;
    }

    public MailContent? LoadMessageBody(Guid account, string folder, string connectionKey, Guid message)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            SELECT body, html, inline_images FROM message_body_cache
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection AND message_id = $message
              AND octet_length(body) <= $maxPartBytes AND (html IS NULL OR octet_length(html) <= $maxPartBytes)
            """;
        AddFolderParameters(command, account, folder, connectionKey);
        command.Parameters.AddWithValue("$maxPartBytes", MaxCachedBodyPartBytes);
        command.Parameters.AddWithValue("$message", message.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        InlineMailImage[]? images = null;
        if (!reader.IsDBNull(2))
        {
            try { images = JsonSerializer.Deserialize<InlineMailImage[]>((byte[])reader.GetValue(2)); }
            catch (JsonException) { }
        }
        return new MailContent(reader.GetString(0), Html: reader.IsDBNull(1) ? null : reader.GetString(1), InlineImages: images);
    }

    public void SaveMessageBody(Guid account, string folder, string connectionKey, Guid message, MailContent content,
        bool completePreview = false) =>
        SaveMessageBodies(account, folder, connectionKey, [(message, content)], false, completePreview);

    public void SaveMessageBodies(Guid account, string folder, string connectionKey,
        IReadOnlyList<(Guid Id, MailContent Content)> bodies, bool onlyIfMissing, bool completePreview = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 1;
        AddFolderParameters(command, account, folder, connectionKey);
        command.CommandText = """
            SELECT json_extract(value, '$.Id')
            FROM folder_cache, json_each(CASE WHEN json_valid(summaries) THEN summaries ELSE '[]' END)
            WHERE account_id = $account AND folder = $folder AND connection_key = $connection
            """;
        var retained = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && Guid.TryParse(reader.GetString(0), out var id))
                    retained.Add(id);
            }
        }
        if (retained.Count == 0 && !completePreview)
            return;

        command.CommandText = """
            INSERT INTO message_body_cache (account_id, folder, connection_key, message_id, body, html,
                                            inline_images, preview_cached_at)
            VALUES ($account, $folder, $connection, $message, $body, $html, $images, $cachedAt)
            ON CONFLICT(account_id, folder, connection_key, message_id) DO UPDATE SET
                body = excluded.body, html = excluded.html, inline_images = excluded.inline_images,
                preview_cached_at = excluded.preview_cached_at
            WHERE excluded.inline_images IS NOT NULL OR
                (message_body_cache.inline_images IS NULL AND (NOT $onlyIfMissing OR message_body_cache.html IS NULL))
            """;
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var body = command.Parameters.Add("$body", SqliteType.Text);
        var html = command.Parameters.Add("$html", SqliteType.Text);
        var images = command.Parameters.Add("$images", SqliteType.Blob);
        var cachedAt = command.Parameters.Add("$cachedAt", SqliteType.Integer);
        command.Parameters.AddWithValue("$onlyIfMissing", onlyIfMissing);
        foreach (var (id, content) in bodies)
        {
            if (!completePreview && !retained.Contains(id))
                continue;
            message.Value = id.ToString();
            body.Value = content.Body;
            html.Value = content.Html ?? "";
            images.Value = completePreview ? JsonSerializer.SerializeToUtf8Bytes(content.InlineImages ?? []) : DBNull.Value;
            cachedAt.Value = completePreview ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : DBNull.Value;
            command.ExecuteNonQuery();
        }
        if (completePreview)
        {
            command.CommandText = """
                DELETE FROM message_body_cache AS preview WHERE rowid IN (
                    SELECT rowid FROM (
                        SELECT rowid, sum(length(inline_images) + octet_length(body) + octet_length(html)) OVER (
                            ORDER BY preview_cached_at DESC, rowid DESC
                        ) AS bytes
                        FROM message_body_cache WHERE inline_images IS NOT NULL
                    ) WHERE bytes > $previewBudget
                ) AND NOT EXISTS (
                    SELECT 1 FROM folder_cache AS retained,
                        json_each(CASE WHEN json_valid(retained.summaries) THEN retained.summaries ELSE '[]' END) AS summary
                    WHERE retained.account_id = preview.account_id AND retained.folder = preview.folder
                      AND retained.connection_key = preview.connection_key
                      AND json_extract(summary.value, '$.Id') = preview.message_id
                );
                UPDATE message_body_cache SET inline_images = NULL, preview_cached_at = NULL WHERE rowid IN (
                    SELECT rowid FROM (
                        SELECT rowid, sum(length(inline_images) + octet_length(body) + octet_length(html)) OVER (
                            ORDER BY preview_cached_at DESC, rowid DESC
                        ) AS bytes
                        FROM message_body_cache WHERE inline_images IS NOT NULL
                    ) WHERE bytes > $previewBudget
                );
                """;
            command.Parameters.AddWithValue("$previewBudget", MaxOpenedPreviewCacheBytes);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void AddFolderParameters(SqliteCommand command, Guid account, string folder, string connectionKey)
    {
        command.Parameters.AddWithValue("$account", account.ToString());
        command.Parameters.AddWithValue("$folder", folder);
        command.Parameters.AddWithValue("$connection", connectionKey);
    }

    private static void AddDefaultHosts(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$incoming", MailDefaults.IncomingHost);
        command.Parameters.AddWithValue("$outgoing", MailDefaults.OutgoingHost);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, DefaultTimeout = 3, ForeignKeys = true
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}













