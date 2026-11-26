using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lit3Mail;

internal sealed class EntryFieldPanel : Panel
{
    private Color borderColor = SystemColors.WindowFrame;

    public EntryFieldPanel() => SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
        ControlStyles.OptimizedDoubleBuffer, true);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Color BorderColor
    {
        get => borderColor;
        set { borderColor = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(borderColor);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }
}

internal static class EntryBorderTheme
{
    private static readonly ConditionalWeakTable<Control, BorderWindow> Borders = new();

    internal static void Apply(Control root, Color border)
    {
        if (root is CheckBox { Appearance: Appearance.Normal } checkbox)
        {
            // Keep normal checkbox layout, then draw the same themed glyph used by grid checkbox cells.
            checkbox.FlatStyle = FlatStyle.Flat;
            checkbox.UseVisualStyleBackColor = false;
            checkbox.BackColor = checkbox.Parent?.BackColor ?? SystemColors.Control;
            checkbox.FlatAppearance.BorderColor = border;
            checkbox.FlatAppearance.CheckedBackColor = checkbox.BackColor;
            checkbox.FlatAppearance.MouseOverBackColor = checkbox.BackColor;
            checkbox.FlatAppearance.MouseDownBackColor = checkbox.BackColor;
            checkbox.Paint -= PaintCheckboxGlyph;
            checkbox.Paint += PaintCheckboxGlyph;
        }
        else if (root is EntryFieldPanel frame)
            frame.BorderColor = border;
        else if (root is ComboBox or NumericUpDown || root is TextBoxBase { BorderStyle: not BorderStyle.None })
        {
            // A single native border keeps the entry's layout and keyboard behavior intact.
            if (root is TextBoxBase text)
                text.BorderStyle = BorderStyle.FixedSingle;
            if (root is NumericUpDown number)
                number.BorderStyle = BorderStyle.FixedSingle;
            Borders.GetValue(root, control => new BorderWindow(control)).SetColor(border);
        }
        foreach (Control child in root.Controls)
            Apply(child, border);
    }

    private static void PaintCheckboxGlyph(object? sender, PaintEventArgs e)
    {
        if (sender is not CheckBox checkbox)
            return;
        var state = checkbox.CheckState switch
        {
            CheckState.Checked => checkbox.Enabled ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.CheckedDisabled,
            CheckState.Indeterminate => checkbox.Enabled ? System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.MixedDisabled,
            _ => checkbox.Enabled ? System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedDisabled
        };
        int size = CheckBoxRenderer.GetGlyphSize(e.Graphics, state).Width;
        bool right = checkbox.CheckAlign is ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight;
        if (checkbox.RightToLeft == RightToLeft.Yes)
            right = !right;
        int x = right ? checkbox.ClientSize.Width - size : 0;
        int y = checkbox.CheckAlign is ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight
            ? 0 : checkbox.CheckAlign is ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
                ? checkbox.ClientSize.Height - size : (checkbox.ClientSize.Height - size) / 2;
        if (checkbox.CheckAlign is ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter)
            x = (checkbox.ClientSize.Width - size) / 2;

        using var background = new SolidBrush(checkbox.BackColor);
        e.Graphics.FillRectangle(background, x, y, size, size);
        CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(x, y), state);
    }

    private sealed class BorderWindow : NativeWindow
    {
        private readonly Control control;
        private Color color;

        internal BorderWindow(Control control)
        {
            this.control = control;
            control.HandleCreated += (_, _) => AssignHandle(control.Handle);
            control.HandleDestroyed += (_, _) => ReleaseHandle();
            control.Disposed += (_, _) => ReleaseHandle();
            if (control.IsHandleCreated)
                AssignHandle(control.Handle);
        }

        internal void SetColor(Color value)
        {
            color = value;
            control.Invalidate();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg is not (0x000F or 0x0085) || Handle == 0 || control.IsDisposed)
                return;
            // Window DC includes the non-client border, unlike the normal Paint graphics.
            nint dc = GetWindowDC(Handle);
            if (dc == 0)
                return;
            try
            {
                using var graphics = Graphics.FromHdc(dc);
                using var pen = new Pen(color);
                graphics.DrawRectangle(pen, 0, 0, Math.Max(0, control.Width - 1), Math.Max(0, control.Height - 1));
            }
            finally { ReleaseDC(Handle, dc); }
        }

        [DllImport("user32.dll")]
        private static extern nint GetWindowDC(nint window);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(nint window, nint dc);
    }
}
