namespace Lit3Mail;

public partial class MailForm
{
    private readonly Label batchStatus = new() { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    private readonly Button cancelExport = new() { Text = "Cancel", AutoSize = true };
    private readonly FlowLayoutPanel batchBar = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 2, 12, 2), Visible = false
    };
    private readonly FlowLayoutPanel composeOperationBar = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12, 2, 12, 2), Visible = false
    };
    private readonly Label composeOperationStatus = new() { AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    private readonly Button dismissComposeStatus = new() { Text = "Dismiss", AutoSize = true };
    private System.Windows.Forms.Timer composeStatusTimer = null!;
    private object? batchActivity;
    private System.Windows.Forms.Timer batchStatusTimer = null!;

    private void InitializeStatusBars()
    {
        batchStatusTimer = new System.Windows.Forms.Timer(components!) { Interval = 10_000 };
        batchStatusTimer.Tick += (_, _) =>
        {
            batchStatusTimer.Stop();
            batchBar.Hide();
        };
        batchBar.Controls.Add(batchStatus);
        batchBar.Controls.Add(cancelExport);
        cancelExport.Click += (_, _) => exportCancellation?.Cancel();
        composeStatusTimer = new System.Windows.Forms.Timer(components!) { Interval = 10_000 };
        composeStatusTimer.Tick += (_, _) => HideComposeOperationStatus();
        dismissComposeStatus.Click += (_, _) => HideComposeOperationStatus();
        composeOperationBar.Controls.Add(composeOperationStatus);
        composeOperationBar.Controls.Add(dismissComposeStatus);
        composeOperationBar.SizeChanged += (_, _) =>
            composeOperationStatus.MaximumSize = new Size(
                Math.Max(1, composeOperationBar.ClientSize.Width - composeOperationBar.Padding.Horizontal), 0);
    }

    private object? BeginBatchActivity(string message)
    {
        if (batchActivity is not null || updatingMessageFolders || exportCancellation is not null)
            return null;

        ShowBatchStatus(message, inProgress: true);
        batchActivity = new object();
        return batchActivity;
    }

    private void EndBatchActivity(object? activity)
    {
        if (activity is null || !ReferenceEquals(batchActivity, activity))
            return;

        batchActivity = null;
        if (!IsDisposed && !Disposing)
            batchBar.Hide();
    }

    private void ShowBatchStatus(string message, bool cancellable = false, bool inProgress = false)
    {
        batchStatusTimer.Stop();
        batchActivity = null;
        batchStatus.Text = message;
        cancelExport.Visible = cancellable;
        batchBar.Show();
        if (!cancellable && !inProgress)
            batchStatusTimer.Start();
    }

    private void ShowComposeOperationStatus(string message, bool inProgress = false, bool persistent = false)
    {
        composeStatusTimer.Stop();
        composeOperationStatus.Text = message;
        dismissComposeStatus.Visible = persistent;
        composeOperationBar.Show();
        if (!inProgress && !persistent)
            composeStatusTimer.Start();
    }

    private void HideComposeOperationStatus()
    {
        composeStatusTimer.Stop();
        composeOperationBar.Hide();
    }
}
