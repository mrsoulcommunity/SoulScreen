using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SoulScreen.AirPlay.Plist;

/// <summary>
/// The textual <c>&lt;plist version="1.0"&gt;</c> form. iOS sends it for a handful of
/// requests (notably some /info and SET_PARAMETER bodies) and expects it back when it
/// asked with <c>Content-Type: text/x-apple-plist+xml</c>.
/// </summary>
public static class XmlPlist
{
    public static bool LooksLikeXmlPlist(ReadOnlySpan<byte> data)
    {
        // Skip a UTF-8 BOM and leading whitespace before looking for the declaration.
        var i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < data.Length && data[i] == (byte)'<';
    }

    public static PlistValue Read(ReadOnlySpan<byte> data) => Read(Encoding.UTF8.GetString(data));

    public static PlistValue Read(string xml)
    {
        // DTD processing stays off: the plist DOCTYPE points at apple.com and we never
        // want a parse to reach out to the network.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);
        var document = XDocument.Load(reader);

        var root = document.Root ?? throw new InvalidDataException("XML plist has no root element.");
        var body = root.Name.LocalName == "plist" ? root.Elements().FirstOrDefault() : root;
        return body is null ? PlistValue.Null : ParseElement(body);
    }

    private static PlistValue ParseElement(XElement element) => element.Name.LocalName switch
    {
        "true" => new PlistBoolean(true),
        "false" => new PlistBoolean(false),
        "integer" => new PlistInteger(long.Parse(element.Value.Trim(), CultureInfo.InvariantCulture)),
        "real" => new PlistReal(double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture)),
        "string" => new PlistString(element.Value),
        "data" => new PlistData(Convert.FromBase64String(StripWhitespace(element.Value))),
        "date" => new PlistDate(DateTime.Parse(element.Value.Trim(), CultureInfo.InvariantCulture,
                                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)),
        "array" => new PlistArray(element.Elements().Select(ParseElement)),
        "dict" => ParseDictionary(element),
        _ => PlistValue.Null,
    };

    private static PlistDictionary ParseDictionary(XElement element)
    {
        var dict = new PlistDictionary();
        string? pendingKey = null;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "key")
            {
                pendingKey = child.Value;
            }
            else if (pendingKey is not null)
            {
                dict[pendingKey] = ParseElement(child);
                pendingKey = null;
            }
        }
        return dict;
    }

    private static string StripWhitespace(string value)
    {
        Span<char> buffer = value.Length <= 1024 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var c in value)
            if (!char.IsWhiteSpace(c)) buffer[n++] = c;
        return new string(buffer[..n]);
    }

    public static byte[] Write(PlistValue root) => Encoding.UTF8.GetBytes(WriteToString(root));

    public static string WriteToString(PlistValue root)
    {
        var sb = new StringBuilder(512);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteElement(sb, root, 1);
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    private static void WriteElement(StringBuilder sb, PlistValue value, int indent)
    {
        var pad = new string('\t', indent);
        switch (value)
        {
            case PlistBoolean b:
                sb.Append(pad).Append(b.Value ? "<true/>" : "<false/>").Append('\n');
                break;
            case PlistInteger i:
                sb.Append(pad).Append("<integer>").Append(i.Value.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case PlistReal r:
                sb.Append(pad).Append("<real>").Append(r.Value.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case PlistString s:
                sb.Append(pad).Append("<string>").Append(Escape(s.Value)).Append("</string>\n");
                break;
            case PlistData d:
                sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(d.Value)).Append("</data>\n");
                break;
            case PlistDate date:
                sb.Append(pad).Append("<date>").Append(date.ValueUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</date>\n");
                break;
            case PlistArray array:
                if (array.Count == 0) { sb.Append(pad).Append("<array/>\n"); break; }
                sb.Append(pad).Append("<array>\n");
                foreach (var item in array) WriteElement(sb, item, indent + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            case PlistDictionary dict:
                if (dict.Count == 0) { sb.Append(pad).Append("<dict/>\n"); break; }
                sb.Append(pad).Append("<dict>\n");
                foreach (var kv in dict)
                {
                    sb.Append(pad).Append('\t').Append("<key>").Append(Escape(kv.Key)).Append("</key>\n");
                    WriteElement(sb, kv.Value, indent + 1);
                }
                sb.Append(pad).Append("</dict>\n");
                break;
            default:
                sb.Append(pad).Append("<string></string>\n");
                break;
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>Format-sniffing entry point used by the RTSP layer, which cannot know in
/// advance whether a sender chose the binary or the XML encoding.</summary>
public static class PlistSerializer
{
    public static PlistValue Read(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return PlistValue.Null;
        if (BinaryPlist.LooksLikeBinaryPlist(body)) return BinaryPlist.Read(body);
        if (XmlPlist.LooksLikeXmlPlist(body)) return XmlPlist.Read(body);
        throw new InvalidDataException("Body is neither a binary nor an XML property list.");
    }

    public static bool TryRead(ReadOnlySpan<byte> body, out PlistValue value)
    {
        try
        {
            value = Read(body);
            return true;
        }
        catch (Exception)
        {
            value = PlistValue.Null;
            return false;
        }
    }
}
