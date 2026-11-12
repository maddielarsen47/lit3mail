using System.Text.Json;

namespace Lit3Mail;

public partial class MailForm
{
    private sealed record PaneSizes(int SidebarWidth, int MessageListHeight);
    private sealed record WindowLayout(int X, int Y, int Width, int Height, bool Maximized,
        PaneSizes RestoredPanes, PaneSizes MaximizedPanes);

    private void InitializeWindowLayout(SplitContainer layout)
    {
        string settingsPath = Path.Combine(LocalDatabase.DefaultFolder, "window-layout.json");
        WindowLayout? saved = null;
        try
        {
            if (File.Exists(settingsPath))
                saved = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(settingsPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }

        var defaults = new PaneSizes(layout.SplitterDistance, mailPane.SplitterDistance);
        var restored = saved?.RestoredPanes ?? defaults;
        var maximized = saved?.MaximizedPanes ?? defaults;

        if (saved is { Width: > 0, Height: > 0 })
        {
            var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            var area = Screen.FromRectangle(bounds).WorkingArea;
            int width = Math.Clamp(bounds.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, area.Width));
            int height = Math.Clamp(bounds.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, area.Height));
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(
                Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - width)),
                Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
        }
        if (saved?.Maximized == true)
            WindowState = FormWindowState.Maximized;

        var appliedState = WindowState;
        bool applying = true;

        void RestorePanes()
        {
            if (IsDisposed || WindowState == FormWindowState.Minimized)
                return;

            applying = true;
            var sizes = WindowState == FormWindowState.Maximized ? maximized : restored;
            SetSplitterDistance(layout, sizes.SidebarWidth, layout.ClientSize.Width);
            SetSplitterDistance(mailPane, sizes.MessageListHeight, mailPane.ClientSize.Height);
            appliedState = WindowState;
            applying = false;
        }

        void RememberSplitter(bool sidebar)
        {
            if (applying || WindowState != appliedState || WindowState == FormWindowState.Minimized)
                return;

            var sizes = WindowState == FormWindowState.Maximized ? maximized : restored;
            if (sidebar)
                sizes = sizes with { SidebarWidth = layout.SplitterDistance };
            else if (!mailPane.Panel2Collapsed)
                sizes = sizes with { MessageListHeight = mailPane.SplitterDistance };

            if (WindowState == FormWindowState.Maximized)
                maximized = sizes;
            else
                restored = sizes;
        }

        layout.SplitterMoved += (_, _) => RememberSplitter(sidebar: true);
        mailPane.SplitterMoved += (_, _) => RememberSplitter(sidebar: false);
        Shown += (_, _) => RestorePanes();
        layout.VisibleChanged += (_, _) =>
        {
            if (layout.Visible)
                this.PostIfHandleAvailable(RestorePanes);
        };
        Resize += (_, _) =>
        {
            if (WindowState == appliedState)
                return;
            this.PostIfHandleAvailable(RestorePanes);
        };
        FormClosing += (_, e) =>
        {
            if (e.Cancel)
                return;

            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            bool reopenMaximized = WindowState == FormWindowState.Maximized ||
                (WindowState == FormWindowState.Minimized && appliedState == FormWindowState.Maximized);
            var current = new WindowLayout(bounds.X, bounds.Y, bounds.Width, bounds.Height,
                reopenMaximized, restored, maximized);

            try
            {
                Directory.CreateDirectory(LocalDatabase.DefaultFolder);
                string temporary = settingsPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(current));
                File.Move(temporary, settingsPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        };
    }

    private static void SetSplitterDistance(SplitContainer pane, int distance, int availableSize)
    {
        int maximum = availableSize - pane.SplitterWidth - pane.Panel2MinSize;
        if (maximum >= pane.Panel1MinSize)
            pane.SplitterDistance = Math.Clamp(distance, pane.Panel1MinSize, maximum);
    }
}
