using Microsoft.Win32;

namespace Lit3Mail;

internal static class ThemedPopup
{
    private static readonly Color Surface = Color.FromArgb(24, 30, 42);
    private static readonly Color Chrome = Color.FromArgb(32, 41, 57);
    private static readonly Color Ink = Color.FromArgb(232, 239, 249);
    private static readonly Color Border = Color.FromArgb(52, 65, 85);
    private static readonly Color Hover = Color.FromArgb(49, 78, 119);

    internal static bool? UseDarkTheme { get; set; }

    private static bool IsDark => !SystemInformation.HighContrast && (UseDarkTheme ?? WindowsUsesDarkTheme());

    internal static bool WindowsUsesDarkTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;

    internal static void ApplyTo(Form form)
    {
        if (!IsDark)
            return;

        form.HandleCreated += (_, _) => MailForm.ApplyWindowTitleBar(form, dark: true);
        MailForm.ApplyWindowTitleBar(form, dark: true);
        ApplyColors(form);
        ScrollbarTheme.Apply(form, dark: true);
    }

    private static void ApplyColors(Control control)
    {
        control.BackColor = control is Button ? Chrome : Surface;
        control.ForeColor = Ink;
        if (control is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Hover;
        }
        foreach (Control child in control.Controls)
            ApplyColors(child);
    }

    internal static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
        Show(null, text, caption, buttons, icon);

    internal static IWin32Window? GetDialogOwner(IWin32Window? owner) =>
        owner is Control control && control.FindForm() is MailForm main ? main.ComposeDialogOwner : owner;

    internal static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        owner = GetDialogOwner(owner);
        if (!IsDark)
            return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);

        using var dialog = new Form
        {
            Text = caption, Font = SystemFonts.MessageBoxFont, AutoScaleMode = AutoScaleMode.Dpi,
            FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 4, 12, 10)
        };
        var results = buttons switch
        {
            MessageBoxButtons.YesNo => new[] { DialogResult.Yes, DialogResult.No },
            MessageBoxButtons.YesNoCancel => [DialogResult.Yes, DialogResult.No, DialogResult.Cancel],
            _ => [DialogResult.OK]
        };
        var choices = results.Select(result => new Button
        {
            Text = result.ToString(), DialogResult = result, AutoSize = true, MinimumSize = new Size(76, 28)
        }).ToArray();
        actions.Controls.AddRange(choices.Reverse().ToArray());

        Icon? symbol = icon switch
        {
            MessageBoxIcon.Error => SystemIcons.Error,
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Information => SystemIcons.Information,
            _ => null
        };
        using var bitmap = symbol?.ToBitmap();
        int inset = bitmap is null ? 16 : 60;
        int messageWidth = 380;
        var label = new Label
        {
            Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(messageWidth, 0),
            Location = new Point(inset, 16)
        };
        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.Controls.Add(label);
        if (bitmap is not null)
            content.Controls.Add(new PictureBox { Image = bitmap, Location = new Point(16, 16), Size = bitmap.Size });
        dialog.Controls.Add(content);
        dialog.Controls.Add(actions);

        int preferredHeight = label.GetPreferredSize(new Size(messageWidth, 0)).Height;
        int contentHeight = Math.Clamp(preferredHeight + 32, 64, 280);
        dialog.ClientSize = new Size(inset + messageWidth + 28, contentHeight + actions.PreferredSize.Height);
        int index = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0
        };
        dialog.AcceptButton = choices[Math.Min(index, choices.Length - 1)];
        dialog.ActiveControl = (Control)dialog.AcceptButton;
        dialog.CancelButton = choices.FirstOrDefault(button => button.DialogResult is DialogResult.Cancel or DialogResult.OK);
        ApplyTo(dialog);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }
}
