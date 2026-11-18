using System.Text.RegularExpressions;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Lit3Mail;

internal sealed class MailBodyEditor : RichTextBox
{
    private const uint EmGetCharFormat = 0x043A;
    private const uint EmSetCharFormat = 0x0444;
    private const uint EmGetTextEx = 0x045E;
    private const uint EmGetParaFormat = 0x043D;
    private const uint EmSetParaFormat = 0x0447;
    private const uint GtNoHiddenText = 8;
    private const uint CfmSize = 0x80000000;
    private const int TwipsPerPoint = 20;
    private const uint PfmNumbering = 0x20;
    private const uint PfmNumberingStyle = 0x2000;
    private const uint PfmNumberingTab = 0x4000;
    private const uint PfmNumberingStart = 0x8000;
    private const ushort PfnArabic = 2;
    private const ushort PfnsPeriod = 0x0200;
    private const int ScfSelection = 1;
    private const int WmPaste = 0x0302;

    internal enum Format : uint { Bold = 1, Italic = 2, Underline = 4 }

    static MailBodyEditor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void SetPlainText(string text)
    {
        Clear();
        AppendPlainText(text);
        Select(0, 0);
    }

    public void InsertHtmlOrText(string value)
    {
        if (HtmlSignature.ToRtf(value) is { } rtf)
            SelectedRtf = rtf;
        else
            AppendPlainText(value);
    }

    public void AppendPlainText(string text)
    {
        ResetFormatting();
        SelectedText = text;
    }

    public void ResetFormatting()
    {
        SelectionFont = Font;
        SelectionColor = Color.Black;
        SetNumbering(0);
        SelectionIndent = 0;
        SelectionHangingIndent = 0;
        SelectionAlignment = HorizontalAlignment.Left;
        OnSelectionChanged(EventArgs.Empty);
    }

    internal static MailContent GetMessageContent(string rtf, string body)
    {
        string html = rtf.Length > 0 ? RtfPipe.Rtf.ToHtml(rtf) : "";
        var images = new List<InlineMailImage>();
        html = Regex.Replace(html, "src=\"data:image/png;base64,([A-Za-z0-9+/=]+)\"", match =>
        {
            string id = MimeKit.Utils.MimeUtils.GenerateMessageId();
            images.Add(new(id, "image/png", Convert.FromBase64String(match.Groups[1].Value)));
            return $"src=\"cid:{id}\"";
        });
        return new(body, Html: html, InlineImages: images);
    }

    internal string GetVisibleText()
    {
        var buffer = new StringBuilder(TextLength + 1);
        var options = new GetTextOptions
        {
            BufferBytes = (uint)(buffer.Capacity * sizeof(char)),
            Flags = GtNoHiddenText,
            CodePage = 1200
        };
        SendMessage(Handle, EmGetTextEx, ref options, buffer);
        return buffer.ToString().Replace("\uFFFC", "").Replace('\r', '\n').Replace('\v', '\n');
    }

    public bool HasFormat(Format format)
    {
        var selection = NewCharacterFormat();
        SendMessage(Handle, EmGetCharFormat, ScfSelection, ref selection);
        uint flag = (uint)format;
        return (selection.Mask & selection.Effects & flag) != 0;
    }

    public void ToggleFormat(Format format)
    {
        var selection = NewCharacterFormat();
        selection.Mask = (uint)format;
        selection.Effects = HasFormat(format) ? 0 : (uint)format;
        SendMessage(Handle, EmSetCharFormat, ScfSelection, ref selection);
        OnSelectionChanged(EventArgs.Empty);
    }

    public void ChangeFontSize(int points)
    {
        var selection = NewCharacterFormat();
        SendMessage(Handle, EmGetCharFormat, ScfSelection, ref selection);
        int current = selection.Height > 0 ? selection.Height : (int)Math.Round(Font.SizeInPoints * TwipsPerPoint);
        selection.Mask = CfmSize;
        selection.Height = Math.Clamp(current + points * TwipsPerPoint, 6 * TwipsPerPoint, 72 * TwipsPerPoint);
        SendMessage(Handle, EmSetCharFormat, ScfSelection, ref selection);
        OnSelectionChanged(EventArgs.Empty);
    }

    public bool HasNumberedList()
    {
        var selection = NewParagraphFormat();
        SendMessage(Handle, EmGetParaFormat, IntPtr.Zero, ref selection);
        return (selection.Mask & PfmNumbering) != 0 && selection.Numbering == PfnArabic;
    }

    public void ToggleNumberedList()
    {
        SetNumbering(HasNumberedList() ? (ushort)0 : PfnArabic);
        OnSelectionChanged(EventArgs.Empty);
    }

    private void SetNumbering(ushort numbering)
    {
        var selection = NewParagraphFormat();
        selection.Mask = PfmNumbering;
        selection.Numbering = numbering;
        if (numbering == PfnArabic)
        {
            selection.Mask |= PfmNumberingStyle | PfmNumberingTab | PfmNumberingStart;
            selection.NumberingStyle = PfnsPeriod;
            selection.NumberingStart = 1;
            selection.NumberingTab = 300;
        }
        SendMessage(Handle, EmSetParaFormat, IntPtr.Zero, ref selection);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        Format? format = keyData switch
        {
            Keys.Control | Keys.B => Format.Bold,
            Keys.Control | Keys.I => Format.Italic,
            Keys.Control | Keys.U => Format.Underline,
            _ => null
        };
        if (format is { } style)
        {
            ToggleFormat(style);
            return true;
        }
        if (keyData is (Keys.Control | Keys.V) or (Keys.Shift | Keys.Insert))
        {
            PasteClipboard();
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmPaste)
        {
            PasteClipboard();
            return;
        }
        base.WndProc(ref message);
    }

    private void PasteClipboard()
    {
        try
        {
            if (ReadOnly)
                return;
            using var image = Clipboard.GetImage();
            if (image is not null)
                InsertImage(image);
            else if (Clipboard.ContainsText())
                SelectedText = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            // Another process can temporarily hold the clipboard open.
        }
    }

    internal void InsertImage(Image image) =>
        SelectedRtf = @"{\rtf1\ansi" + CreatePictureGroup(image) + "}";

    internal static string CreatePictureGroup(Image image)
    {
        double scale = Math.Min(1, Math.Min(600d / image.Width, 450d / image.Height));
        return CreatePictureGroup(image, Math.Max(1, image.Width * scale), Math.Max(1, image.Height * scale));
    }

    internal static string CreatePictureGroup(Image image, double widthPixels, double heightPixels)
    {
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        int width = (int)Math.Clamp(Math.Round(widthPixels * 15), 1, int.MaxValue);
        int height = (int)Math.Clamp(Math.Round(heightPixels * 15), 1, int.MaxValue);
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\\pict\\pngblip\\picw{image.Width}\\pich{image.Height}\\picwgoal{width}\\pichgoal{height} {Convert.ToHexString(png.ToArray())}}}");
    }

    private static CharacterFormat NewCharacterFormat() =>
        new() { Size = (uint)Marshal.SizeOf<CharacterFormat>(), FaceName = "" };

    private static ParagraphFormat NewParagraphFormat() =>
        new() { Size = (uint)Marshal.SizeOf<ParagraphFormat>(), Tabs = new int[32] };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CharacterFormat
    {
        public uint Size;
        public uint Mask;
        public uint Effects;
        public int Height;
        public int Offset;
        public int TextColor;
        public byte CharacterSet;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetTextOptions
    {
        public uint BufferBytes;
        public uint Flags;
        public uint CodePage;
        public IntPtr DefaultChar;
        public IntPtr UsedDefaultChar;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParagraphFormat
    {
        public uint Size;
        public uint Mask;
        public ushort Numbering;
        public ushort Effects;
        public int StartIndent;
        public int RightIndent;
        public int Offset;
        public ushort Alignment;
        public short TabCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] Tabs;
        public int SpaceBefore;
        public int SpaceAfter;
        public int LineSpacing;
        public short Style;
        public byte LineSpacingRule;
        public byte OutlineLevel;
        public ushort ShadingWeight;
        public ushort ShadingStyle;
        public ushort NumberingStart;
        public ushort NumberingStyle;
        public ushort NumberingTab;
        public ushort BorderSpace;
        public ushort BorderWidth;
        public ushort Borders;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, ref CharacterFormat format);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, ref ParagraphFormat format);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, ref GetTextOptions options, [Out] StringBuilder buffer);
}







