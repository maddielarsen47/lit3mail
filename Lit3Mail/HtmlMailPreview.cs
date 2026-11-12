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
    private readonly Dictionary<Uri, byte[]> quoteImages = new();
    private int quoteImageBytes;
    private Task? initialization;
    private TaskCompletionSource<bool>? documentReady;
    private ulong? navigationId;
    private string? navigationTarget;
    private byte[]? navigationDocument;
    private int displayVersion;
    private bool allowRemoteImages;

    public byte[]? GetQuoteImage(string source) =>
        allowRemoteImages && Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        quoteImages.TryGetValue(uri, out var bytes) ? bytes : null;

    public void ClearDisplay()
    {
        ++displayVersion;
        allowRemoteImages = false;
        quoteImages.Clear();
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

    public async Task<bool> DisplayAsync(string html, bool showRemoteImages)
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

            navigationDocument = Encoding.UTF8.GetBytes(BuildDocument(html));
            navigationTarget = $"http://{PreviewHost}/{Guid.NewGuid():N}";
            allowRemoteImages = showRemoteImages;
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
            return false;
        }
        finally
        {
            documentReady = null;
            navigationId = null;
            navigationTarget = null;
            navigationDocument = null;
            if (!showingDocument)
                allowRemoteImages = false;
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
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
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
        core.DOMContentLoaded += (_, e) =>
        {
            if (e.NavigationId == navigationId)
                documentReady?.TrySetResult(true);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.NavigationId == navigationId && !e.IsSuccess)
                documentReady?.TrySetResult(false);
        };
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
                string imageSources = allowRemoteImages ? "data: https: http:" : "data:";
                string policy = $"default-src 'none'; style-src 'unsafe-inline'; img-src {imageSources}; " +
                    "base-uri 'none'; form-action 'none'; frame-src 'none'";
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(document, writable: false), 200, "OK",
                    $"Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: {policy}\r\n" +
                    "Cache-Control: no-store\r\nReferrer-Policy: no-referrer");
            }
            else if (!CanLoadResource(e.Request.Uri, e.ResourceContext, allowRemoteImages))
                e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            else if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && RemoteMailImages.IsWebImageUrl(uri))
            {
                int version = displayVersion;
                var deferral = e.GetDeferral();
                MemoryStream? pendingBody = null;
                try
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                    if (await RemoteMailImages.DownloadAsync(uri) is { } image)
                    {
                        pendingBody = image.Body;
                        if (!IsDisposed && version == displayVersion && allowRemoteImages)
                        {
                            e.Response = environment.CreateWebResourceResponse(image.Body, 200, "OK",
                                $"Content-Type: {image.MediaType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
                            if (!quoteImages.ContainsKey(uri) && image.Body.Length <= MaxQuoteImageBytes - quoteImageBytes)
                            {
                                byte[] bytes = image.Body.ToArray();
                                quoteImages.Add(uri, bytes);
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

    internal static bool CanLoadResource(string address, CoreWebView2WebResourceContext context, bool allowRemoteImages)
    {
        if (address == "about:blank")
            return true;
        if (context != CoreWebView2WebResourceContext.Image ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        return uri.Scheme == "data" || (allowRemoteImages && RemoteMailImages.IsWebImageUrl(uri));
    }

    internal static string BuildDocument(string html)
    {
        using var writer = new StringWriter();
        writer.Write("""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <style>body { font-family: 'Segoe UI', sans-serif; overflow-wrap: anywhere; } img { max-width: 100%; }</style>
            </head>
            """);

        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        while (tokenizer.ReadNextToken(out var token))
        {
            // Remove sender directives such as preconnect, refresh, and base URL changes.
            if (token is HtmlTagToken { Id: HtmlTagId.Link or HtmlTagId.Meta or HtmlTagId.Base })
                continue;
            token.WriteTo(writer);
        }
        return writer.ToString();
    }
}




