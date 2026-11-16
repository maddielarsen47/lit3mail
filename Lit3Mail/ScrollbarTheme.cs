using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lit3Mail;

internal static class ScrollbarTheme
{
    private static readonly ConditionalWeakTable<Control, NativeTheme> NativeThemes = new();

    public static void Apply(Control control, bool dark)
    {
        if (control is HtmlMailPreview preview)
        {
            preview.UseDarkScrollbars = dark;
            return;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
            control is (ScrollableControl { AutoScroll: true } or RichTextBox or ListBox or ScrollBar or
                TextBox { Multiline: true, ScrollBars: not ScrollBars.None }))
            NativeThemes.GetValue(control, static item => new NativeTheme(item)).Apply(dark);

        foreach (Control child in control.Controls)
            Apply(child, dark);
    }

    private sealed class NativeTheme
    {
        private readonly Control control;
        private bool dark;
        private bool appliedDark;

        public NativeTheme(Control control)
        {
            this.control = control;
            control.HandleCreated += (_, _) =>
            {
                appliedDark = false;
                Apply(dark);
            };
        }

        public void Apply(bool dark)
        {
            this.dark = dark;
            if (!control.IsHandleCreated || appliedDark == dark)
                return;
            appliedDark = dark;
            SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : null, null);
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(nint window, string? application, string? classList);
}
