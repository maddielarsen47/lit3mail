using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lit3Mail;

internal static class LocalDataReset
{
    internal sealed record Request(string DatabasePath, Guid[] AccountIds);
    private const string RequestFileName = "clear-local-data.json";

    public static void Schedule(LocalDatabase database, IEnumerable<Guid> accounts)
    {
        Directory.CreateDirectory(LocalDatabase.DefaultFolder);
        var request = new Request(Path.Combine(database.DataFolder, "lit3mail.db"), accounts.Distinct().ToArray());
        string requestPath = Path.Combine(LocalDatabase.DefaultFolder, RequestFileName);
        string temporary = requestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(request));
        File.Move(temporary, requestPath, overwrite: true);
    }

    public static void Cancel() => File.Delete(Path.Combine(LocalDatabase.DefaultFolder, RequestFileName));

    public static void ApplyPending()
    {
        string requestPath = Path.Combine(LocalDatabase.DefaultFolder, RequestFileName);
        if (!File.Exists(requestPath))
            return;
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath))
            ?? throw new InvalidOperationException("The local data reset request is invalid.");
        Complete(request, LocalDatabase.DefaultFolder, WindowsCredentialStore.Delete);
    }

    internal static void Complete(Request request, string settingsFolder, Action<Guid> deleteSignIn)
    {
        if (!Path.IsPathFullyQualified(request.DatabasePath) ||
            !string.Equals(Path.GetFileName(request.DatabasePath), "lit3mail.db", StringComparison.OrdinalIgnoreCase) ||
            request.AccountIds is null)
            throw new InvalidOperationException("The local data reset request is invalid.");

        // Keep the request until every step succeeds so a failed reset can be retried.
        foreach (var account in request.AccountIds)
            deleteSignIn(account);
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "-wal", "-shm", "" })
            File.Delete(request.DatabasePath + suffix);

        string previewFolder = Path.Combine(settingsFolder, "HtmlPreview");
        if (Directory.Exists(previewFolder))
            Directory.Delete(previewFolder, recursive: true);
        foreach (string name in new[] { "window-layout.json", "window-layout.json.tmp", "data-folder.txt",
            "pending-data-folder.txt", "pending-data-folder.txt.tmp", RequestFileName + ".tmp", RequestFileName })
            File.Delete(Path.Combine(settingsFolder, name));
    }
}
