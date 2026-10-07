using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevBR.Restore;

/// <summary>
/// Applies a merge result to a JSON/JSONC file by editing only the spans that changed, so comments,
/// ordering and formatting elsewhere in the file survive. The edited text is parsed again and must equal
/// the merge result exactly; otherwise the caller falls back to writing the merged document.
/// </summary>
public static class JsoncEditor
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <returns>The edited file, or null when a span edit cannot reproduce <paramref name="merged"/> exactly.</returns>
    public static byte[]? Apply(byte[] original, JsonNode merged)
    {
        var bom = original.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bom ? original[Encoding.UTF8.Preamble.Length..] : original);
        var target = JsonMerger.Parse(Encoding.UTF8.GetBytes(text));
        if (target is not JsonObject targetObject || merged is not JsonObject mergedObject)
        {
            return null;
        }

        string edited;
        try
        {
            var start = text.IndexOf('{', StringComparison.Ordinal);
            var end = ObjectEnd(text, start);
            var unit = IndentUnit(text);
            edited = string.Concat(text.AsSpan(0, start), EditObject(text[start..end], targetObject, mergedObject, unit, 1), text.AsSpan(end));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(edited);
        if (!JsonNode.DeepEquals(JsonMerger.Parse(bytes), merged))
        {
            return null;
        }

        return bom ? [.. Encoding.UTF8.Preamble, .. bytes] : bytes;
    }

    /// <summary>Serializes a document as plain indented JSON (the fallback when span editing is not possible).</summary>
    public static byte[] Serialize(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(WriteOptions) + Environment.NewLine);

    private static string EditObject(string text, JsonObject target, JsonObject merged, string unit, int depth)
    {
        var (properties, _) = Scan(text);
        var edits = new List<(int Start, int End, string Replacement)>();
        var additions = new StringBuilder();

        foreach (var (name, node) in merged)
        {
            var existing = properties.FirstOrDefault(p => p.Name == name);
            if (existing.Name is null)
            {
                var indent = string.Concat(Enumerable.Repeat(unit, depth));
                additions.Append(properties.Count == 0 && additions.Length == 0 ? string.Empty : ",")
                    .Append('\n').Append(indent).Append(JsonSerializer.Serialize(name, WriteOptions)).Append(": ").Append(Format(node, unit, depth));
                continue;
            }

            var before = target[name];
            if (JsonNode.DeepEquals(before, node))
            {
                continue;
            }

            var span = text[existing.Start..existing.End];
            var replacement = before is JsonObject beforeObject && node is JsonObject nodeObject
                ? EditObject(span, beforeObject, nodeObject, unit, depth + 1)
                : Format(node, unit, depth);
            edits.Add((existing.Start, existing.End, replacement));
        }

        if (additions.Length > 0 && properties.Count == 0)
        {
            // An empty object: rewrite it whole (comments inside an empty object are not kept).
            return "{" + additions + "\n" + string.Concat(Enumerable.Repeat(unit, depth - 1)) + "}";
        }

        var builder = new StringBuilder(text);
        if (additions.Length > 0)
        {
            // After the last value; an existing trailing comma then follows the new entries, which JSONC allows.
            var at = properties.Max(p => p.End);
            edits.Add((at, at, additions.ToString()));
        }

        foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            builder.Remove(start, end - start).Insert(start, replacement);
        }

        return builder.ToString();
    }

    private static string Format(JsonNode? node, string unit, int depth)
    {
        var lines = (node?.ToJsonString(WriteOptions) ?? "null").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            // The serializer indents by two spaces per level; re-indent with the file's own unit.
            var trimmed = lines[i].TrimStart(' ');
            var level = (lines[i].Length - trimmed.Length) / 2;
            lines[i] = string.Concat(Enumerable.Repeat(unit, depth + level)) + trimmed;
        }

        return string.Join('\n', lines);
    }

    /// <summary>The top-level properties of an object literal: names and the character span of each value.</summary>
    private static (List<(string Name, int Start, int End)> Properties, int Close) Scan(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var properties = new List<(string, int, int)>();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("Not an object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (properties, CharIndex(bytes, (int)reader.TokenStartIndex));
            }

            var name = reader.GetString()!;
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            properties.Add((name, CharIndex(bytes, start), CharIndex(bytes, (int)reader.BytesConsumed)));
        }

        throw new InvalidOperationException("Unterminated object.");
    }

    private static int ObjectEnd(string text, int start)
    {
        var bytes = Encoding.UTF8.GetBytes(text[start..]);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        reader.Read();
        reader.Skip();
        return start + CharIndex(bytes, (int)reader.BytesConsumed);
    }

    private static int CharIndex(byte[] utf8, int byteIndex) => Encoding.UTF8.GetCharCount(utf8, 0, byteIndex);

    private static string IndentUnit(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith('"') && trimmed.Length < line.Length)
            {
                return line[..(line.Length - trimmed.Length)];
            }
        }

        return "  ";
    }
}
