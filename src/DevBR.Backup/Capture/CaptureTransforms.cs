using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevBR.Backup.Capture;

/// <summary>
/// Structured redaction applied while staging, where an adapter can do it safely. Files that cannot be
/// parsed are omitted rather than partially redacted, so they are never corrupted.
/// </summary>
public static class CaptureTransforms
{
    private const long MaxTransformBytes = 8L * 1024 * 1024;

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The transform for a file of an artifact, if any. Keyed by artifact id and file name.</summary>
    public static Func<byte[], byte[]?>? For(string artifactId, string fileName) => (artifactId, fileName.ToLowerInvariant()) switch
    {
        // Only the user-scope MCP servers; the rest of .claude.json is account and session state.
        ("claude-code:user-mcp", ".claude.json") => KeepOnly("mcpServers"),

        // Remembered folder-trust approvals are machine-specific decisions; never carried over.
        ("copilot:settings", "config.json") => Remove("trusted_folders", "trustedFolders"),

        // Inline registry credentials are removed; the credential-helper choice is kept.
        ("docker:client-config", "config.json") => RemoveRegistryAuth(),
        _ => null,
    };

    /// <summary>Whether an artifact's recognized secrets are fully removed by its transform.</summary>
    public static bool RedactsSecrets(string artifactId) => artifactId is "docker:client-config" or "claude-code:user-mcp";

    public static long MaxBytes => MaxTransformBytes;

    private static Func<byte[], byte[]?> KeepOnly(params string[] properties) => bytes =>
    {
        if (Parse(bytes) is not JsonObject root)
        {
            return null;
        }

        var kept = new JsonObject();
        foreach (var property in properties)
        {
            if (root[property] is { } value)
            {
                kept[property] = value.DeepClone();
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(kept, Indented);
    };

    private static Func<byte[], byte[]?> Remove(params string[] properties) => bytes =>
    {
        if (Parse(bytes) is not JsonObject root)
        {
            return null;
        }

        foreach (var property in properties)
        {
            root.Remove(property);
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, Indented);
    };

    private static Func<byte[], byte[]?> RemoveRegistryAuth() => bytes =>
    {
        if (Parse(bytes) is not JsonObject root)
        {
            return null;
        }

        if (root["auths"] is JsonObject auths)
        {
            foreach (var (_, registry) in auths.ToList())
            {
                if (registry is JsonObject entry)
                {
                    entry.Remove("auth");
                    entry.Remove("identitytoken");
                    entry.Remove("password");
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, Indented);
    };

    private static JsonNode? Parse(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes, documentOptions: Jsonc);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
