using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Lit3Mail;

internal enum RemoteMailResource { Image, Stylesheet, Font }

internal static class RemoteMailImages
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private static readonly SemaphoreSlim DownloadSlots = new(4);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = ConnectPublicAsync
    });

    internal static bool IsWebImageUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
        uri.IsDefaultPort;

    internal static async Task<(MemoryStream Body, string MediaType)?> DownloadAsync(Uri uri, RemoteMailResource resource)
    {
        using var queueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await DownloadSlots.WaitAsync(queueTimeout.Token).ConfigureAwait(false);
        try
        {
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await DownloadCoreAsync(uri, resource, downloadTimeout.Token).ConfigureAwait(false);
        }
        finally
        {
            DownloadSlots.Release();
        }
    }

    private static async Task<(MemoryStream Body, string MediaType)?> DownloadCoreAsync(Uri uri, RemoteMailResource resource, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 4 && IsWebImageUrl(uri); redirects++)
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 &&
                response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || !MatchesResourceType(resource, mediaType) ||
                response.Content.Headers.ContentLength > MaxImageBytes)
                return null;

            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var body = new MemoryStream();
            try
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + count > MaxImageBytes)
                    {
                        body.Dispose();
                        return null;
                    }
                    body.Write(buffer, 0, count);
                }
                if (mediaType is null)
                {
                    if (!body.GetBuffer().AsSpan(0, (int)body.Length).StartsWith(PngSignature))
                    {
                        body.Dispose();
                        return null;
                    }
                    mediaType = "image/png";
                }

                body.Position = 0;
                return (body, mediaType);
            }
            catch
            {
                body.Dispose();
                throw;
            }
        }
        return null;
    }

    private static bool MatchesResourceType(RemoteMailResource resource, string? mediaType) =>
        resource switch
        {
            RemoteMailResource.Image => mediaType is null || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            RemoteMailResource.Stylesheet => string.Equals(mediaType, "text/css", StringComparison.OrdinalIgnoreCase),
            RemoteMailResource.Font => mediaType?.StartsWith("font/", StringComparison.OrdinalIgnoreCase) == true ||
                mediaType?.ToLowerInvariant() is "application/font-woff" or "application/font-woff2" or "application/vnd.ms-fontobject",
            _ => false
        };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Could not connect to a public image server.");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is > 0 and < 224 and not (10 or 127) &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) &&
                !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] is 0 or 168) &&
                !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
                !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        return (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || (bytes[2] == 0x0d && bytes[3] == 0xb8))) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && bytes[2] < 0x10);
    }
}
