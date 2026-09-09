using System.Text;
using SoulScreen.AirPlay.Plist;

namespace SoulScreen.AirPlay.Rtsp;

/// <summary>
/// A request on the AirPlay control channel.
/// <para>
/// The channel is nominally RTSP/1.0, but iOS freely mixes in HTTP/1.1 requests such as
/// <c>GET /info</c> and <c>POST /fp-setup</c> on the very same socket, so one parser has
/// to accept both and the reply must echo whichever protocol version came in.
/// </para>
/// </summary>
public sealed class RtspRequest
{
    public required string Method { get; init; }
    public required string Uri { get; init; }
    public required string Protocol { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];

    public string? ContentType => Headers.GetValueOrDefault("Content-Type");
    public int CSeq => int.TryParse(Headers.GetValueOrDefault("CSeq"), out var v) ? v : 0;
    public string? UserAgent => Headers.GetValueOrDefault("User-Agent");
    public string? ClientInstance => Headers.GetValueOrDefault("Client-Instance");
    public string? ActiveRemote => Headers.GetValueOrDefault("Active-Remote");
    public string? DacpId => Headers.GetValueOrDefault("DACP-ID");

    /// <summary>Path with any query string removed, e.g. "/fp-setup".</summary>
    public string Path
    {
        get
        {
            var uri = Uri;
            // Senders sometimes use an absolute form: rtsp://192.168.1.5/12345678
            if (uri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var slash = uri.IndexOf('/', uri.IndexOf("//", StringComparison.Ordinal) + 2);
                uri = slash >= 0 ? uri[slash..] : "/";
            }
            var query = uri.IndexOf('?');
            return query >= 0 ? uri[..query] : uri;
        }
    }

    /// <summary>Parses the body as a property list, or returns null when it is not one.</summary>
    public PlistDictionary? BodyAsPlist() =>
        PlistSerializer.TryRead(Body, out var value) ? value as PlistDictionary : null;

    public override string ToString() => $"{Method} {Uri} {Protocol} ({Body.Length}B)";
}

public sealed class RtspResponse
{
    public int StatusCode { get; set; } = 200;
    public string ReasonPhrase { get; set; } = "OK";
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];

    public static RtspResponse Ok() => new();

    public static RtspResponse Status(int code, string reason) => new() { StatusCode = code, ReasonPhrase = reason };

    public static RtspResponse NotFound() => Status(404, "Not Found");

    public static RtspResponse BadRequest() => Status(400, "Bad Request");

    public static RtspResponse NotImplemented() => Status(501, "Not Implemented");

    /// <summary>403 is what a receiver returns when pairing or FairPlay verification fails.</summary>
    public static RtspResponse Forbidden() => Status(403, "Forbidden");

    public static RtspResponse Binary(byte[] payload, string contentType = "application/octet-stream")
    {
        var response = Ok();
        response.Body = payload;
        response.Headers["Content-Type"] = contentType;
        return response;
    }

    public static RtspResponse BinaryPlistBody(PlistValue value)
        => Binary(BinaryPlist.Write(value), "application/x-apple-binary-plist");

    public static RtspResponse XmlPlistBody(PlistValue value)
        => Binary(XmlPlist.Write(value), "text/x-apple-plist+xml");

    public static RtspResponse Text(string text, string contentType = "text/parameters")
        => Binary(Encoding.UTF8.GetBytes(text), contentType);

    public byte[] Serialize(string protocol, int cseq, string serverName)
    {
        var head = new StringBuilder(256);
        head.Append(protocol).Append(' ').Append(StatusCode).Append(' ').Append(ReasonPhrase).Append("\r\n");

        if (!Headers.ContainsKey("CSeq") && cseq > 0) head.Append("CSeq: ").Append(cseq).Append("\r\n");
        if (!Headers.ContainsKey("Server")) head.Append("Server: ").Append(serverName).Append("\r\n");
        // Apple receivers always send Content-Length, even for empty bodies; some senders
        // hang waiting for it otherwise.
        if (!Headers.ContainsKey("Content-Length")) head.Append("Content-Length: ").Append(Body.Length).Append("\r\n");

        foreach (var (key, value) in Headers)
            head.Append(key).Append(": ").Append(value).Append("\r\n");

        head.Append("\r\n");

        var headBytes = Encoding.UTF8.GetBytes(head.ToString());
        if (Body.Length == 0) return headBytes;

        var buffer = new byte[headBytes.Length + Body.Length];
        headBytes.CopyTo(buffer, 0);
        Body.CopyTo(buffer, headBytes.Length);
        return buffer;
    }

    public override string ToString() => $"{StatusCode} {ReasonPhrase} ({Body.Length}B)";
}
