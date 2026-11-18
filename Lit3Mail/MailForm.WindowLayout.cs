using System.Text.Json;

namespace Lit3Mail;

public partial class MailForm
{
    private sealed record PaneSizes(int SidebarWidth, int? MessageListHeight = null);
    private sealed record WindowLayout(int X, int Y, int Width, int Height, bool Maximized,
        PaneSizes RestoredPanes, PaneSizes MaximizedPanes, double? PreviewHeightRatio = null);

    private void InitializeWindowLayout(SplitContainer layout, ToolTip hints)
    {
        string settingsPath = Path.Combine(LocalDatabase.DefaultFolder, "window-layout.json");
        WindowLayout? saved = null;
        try
        {
            if (File.Exists(settingsPath))
                saved = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(settingsPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }

        var defaults = new PaneSizes(layout.SplitterDistance);
        double defaultPreviewHeightRatio = GetPreviewHeightRatio();
        double? previewHeightRatio = saved?.PreviewHeightRatio is > 0 and < 1 ? saved.PreviewHeightRatio : null;
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
        bool applyingPreviewHeight = false;
        int appliedPreviewLayoutHeight = 0;
        bool previewResizeQueued = false;
        int selectionBarIndex = mailPane.Panel1.Controls.GetChildIndex(messageSelectionBar);

        void RestorePanes()
        {
            if (IsDisposed || WindowState == FormWindowState.Minimized)
                return;

            applying = true;
            var sizes = WindowState == FormWindowState.Maximized ? maximized : restored;
            SetSplitterDistance(layout, sizes.SidebarWidth, layout.ClientSize.Width);
            if (previewHeightRatio is null)
            {
                int height = mailPane.ClientSize.Height;
                previewHeightRatio = sizes.MessageListHeight is { } legacyHeight &&
                    legacyHeight > 0 && legacyHeight < height - mailPane.SplitterWidth
                    ? (height - mailPane.SplitterWidth - legacyHeight) / (double)height
                    : defaultPreviewHeightRatio;
            }
            ApplyPreviewHeight();
            appliedState = WindowState;
            applying = false;
        }

        void RememberSidebarWidth()
        {
            if (applying || WindowState != appliedState || WindowState == FormWindowState.Minimized)
                return;

            var sizes = WindowState == FormWindowState.Maximized ? maximized : restored;
            sizes = sizes with { SidebarWidth = layout.SplitterDistance };

            if (WindowState == FormWindowState.Maximized)
                maximized = sizes;
            else
                restored = sizes;
        }

        void ApplyPreviewHeight()
        {
            int height = mailPane.ClientSize.Height;
            if (applyingPreviewHeight || mailPane.Panel1Collapsed || WindowState == FormWindowState.Minimized ||
                previewHeightRatio is not { } ratio || height <= 0)
                return;

            applyingPreviewHeight = true;
            try
            {
                int previewHeight = (int)Math.Round(height * ratio);
                SetSplitterDistance(mailPane, height - mailPane.SplitterWidth - previewHeight, height);
                appliedPreviewLayoutHeight = height;
            }
            finally
            {
                applyingPreviewHeight = false;
            }
        }

        double GetPreviewHeightRatio()
        {
            int height = mailPane.ClientSize.Height;
            return (height - mailPane.SplitterDistance - mailPane.SplitterWidth) / (double)height;
        }

        layout.SplitterMoved += (_, _) => RememberSidebarWidth();
        mailPane.ClientSizeChanged += (_, _) =>
        {
            if (previewResizeQueued || !IsHandleCreated)
                return;

            previewResizeQueued = true;
            this.PostIfHandleAvailable(() =>
            {
                previewResizeQueued = false;
                ApplyPreviewHeight();
            });
        };
        mailPane.SplitterMoved += (_, _) =>
        {
            if (!applying && !applyingPreviewHeight && !previewResizeQueued && !mailPane.Panel1Collapsed &&
                WindowState == appliedState &&
                WindowState != FormWindowState.Minimized && mailPane.ClientSize.Height == appliedPreviewLayoutHeight)
                previewHeightRatio = GetPreviewHeightRatio();
        };
        hints.SetToolTip(toggleMessageList, toggleMessageList.AccessibleName);
        toggleMessageList.Click += (_, _) =>
        {
            bool hideList = !mailPane.Panel1Collapsed;
            applyingPreviewHeight = true;
            try
            {
                if (hideList)
                {
                    messageSelectionBar.Dock = DockStyle.Top;
                    mailPane.Panel2.Controls.Add(messageSelectionBar);
                    mailPane.Panel1Collapsed = true;
                }
                else
                {
                    mailPane.Panel1Collapsed = false;
                    messageSelectionBar.Dock = DockStyle.Bottom;
                    mailPane.Panel1.Controls.Add(messageSelectionBar);
                    mailPane.Panel1.Controls.SetChildIndex(messageSelectionBar, selectionBarIndex);
                }
            }
            finally
            {
                applyingPreviewHeight = false;
            }

            if (!hideList)
                ApplyPreviewHeight();
            toggleMessageList.Text = hideList ? "▾" : "▴";
            toggleMessageList.AccessibleName = hideList ? "Show email list" : "Hide email list";
            hints.SetToolTip(toggleMessageList, toggleMessageList.AccessibleName);
            toggleMessageList.Focus();
        };
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
                reopenMaximized, restored, maximized, previewHeightRatio);

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
