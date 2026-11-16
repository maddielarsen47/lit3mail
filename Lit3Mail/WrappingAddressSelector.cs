using System.Runtime.InteropServices;

namespace Lit3Mail;

internal sealed class WrappingAddressSelector : ComboBox
{
    private const uint SetItemHeight = 0x0153;
    private const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
        TextFormatFlags.NoPrefix;
    private int selectionHeight;
    private bool resizePosted;

    public WrappingAddressSelector() => DrawMode = DrawMode.OwnerDrawVariable;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        selectionHeight = 0;
        UpdateSelectionHeight();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateSelectionHeight();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateSelectionHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateSelectionHeight();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        UpdateSelectionHeight();
    }

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = MeasureAddress(ItemText(e.Index), Math.Max(Width, DropDownWidth) - 8);
        base.OnMeasureItem(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        var bounds = Rectangle.Inflate(e.Bounds, -4, -2);
        TextRenderer.DrawText(e.Graphics, ItemText(e.Index), Font, bounds, e.ForeColor, WrapFlags);
        e.DrawFocusRectangle();
        base.OnDrawItem(e);
    }

    private string ItemText(int index) => index >= 0 && index < Items.Count ? GetItemText(Items[index]) ?? "" : "";

    private int MeasureAddress(string text, int width) => Math.Max(Font.Height,
        TextRenderer.MeasureText(text, Font, new Size(Math.Max(1, width), int.MaxValue), WrapFlags).Height) + 4;

    private void UpdateSelectionHeight()
    {
        if (!IsHandleCreated || !Visible || resizePosted)
            return;
        resizePosted = true;
        BeginInvoke(() =>
        {
            resizePosted = false;
            if (!IsHandleCreated || !Visible || Width <= 0)
                return;
            int height = MeasureAddress(ItemText(SelectedIndex), Width - SystemInformation.VerticalScrollBarWidth - 8);
            if (height == selectionHeight)
                return;
            selectionHeight = height;
            SendMessage(Handle, SetItemHeight, -1, height);
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint parameter, nint value);
}
