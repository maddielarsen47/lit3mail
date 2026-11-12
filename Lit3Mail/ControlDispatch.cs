namespace Lit3Mail;

internal static class ControlDispatch
{
    public static void PostIfHandleAvailable(this Control control, Action callback)
    {
        if (!CanPost(control))
            return;

        try
        {
            control.BeginInvoke((Action)(() =>
            {
                if (CanPost(control))
                    callback();
            }));
        }
        catch (InvalidOperationException) when (!CanPost(control))
        {
            // The window handle can be destroyed between the check and BeginInvoke.
        }
    }

    private static bool CanPost(Control control) =>
        control.IsHandleCreated && !control.Disposing && !control.IsDisposed;
}
