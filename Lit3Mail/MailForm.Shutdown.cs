using System.Diagnostics;

namespace Lit3Mail;

public partial class MailForm
{
    private bool backgroundShutdown;
    private bool shutdownReady;
    private Task backgroundShutdownCompletion = Task.CompletedTask;
    private bool stagingUpdate;
    private Task responseFlagCompletion = Task.CompletedTask;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownCancellationGrace = TimeSpan.FromSeconds(2);

    private bool HasPendingShutdownWork => composeSession.OperationPending || updatingMessageFolders ||
        !messageFolderChange.IsCompleted || !exportCompletion.IsCompleted || flagUpdates.Count > 0 ||
        !responseFlagCompletion.IsCompleted || stagingUpdate;

    private async Task FinishBackgroundShutdownAsync()
    {
        backgroundShutdown = true;
        // Keep the form and message loop alive for existing async continuations, but remove all windows.
        Hide();
        ShowInTaskbar = false;
        composeWindow?.Hide();
        foreach (var timer in components.Components.OfType<System.Windows.Forms.Timer>())
            timer.Stop();
        folderLoad?.Cancel();
        previewLoad?.Cancel();
        wakeRefresh?.Cancel();
        activeCacheCancellation?.Cancel();
        cacheQueue.Clear();

        var elapsed = Stopwatch.StartNew();
        while (!IsDisposed && HasPendingShutdownWork && elapsed.Elapsed < ShutdownTimeout)
            await Task.Delay(100);
        if (IsDisposed)
            return;

        if (HasPendingShutdownWork)
        {
            // Give cancellation handlers a bounded opportunity to preserve drafts and clean up exports.
            mailboxLifetime.Cancel();
            elapsed.Restart();
            while (!IsDisposed && HasPendingShutdownWork && elapsed.Elapsed < ShutdownCancellationGrace)
                await Task.Delay(100);
        }
        if (IsDisposed)
            return;

        shutdownReady = true;
        Close();
        // An updater launch can still fail or be declined after the drain has finished.
        if (!IsDisposed)
        {
            backgroundShutdown = false;
            shutdownReady = false;
            ShowInTaskbar = true;
            Show();
        }
    }
}
