using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RestPeach.Core.Text;

namespace RestPeach.Core.Http;

/// <summary>One Server-Sent Event: its type ("message" when the stream names none), its data and its id.</summary>
public sealed record SseEvent(string Event, string Data, string? Id);

/// <summary>
/// Reads a <c>text/event-stream</c> as it arrives (chunks may cut a line, or a UTF-8 character, anywhere). Follows
/// the WHATWG rules: <c>field: value</c> lines, <c>:</c> comments, several <c>data</c> lines joined with "\n", and an
/// event dispatched on the blank line that ends it.
/// </summary>
public sealed class SseParser
{
    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _event = "";
    private string? _id;
    private bool _hasData, _afterCr, _started;

    public static bool IsEventStream(string? contentType) =>
        contentType is not null && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    public List<SseEvent> Feed(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[_utf8.GetCharCount(bytes, flush: false)];
        _utf8.GetChars(bytes, chars, flush: false);
        return Feed(chars);
    }

    public List<SseEvent> Feed(ReadOnlySpan<char> text)
    {
        var events = new List<SseEvent>();
        foreach (var c in text)
        {
            if (!_started) { _started = true; if (c == '\uFEFF') continue; }
            if (_afterCr) { _afterCr = false; if (c == '\n') continue; }
            if (c is '\r' or '\n')
            {
                _afterCr = c == '\r';
                Line(_line.ToString(), events);
                _line.Clear();
            }
            else _line.Append(c);
        }
        return events;
    }

    private void Line(string line, List<SseEvent> events)
    {
        if (line.Length == 0)
        {
            if (_hasData) events.Add(new SseEvent(_event.Length > 0 ? _event : "message", _data.ToString(), _id));
            _data.Clear();
            _event = "";
            _hasData = false;
            return;
        }
        if (line[0] == ':') return;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
            case "event": _event = value; break;
            case "id" when !value.Contains('\0'): _id = value; break;
        }
    }

    /// <summary>Every complete event of a whole stream.</summary>
    public static List<SseEvent> Parse(string text) => new SseParser().Feed(text.AsSpan());

    /// <summary>
    /// The events as a JSON array, what tests see: <c>[{"event": "message", "data": …, "id": "1"}]</c>; a data that
    /// is JSON is kept as JSON, so <c>$[0].data.choices[0].delta.content</c> works.
    /// </summary>
    public static string ToJson(IEnumerable<SseEvent> events)
    {
        var array = new JsonArray();
        foreach (var e in events)
        {
            var o = new JsonObject { ["event"] = e.Event };
            JsonNode? data;
            try { data = LooksLikeJson(e.Data) ? JsonNode.Parse(e.Data) : JsonValue.Create(e.Data); }
            catch (JsonException) { data = JsonValue.Create(e.Data); }
            o["data"] = data;
            if (e.Id is not null) o["id"] = e.Id;
            array.Add(o);
        }
        return array.ToJsonString(CanonicalJson.StringOptions);
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }
}
