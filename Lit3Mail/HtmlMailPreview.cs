using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MimeKit.Text;

namespace Lit3Mail;

internal sealed class HtmlMailPreview : WebView2
{
    private const string PreviewHost = "lit3mail-preview.invalid";
    private const int MaxQuoteImageBytes = 32 * 1024 * 1024;
    private readonly SemaphoreSlim navigationGate = new(1);
    private readonly Dictionary<Uri, (byte[] Bytes, string MediaType)> quoteImages = new();
    private readonly Dictionary<string, InlineMailImage> inlineResources = new(StringComparer.Ordinal);
    private int quoteImageBytes;
    private Task? initialization;
    private TaskCompletionSource<bool>? documentReady;
    private ulong? navigationId;
    private string? navigationTarget;
    private byte[]? navigationDocument;
    private int displayVersion;
    private bool allowRemoteImages;
    private bool allowInlineImages;
    public HtmlMailPreview() => DefaultBackgroundColor = Color.White;

    private static async Task ApplyColorSchemeAsync(CoreWebView2 browser)
    {
        browser.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        await browser.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", JsonSerializer.Serialize(new
        {
            features = new[] { new { name = "prefers-color-scheme", value = "light" } }
        }));
    }

    private ContextMenuStrip? previewMenu;

    private byte[]? GetQuoteImage(string source) =>
        inlineResources.GetValueOrDefault(source)?.Bytes ?? GetDownloadedImage(source)?.Bytes;

    public InlineMailImage? GetInlineQuoteImage(string source)
    {
        if (GetDownloadedImage(source) is not { } image ||
            !ComposeHtmlQuote.IsSupportedImage(image.MediaType))
            return null;
        return new(MimeKit.Utils.MimeUtils.GenerateMessageId(), image.MediaType, image.Bytes, source);
    }

    private (byte[] Bytes, string MediaType)? GetDownloadedImage(string source) =>
        allowRemoteImages && Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        quoteImages.TryGetValue(uri, out var image) ? image : null;

    public void ClearDisplay()
    {
        ++displayVersion;
        previewMenu?.Close();
        allowRemoteImages = false;
        allowInlineImages = false;
        quoteImages.Clear();
        inlineResources.Clear();
        quoteImageBytes = 0;
        Hide();
        documentReady?.TrySetResult(false);
        try
        {
            CoreWebView2?.Stop();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The renderer can be unavailable during shutdown or after a browser failure.
        }
    }

    public async Task<bool> DisplayAsync(MailContent content, bool showRemoteImages, bool showInlineImages = false)
    {
        ClearDisplay();
        int version = displayVersion;
        await navigationGate.WaitAsync();
        bool showingDocument = false;
        try
        {
            if (IsDisposed || version != displayVersion)
                return false;

            await (initialization ??= InitializeBrowserAsync());
            if (IsDisposed || version != displayVersion)
                return false;

            navigationTarget = $"http://{PreviewHost}/{Guid.NewGuid():N}";
            allowRemoteImages = showRemoteImages;
            allowInlineImages = showInlineImages;
            string html = content.Html ?? "";
            if ((showRemoteImages || showInlineImages) && content.InlineImages is { Count: > 0 })
            {
                var resourceUrls = new Dictionary<InlineMailImage, string>(ReferenceEqualityComparer.Instance);
                html = content.HtmlWithInlineImages(image =>
                {
                    if (!resourceUrls.TryGetValue(image, out var url))
                    {
                        url = $"{navigationTarget}/image/{resourceUrls.Count}";
                        resourceUrls.Add(image, url);
                        inlineResources.Add(url, image);
                    }
                    return url;
                }) ?? html;
            }
            navigationDocument = Encoding.UTF8.GetBytes(BuildDocument(html, allowRemoteImages));
            documentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CoreWebView2.Navigate(navigationTarget);
            bool loaded = await documentReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!loaded || IsDisposed || version != displayVersion)
                return false;

            Show();
            showingDocument = true;
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or
            System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException or
            IOException or UnauthorizedAccessException or TimeoutException)
        {
            if (initialization?.IsFaulted == true)
                initialization = null;
            return false;
        }
        finally
        {
            documentReady = null;
            navigationId = null;
            navigationTarget = null;
            navigationDocument = null;
            if (!showingDocument)
            {
                allowRemoteImages = allowInlineImages = false;
                inlineResources.Clear();
            }
            navigationGate.Release();
        }
    }

    private async Task InitializeBrowserAsync()
    {
        string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lit3Mail", "HtmlPreview");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataPath);
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await EnsureCoreWebView2Async(environment, options);
        var core = CoreWebView2;
        await ApplyColorSchemeAsync(core);
        var settings = core.Settings;
        settings.IsScriptEnabled = true; // The document CSP blocks sender scripts.
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        core.WebMessageReceived += (_, e) =>
        {
            if (e.Source != core.Source || !Uri.TryCreate(e.Source, UriKind.Absolute, out var page) ||
                page.Host != PreviewHost)
                return;

            if (JsonSerializer.Deserialize<string>(e.WebMessageAsJson) is not { } message)
                return;

            int version = displayVersion;
            const string menuPrefix = "preview-menu:";
            if (message == "dismiss-preview-menu")
                BeginInvoke(() => previewMenu?.Close());
            else if (message.StartsWith(menuPrefix, StringComparison.Ordinal))
            {
                if (JsonSerializer.Deserialize<string?[]>(message[menuPrefix.Length..]) is [var selection, var source])
                {
                    Point location = Cursor.Position;
                    BeginInvoke(() => ShowPreviewMenu(selection, source, version, location));
                }
            }
            else if (allowRemoteImages)
                UseImage(message, version, save: false);
        };
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            if (documentReady is not null && navigationId is null && e.Uri == navigationTarget)
                navigationId = e.NavigationId;
            else
            {
                e.Cancel = true;
                OpenClickedLink(e.Uri, e.IsUserInitiated && !e.IsRedirected);
            }
        };
        core.DOMContentLoaded += async (_, e) =>
        {
            if (e.NavigationId != navigationId || documentReady is not { } ready)
                return;

            try
            {
                await core.ExecuteScriptAsync("""
                    document.addEventListener('keydown', event => {
                        if (event.ctrlKey && !event.altKey && !event.shiftKey &&
                            event.key.toLowerCase() === 'c' && globalThis.lit3mailSelectedImage &&
                            !getSelection()?.toString()) {
                            event.preventDefault();
                            chrome.webview.postMessage(globalThis.lit3mailSelectedImage);
                        }
                    });
                    document.addEventListener('pointerdown', event => {
                        if (event.button === 0)
                            chrome.webview.postMessage('dismiss-preview-menu');
                    }, true);
                    document.addEventListener('contextmenu', event => {
                        event.preventDefault();
                        const image = event.target.closest?.('img');
                        const selection = getSelection()?.toString() ?? '';
                        const source = image?.naturalWidth ? image.currentSrc : null;
                        chrome.webview.postMessage('preview-menu:' + JSON.stringify([selection, source]));
                    }, true);
                    document.addEventListener('click', event => {
                        const image = event.target.closest?.('img');
                        globalThis.lit3mailSelectedImage = image?.naturalWidth ? image.currentSrc : null;
                        if (globalThis.lit3mailSelectedImage) {
                            getSelection()?.removeAllRanges();
                            event.preventDefault();
                        }
                    });
                    """);
                if (await HasVisibleBodyAsync())
                    ready.TrySetResult(true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                ready.TrySetResult(false);
            }
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (e.NavigationId != navigationId || documentReady is not { } ready || ready.Task.IsCompleted)
                return;
            try
            {
                ready.TrySetResult(e.IsSuccess && await HasVisibleBodyAsync());
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                ready.TrySetResult(false);
            }
        };

        async Task<bool> HasVisibleBodyAsync() => await core.ExecuteScriptAsync("""
            (() => {
                const body = document.body;
                const visible = element => element.checkVisibility?.({ opacityProperty: true, visibilityProperty: true })
                    ?? element.getClientRects().length > 0;
                if (!body)
                    return false;
                return Boolean((visible(body) && body.innerText.trim()) ||
                    [...body.querySelectorAll('img, svg, table, hr')].some(element => visible(element) &&
                        element.getBoundingClientRect().height > 0 &&
                        (element.tagName !== 'IMG' || element.naturalWidth > 0)));
            })()
            """) == "true";
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenClickedLink(e.Uri, e.IsUserInitiated);
        };
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += async (_, e) =>
        {
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document &&
                e.Request.Uri == navigationTarget && navigationDocument is { } document)
            {
                string imageSources = allowRemoteImages ? "data: https: http:" : allowInlineImages ? "data:" : "'none'";
                if (inlineResources.Count > 0)
                    imageSources += " 'self'";
                string remoteSources = allowRemoteImages ? " https: http:" : "";
                string fontSources = allowRemoteImages ? "https: http:" : "'none'";
                string policy = $"default-src 'none'; script-src 'none'; style-src 'unsafe-inline'{remoteSources}; " +
                    $"font-src {fontSources}; img-src {imageSources}; " +
                    "base-uri 'none'; form-action 'none'; frame-src 'none'";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(document, writable: false), 200, "OK",
                    $"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {policy}\r\n" +
                    "Cache-Control: no-store\r\nReferrer-Policy: no-referrer");
            }
            else if (e.ResourceContext == CoreWebView2WebResourceContext.Image &&
                inlineResources.TryGetValue(e.Request.Uri, out var inline))
            {
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(inline.Bytes, writable: false), 200, "OK",
                    $"Content-Type: {inline.MediaType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            else if (!CanLoadResource(e.Request.Uri, e.ResourceContext, allowRemoteImages, allowInlineImages))
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            else if (GetRemoteResourceType(e.ResourceContext) is { } resource &&
                Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && RemoteMailImages.IsWebImageUrl(uri))
            {
                int version = displayVersion;
                var deferral = e.GetDeferral();
                MemoryStream? pendingBody = null;
                try
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                    if (await RemoteMailImages.DownloadAsync(uri, resource) is { } download)
                    {
                        pendingBody = download.Body;
                        if (!IsDisposed && version == displayVersion && allowRemoteImages)
                        {
                            e.Response = environment.CreateWebResourceResponse(download.Body, 200, "OK",
                                $"Content-Type: {download.MediaType}\r\nCache-Control: no-store\r\n" +
                                "X-Content-Type-Options: nosniff\r\nAccess-Control-Allow-Origin: *");
                            if (resource == RemoteMailResource.Image && !quoteImages.ContainsKey(uri) &&
                                download.Body.Length <= MaxQuoteImageBytes - quoteImageBytes)
                            {
                                byte[] bytes = download.Body.ToArray();
                                quoteImages.Add(uri, (bytes, download.MediaType));
                                quoteImageBytes += bytes.Length;
                            }
                            pendingBody = null;
                        }
                    }
                }
                catch (Exception error) when (error is System.Net.Http.HttpRequestException or IOException or
                    OperationCanceledException or UriFormatException or InvalidOperationException or
                    System.Runtime.InteropServices.COMException) { }
                finally
                {
                    pendingBody?.Dispose();
                    deferral.Complete();
                }
            }
        };
    }

    internal static Uri? GetClickedWebLink(string address, bool userInitiated) =>
        userInitiated && Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && uri.Host.Length > 0 &&
        !string.Equals(uri.Host, PreviewHost, StringComparison.OrdinalIgnoreCase) ? uri : null;

    private void OpenClickedLink(string address, bool userInitiated)
    {
        if (GetClickedWebLink(address, userInitiated) is not { } link)
            return;
        this.PostIfHandleAvailable(() =>
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ThemedPopup.Show(FindForm(), "Could not open the link in your default browser.", "Lit3Mail");
            }
        });
    }

    private void ShowPreviewMenu(string? selection, string? source, int version, Point location)
    {
        if (IsDisposed || version != displayVersion)
            return;

        previewMenu?.Close();
        var menu = previewMenu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (_, _) =>
        {
            if (version == displayVersion && !string.IsNullOrEmpty(selection))
            {
                try { Clipboard.SetText(selection); }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    ThemedPopup.Show(FindForm(), "Could not copy the selected text.", "Lit3Mail");
                }
            }
        }).Enabled = !string.IsNullOrEmpty(selection);
        if (allowRemoteImages && source is not null)
        {
            menu.Items.Add("Copy image", null, (_, _) => BeginInvoke(() => UseImage(source, version, save: false)));
            menu.Items.Add("Save image as...", null, (_, _) => BeginInvoke(() => UseImage(source, version, save: true)));
        }
        menu.Closed += (_, _) =>
        {
            previewMenu = null;
            BeginInvoke(() => menu.Dispose());
        };
        menu.Show(this, PointToClient(location));
    }

    private void UseImage(string source, int version, bool save)
    {
        if (IsDisposed || !allowRemoteImages || version != displayVersion)
            return;

        try
        {
            byte[]? bytes = GetQuoteImage(source);
            if (bytes is null && source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int separator = source.IndexOf(',');
                if (separator > 0 && source[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) &&
                    source.Length - separator <= MaxQuoteImageBytes * 4L / 3 + 4)
                    bytes = Convert.FromBase64String(source[(separator + 1)..]);
            }

            if (bytes is null)
                throw new InvalidOperationException("The image data is unavailable.");

            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream);
            if (save)
            {
                using var dialog = new SaveFileDialog
                {
                    Title = "Save image", FileName = "image.png", Filter = "PNG image (*.png)|*.png",
                    RestoreDirectory = true
                };
                if (dialog.ShowDialog(ThemedPopup.GetDialogOwner(FindForm())) == DialogResult.OK)
                    image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
            else
            {
                using var bitmap = new Bitmap(image);
                Clipboard.SetImage(bitmap);
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or
            IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            ThemedPopup.Show(FindForm(), save ? "Could not save the image." : "Could not copy the image.", "Lit3Mail");
        }
    }

    internal static bool CanLoadResource(string address, CoreWebView2WebResourceContext context, bool allowRemoteImages,
        bool allowInlineImages = false)
    {
        if (address == "about:blank")
            return true;
        if (GetRemoteResourceType(context) is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == "data" ? context == CoreWebView2WebResourceContext.Image && (allowInlineImages || allowRemoteImages) :
            allowRemoteImages && !uri.Host.Equals(PreviewHost, StringComparison.OrdinalIgnoreCase) &&
            RemoteMailImages.IsWebImageUrl(uri);
    }

    private static RemoteMailResource? GetRemoteResourceType(CoreWebView2WebResourceContext context) =>
        context switch
        {
            CoreWebView2WebResourceContext.Image => RemoteMailResource.Image,
            CoreWebView2WebResourceContext.Stylesheet => RemoteMailResource.Stylesheet,
            CoreWebView2WebResourceContext.Font => RemoteMailResource.Font,
            _ => null
        };

    internal static bool IsStylesheetLink(HtmlTagToken tag) =>
        tag.Id == HtmlTagId.Link && !tag.IsEndTag &&
        tag.Attributes.Any(attribute => attribute.Name.Equals("rel", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(attribute.Value?.Trim(), "stylesheet", StringComparison.OrdinalIgnoreCase));

    internal static string BuildDocument(string html, bool allowRemoteContent = false)
    {
        using var writer = new StringWriter();
        writer.Write("""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <style>html { background-color: white; color: black; color-scheme: light; } body { font-family: 'Segoe UI', sans-serif; overflow-wrap: anywhere; } img { max-width: 100%; }</style>
            """);
        writer.Write("</head>");

        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        while (tokenizer.ReadNextToken(out var token))
        {
            // Remove sender directives such as preconnect, refresh, and base URL changes.
            if (token is HtmlTagToken { Id: HtmlTagId.Link or HtmlTagId.Meta or HtmlTagId.Base } tag &&
                !(allowRemoteContent && IsStylesheetLink(tag)))
                continue;
            token.WriteTo(writer);
        }
        return writer.ToString();
    }
}




