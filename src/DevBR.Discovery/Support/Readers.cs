using System.Text;
using System.Text.Json;
using DevBR.Application.Machine;

namespace DevBR.Discovery.Support;

/// <summary>Bounded, tolerant readers for configuration formats. Content is parsed, never executed.</summary>
public static class ConfigReaders
{
    public const long MaxConfigBytes = 8L * 1024 * 1024;

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>Parses JSON or JSONC (VS Code style). Returns null when missing or malformed.</summary>
    public static JsonDocument? ReadJson(IMachineFileSystem fs, string path)
    {
        var text = fs.ReadText(path, MaxConfigBytes);
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text, JsoncOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ObjectKeys(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Object
            ? [.. child.EnumerateObject().Select(p => p.Name)]
            : [];

    public static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    /// <summary>MCP server names under a top-level key (mcpServers / servers), or empty.</summary>
    public static IReadOnlyList<string> McpServerNames(IMachineFileSystem fs, string path, params string[] keys)
    {
        using var document = ReadJson(fs, path);
        if (document is null)
        {
            return [];
        }

        return [.. keys.SelectMany(k => ObjectKeys(document.RootElement, k)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>TOML via Tomlyn; returns the top-level table or null when missing or malformed.</summary>
    public static Tomlyn.Model.TomlTable? ReadToml(IMachineFileSystem fs, string path)
    {
        var text = fs.ReadText(path, MaxConfigBytes);
        if (text is null)
        {
            return null;
        }

        try
        {
            return Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(text);
        }
        catch (Tomlyn.TomlException)
        {
            return null;
        }
    }
}

/// <summary>Minimal reader for git's INI-style config files (sections, subsections, includes).</summary>
public sealed class GitConfig
{
    private readonly List<(string Section, string? Subsection, string Key, string Value)> _entries = [];

    public static GitConfig Parse(string? text)
    {
        var config = new GitConfig();
        if (string.IsNullOrEmpty(text))
        {
            return config;
        }

        string section = string.Empty;
        string? subsection = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                var header = close > 0 ? line[1..close].Trim() : line[1..];
                var quote = header.IndexOf('"', StringComparison.Ordinal);
                if (quote >= 0)
                {
                    section = header[..quote].Trim().ToLowerInvariant();
                    subsection = header[(quote + 1)..].TrimEnd('"');
                }
                else
                {
                    var dot = header.IndexOf('.', StringComparison.Ordinal);
                    section = (dot >= 0 ? header[..dot] : header).ToLowerInvariant();
                    subsection = dot >= 0 ? header[(dot + 1)..] : null;
                }

                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var key = (equals >= 0 ? line[..equals] : line).Trim().ToLowerInvariant();
            var value = equals >= 0 ? line[(equals + 1)..].Trim() : "true";
            var comment = value.IndexOfAny(['#', ';']);
            if (comment >= 0 && !value.StartsWith('"'))
            {
                value = value[..comment].Trim();
            }

            config._entries.Add((section, subsection, key, value.Trim('"')));
        }

        return config;
    }

    public string? Get(string section, string? subsection, string key)
        => _entries.LastOrDefault(e => e.Section == section && string.Equals(e.Subsection, subsection, StringComparison.Ordinal) && e.Key == key).Value;

    public IEnumerable<(string? Subsection, string Value)> GetAll(string section, string key)
        => _entries.Where(e => e.Section == section && e.Key == key).Select(e => (e.Subsection, e.Value));

    public IEnumerable<string> Subsections(string section)
        => _entries.Where(e => e.Section == section && e.Subsection is not null).Select(e => e.Subsection!).Distinct(StringComparer.Ordinal);

    /// <summary>include.path and includeIf.*.path values.</summary>
    public IEnumerable<string> IncludePaths()
        => _entries.Where(e => (e.Section is "include" or "includeif") && e.Key == "path").Select(e => e.Value);
}

/// <summary>Reads the local target path of a Windows shortcut (MS-SHLLINK) without the shell.</summary>
public static class ShellLinkReader
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;

    public static string? ReadTarget(byte[]? data)
    {
        if (data is null || data.Length < HeaderSize + 4 || BitConverter.ToInt32(data, 0) != HeaderSize)
        {
            return null;
        }

        try
        {
            var flags = BitConverter.ToUInt32(data, 0x14);
            var offset = HeaderSize;
            if ((flags & HasLinkTargetIdList) != 0)
            {
                offset += 2 + BitConverter.ToUInt16(data, offset);
            }

            if ((flags & HasLinkInfo) == 0 || offset + 0x1C > data.Length)
            {
                return null;
            }

            var linkInfoSize = BitConverter.ToInt32(data, offset);
            var linkInfoFlags = BitConverter.ToInt32(data, offset + 8);
            var localBasePathOffset = BitConverter.ToInt32(data, offset + 0x10);
            if ((linkInfoFlags & 1) == 0 || localBasePathOffset <= 0 || localBasePathOffset >= linkInfoSize)
            {
                return null;
            }

            var start = offset + localBasePathOffset;
            var end = Array.IndexOf(data, (byte)0, start);
            if (end < 0 || end > offset + linkInfoSize)
            {
                return null;
            }

            var target = Encoding.Default.GetString(data, start, end - start);
            return target.Length >= 3 && target[1] == ':' ? target : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
