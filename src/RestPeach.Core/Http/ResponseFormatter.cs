using System.Xml;
using System.Xml.Linq;
using RestPeach.Core.Text;

namespace RestPeach.Core.Http;

public enum BodyKind { Empty, Json, Xml, Html, Text, Image, Binary }

/// <summary>A response body ready to show: re-indented when it is JSON or XML, plus what it is.</summary>
public sealed record FormattedBody(BodyKind Kind, string Text, bool Reformatted)
{
    /// <summary>File extension the highlighter understands for this kind.</summary>
    public string Extension => Kind switch
    {
        BodyKind.Json => ".json",
        BodyKind.Xml => ".xml",
        BodyKind.Html => ".html",
        _ => ".txt",
    };
}

public static class ResponseFormatter
{
    public const int MaxShownChars = 16 * 1024 * 1024;

    public static BodyKind Detect(byte[] body, string? contentType)
    {
        if (body.Length == 0) return BodyKind.Empty;
        var ct = (contentType ?? "").ToLowerInvariant();
        if (ct.StartsWith("image/") && !ct.Contains("svg")) return BodyKind.Image;
        if (ct.Contains("json")) return BodyKind.Json;
        if (ct.Contains("html")) return BodyKind.Html;
        if (ct.Contains("xml")) return BodyKind.Xml;
        if (LooksBinary(body) && !ct.StartsWith("text/")) return BodyKind.Binary;
        // Servers often say text/plain (or nothing) for JSON: sniff the first significant character.
        var head = ApiResponse.Decode(body.Length > 512 ? body[..512] : body, contentType).TrimStart();
        if (head.StartsWith('{') || head.StartsWith('[')) return BodyKind.Json;
        if (head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return BodyKind.Html;
        if (head.StartsWith("<?xml") || head.StartsWith('<')) return BodyKind.Xml;
        return BodyKind.Text;
    }

    private static bool LooksBinary(byte[] body)
    {
        int n = Math.Min(body.Length, 8000);
        for (int i = 0; i < n; i++) if (body[i] == 0) return true;
        return false;
    }

    public static FormattedBody Format(ApiResponse r, bool pretty = true)
    {
        var kind = Detect(r.Body, r.ContentType);
        if (kind is BodyKind.Empty or BodyKind.Image or BodyKind.Binary) return new FormattedBody(kind, "", false);
        var text = r.Text;
        if (text.Length > MaxShownChars) return new FormattedBody(BodyKind.Text, text[..MaxShownChars], false);
        if (!pretty) return new FormattedBody(kind, text, false);
        switch (kind)
        {
            case BodyKind.Json when CanonicalJson.Pretty(text) is { } json:
                return new FormattedBody(kind, json, json != text);
            case BodyKind.Json:
                return new FormattedBody(BodyKind.Text, text, false); // said JSON, is not
            case BodyKind.Xml when PrettyXml(text) is { } xml:
                return new FormattedBody(kind, xml, xml != text);
            default:
                return new FormattedBody(kind, text, false);
        }
    }

    public static string? PrettyXml(string text)
    {
        try
        {
            var doc = XDocument.Parse(text, LoadOptions.None);
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", OmitXmlDeclaration = doc.Declaration is null };
            using var sw = new StringWriter();
            using (var w = XmlWriter.Create(sw, settings)) doc.Save(w);
            return sw.ToString();
        }
        catch (XmlException) { return null; }
    }

    /// <summary>"512 B", "1.4 KB", "2.0 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    /// <summary>"85 ms", "1.24 s".</summary>
    public static string Duration(double ms) => ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.00} s";
}
