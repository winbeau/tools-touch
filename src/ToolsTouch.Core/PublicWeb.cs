using System.Net;
using System.Net.Sockets;
using System.Text;
using AngleSharp.Html.Parser;

namespace ToolsTouch.Core;

public sealed record WebDocument(string Url, string Title, string Text, string[] Links, DateTimeOffset FetchedAt);
public sealed record WebBytes(Uri Url, string MediaType, byte[] Bytes);

public sealed class PublicWeb : IDisposable
{
    private readonly HttpClient client;

    public PublicWeb()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                    throw new InvalidOperationException("PRIVATE_ADDRESS_FORBIDDEN");
                // Connect to the checked address directly, preventing a second DNS lookup/rebinding.
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception error) { socket.Dispose(); last = error; }
                }
                throw new HttpRequestException("PUBLIC_CONNECTION_FAILED", last);
            }
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ToolsTouch/0.1");
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 2) &&
                !(bytes[0] == 0x20 && bytes[1] == 1 && (bytes[2] == 0 && bytes[3] == 0 || bytes[2] == 0x0d && bytes[3] == 0xb8));
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
            !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 || bytes[1] == 2)) &&
            !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100)) &&
            !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    public static Uri ValidateUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType == UriHostNameType.Unknown ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && !IsPublicAddress(address))
            throw new InvalidOperationException("PUBLIC_URL_REQUIRED");
        return uri;
    }

    public async Task<WebBytes> DownloadAsync(string url, int maxBytes = 2_000_000, CancellationToken cancellationToken = default)
    {
        var uri = ValidateUri(url);
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = ValidateUri(new Uri(uri, response.Headers.Location ?? throw new HttpRequestException("REDIRECT_WITHOUT_LOCATION")).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("CONTENT_TOO_LARGE");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var destination = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (destination.Length + count > maxBytes) throw new InvalidOperationException("CONTENT_TOO_LARGE");
                destination.Write(buffer, 0, count);
            }
            return new(uri, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", destination.ToArray());
        }
        throw new HttpRequestException("TOO_MANY_REDIRECTS");
    }

    public async Task<WebDocument> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await DownloadAsync(url, cancellationToken: cancellationToken);
        if (result.MediaType is not ("text/html" or "text/plain" or "application/xhtml+xml"))
            throw new InvalidOperationException("UNSUPPORTED_PAGE_TYPE");
        var html = Encoding.UTF8.GetString(result.Bytes);
        if (result.MediaType == "text/plain") return new(result.Url.AbsoluteUri, "", html[..Math.Min(html.Length, 40000)], [], DateTimeOffset.UtcNow);
        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);
        foreach (var element in document.QuerySelectorAll("script,style,noscript,iframe,svg")) element.Remove();
        var links = document.QuerySelectorAll("meta[name='citation_pdf_url']").Select(element => element.GetAttribute("content"))
            .Concat(document.QuerySelectorAll("a[href]").Select(element => element.GetAttribute("href")))
            .Where(link => Uri.TryCreate(result.Url, link, out var target) && target.Scheme is "http" or "https")
            .Select(link => new Uri(result.Url, link!).AbsoluteUri).Distinct().Take(100).ToArray();
        var text = document.Body?.TextContent ?? document.DocumentElement.TextContent;
        text += "\n" + string.Join("\n", document.QuerySelectorAll("a[href^='mailto:']").Select(element => element.GetAttribute("href")));
        return new(result.Url.AbsoluteUri, document.Title ?? "", text[..Math.Min(text.Length, 40000)], links, DateTimeOffset.UtcNow);
    }

    public void Dispose() => client.Dispose();
}
