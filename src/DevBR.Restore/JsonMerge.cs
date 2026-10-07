using System.Text.Json;
using System.Text.Json.Nodes;
using DevBR.Discovery.Support;

namespace DevBR.Restore;

public enum MergeChangeKind
{
    Added,
    Unchanged,
    ConflictKeptTarget,
    ConflictUsedBackup,
}

/// <param name="Path">Setting identity, e.g. "editor.fontSize" or "mcpServers.docs".</param>
public sealed record MergeChange(string Path, MergeChangeKind Kind, string? BackupValue, string? TargetValue);

public sealed record MergePreview(string TargetPath, IReadOnlyList<MergeChange> Changes)
{
    public int Added => Changes.Count(c => c.Kind == MergeChangeKind.Added);

    public int Conflicts => Changes.Count(c => c.Kind is MergeChangeKind.ConflictKeptTarget or MergeChangeKind.ConflictUsedBackup);
}

/// <summary>How one kind of configuration file is merged: which keys, array identities, atomic units, path fields.</summary>
public sealed class MergeProfile
{
    /// <summary>When set, only these top-level keys are merged (the rest of the target file is left alone).</summary>
    public IReadOnlyList<string>? ScopeKeys { get; init; }

    /// <summary>Paths ("mcpServers.*") whose values are compared and copied as a whole, never field by field.</summary>
    public IReadOnlyList<string> AtomicPaths { get; init; } = [];

    /// <summary>Array path → identity of an element (null identity: compared as a whole value).</summary>
    public IReadOnlyDictionary<string, Func<JsonNode?, string?>> ArrayIdentities { get; init; } = new Dictionary<string, Func<JsonNode?, string?>>();

    /// <summary>Whether a property (by its name and full path) holds a path that may be remapped.</summary>
    public Func<string, string, bool> IsPathField { get; init; } = (_, _) => false;

    public static MergeProfile? For(string artifactId, string fileName)
    {
        var name = fileName.ToLowerInvariant();
        var tool = artifactId.Split(':')[0];
        return (tool, name) switch
        {
            ("vscode" or "vscode-insiders" or "cursor", "settings.json") => EditorSettings,
            ("vscode" or "vscode-insiders" or "cursor", "keybindings.json") => Keybindings,
            (_, "mcp.json" or "mcp-config.json" or "claude_desktop_config.json") => McpFile,
            ("claude-code", ".claude.json") => new MergeProfile { ScopeKeys = ["mcpServers"], AtomicPaths = ["mcpServers.*"] },
            ("claude-code", "settings.json" or "settings.local.json") => ClaudeSettings,
            ("gemini-cli", "settings.json") => McpFile,
            ("copilot", "config.json") => new MergeProfile(),
            ("windows-terminal", "settings.json") => TerminalSettings,
            _ => null,
        };
    }

    private static readonly HashSet<string> PathSuffixes = new(StringComparer.OrdinalIgnoreCase) { "path", "dir", "directory", "cwd", "folder", "home" };

    private static bool LooksLikePathKey(string name)
        => PathSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static readonly MergeProfile EditorSettings = new() { IsPathField = (name, _) => LooksLikePathKey(name) };

    private static readonly MergeProfile Keybindings = new()
    {
        ArrayIdentities = new Dictionary<string, Func<JsonNode?, string?>>
        {
            [""] = node => node is JsonObject o ? $"{o["key"]}|{o["command"]}|{o["when"]}" : null,
        },
    };

    private static readonly MergeProfile McpFile = new()
    {
        AtomicPaths = ["mcpServers.*", "servers.*", "inputs"],
        IsPathField = (name, path) => name.Equals("cwd", StringComparison.OrdinalIgnoreCase),
    };

    private static readonly MergeProfile ClaudeSettings = new()
    {
        AtomicPaths = ["hooks", "env"],
        ArrayIdentities = new Dictionary<string, Func<JsonNode?, string?>>
        {
            ["permissions.allow"] = node => node?.ToJsonString(),
            ["permissions.deny"] = node => node?.ToJsonString(),
            ["permissions.ask"] = node => node?.ToJsonString(),
            ["permissions.additionalDirectories"] = node => node?.ToJsonString(),
        },
        IsPathField = (_, path) => path.StartsWith("permissions.additionalDirectories", StringComparison.Ordinal),
    };

    private static readonly MergeProfile TerminalSettings = new()
    {
        ArrayIdentities = new Dictionary<string, Func<JsonNode?, string?>>
        {
            ["profiles.list"] = node => node is JsonObject o ? (string?)o["guid"] ?? (string?)o["name"] : null,
            ["schemes"] = node => node is JsonObject o ? (string?)o["name"] : null,
            ["themes"] = node => node is JsonObject o ? (string?)o["name"] : null,
            ["actions"] = node => node?.ToJsonString(),
        },
        IsPathField = (name, _) => name is "startingDirectory" or "backgroundImage" or "icon",
    };
}

/// <summary>
/// Merges a backed-up configuration into the target's at named settings, servers or profiles: identical
/// values are left alone, missing ones are added, and conflicting ones keep the target's value unless the
/// user chooses the backup's. Enterprise-managed settings are never touched (they live elsewhere).
/// </summary>
public static class JsonMerger
{
    public static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    public static JsonNode? Parse(byte[] bytes)
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

    public static (JsonNode Merged, IReadOnlyList<MergeChange> Changes) Merge(JsonNode? target, JsonNode backup, MergeProfile profile, bool preferBackup)
    {
        var changes = new List<MergeChange>();
        var merged = target?.DeepClone() ?? (backup is JsonArray ? new JsonArray() : new JsonObject());

        if (merged is JsonObject targetObject && backup is JsonObject backupObject)
        {
            var keys = profile.ScopeKeys is null ? backupObject.Select(p => p.Key).ToList() : [.. profile.ScopeKeys.Where(backupObject.ContainsKey)];
            foreach (var key in keys)
            {
                MergeProperty(targetObject, key, backupObject[key], key, profile, preferBackup, changes);
            }
        }
        else if (merged is JsonArray targetArray && backup is JsonArray backupArray && profile.ArrayIdentities.TryGetValue("", out var identity))
        {
            MergeArray(targetArray, backupArray, identity, string.Empty, profile, preferBackup, changes);
        }
        else if (!JsonNode.DeepEquals(merged, backup))
        {
            changes.Add(new MergeChange("(whole file)", preferBackup ? MergeChangeKind.ConflictUsedBackup : MergeChangeKind.ConflictKeptTarget, Describe(backup), Describe(merged)));
            if (preferBackup)
            {
                merged = backup.DeepClone();
            }
        }

        return (merged, changes);
    }

    private static void MergeProperty(JsonObject target, string key, JsonNode? backupValue, string path, MergeProfile profile, bool preferBackup, List<MergeChange> changes)
    {
        if (!target.ContainsKey(key))
        {
            target[key] = backupValue?.DeepClone();
            changes.Add(new MergeChange(path, MergeChangeKind.Added, Describe(backupValue, key), null));
            return;
        }

        var targetValue = target[key];
        if (JsonNode.DeepEquals(targetValue, backupValue))
        {
            changes.Add(new MergeChange(path, MergeChangeKind.Unchanged, Describe(backupValue, key), Describe(targetValue, key)));
            return;
        }

        var atomic = IsAtomic(path, profile);
        if (!atomic && targetValue is JsonObject targetChild && backupValue is JsonObject backupChild)
        {
            foreach (var (childKey, childValue) in backupChild.ToList())
            {
                MergeProperty(targetChild, childKey, childValue, $"{path}.{childKey}", profile, preferBackup, changes);
            }

            return;
        }

        if (!atomic && targetValue is JsonArray targetArray && backupValue is JsonArray backupArray && profile.ArrayIdentities.TryGetValue(path, out var identity))
        {
            MergeArray(targetArray, backupArray, identity, path, profile, preferBackup, changes);
            return;
        }

        changes.Add(new MergeChange(path, preferBackup ? MergeChangeKind.ConflictUsedBackup : MergeChangeKind.ConflictKeptTarget, Describe(backupValue, key), Describe(targetValue, key)));
        if (preferBackup)
        {
            target[key] = backupValue?.DeepClone();
        }
    }

    private static void MergeArray(JsonArray target, JsonArray backup, Func<JsonNode?, string?> identity, string path, MergeProfile profile, bool preferBackup, List<MergeChange> changes)
    {
        var existing = target.Select((node, index) => (Id: identity(node), Index: index)).Where(x => x.Id is not null).ToDictionary(x => x.Id!, x => x.Index, StringComparer.Ordinal);
        foreach (var element in backup)
        {
            var id = identity(element);
            var label = $"{path}[{Short(id)}]";
            if (id is null || !existing.TryGetValue(id, out var index))
            {
                target.Add(element?.DeepClone());
                changes.Add(new MergeChange(label, MergeChangeKind.Added, Describe(element), null));
                continue;
            }

            if (JsonNode.DeepEquals(target[index], element))
            {
                changes.Add(new MergeChange(label, MergeChangeKind.Unchanged, Describe(element), Describe(target[index])));
            }
            else
            {
                changes.Add(new MergeChange(label, preferBackup ? MergeChangeKind.ConflictUsedBackup : MergeChangeKind.ConflictKeptTarget, Describe(element), Describe(target[index])));
                if (preferBackup)
                {
                    target[index] = element?.DeepClone();
                }
            }
        }
    }

    /// <summary>Rewrites declared path fields of a backed-up document in place; returns what changed.</summary>
    public static IReadOnlyList<(string Path, string Before, string After, RewriteOutcome Outcome)> RewritePaths(JsonNode? node, MergeProfile profile, PathRewriter rewriter, string path = "")
    {
        var results = new List<(string, string, string, RewriteOutcome)>();
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    var childPath = path.Length == 0 ? key : $"{path}.{key}";
                    if (value is JsonValue v && v.TryGetValue<string>(out var text) && profile.IsPathField(key, childPath))
                    {
                        var (after, outcome) = rewriter.Rewrite(text);
                        if (outcome != RewriteOutcome.Unchanged)
                        {
                            results.Add((childPath, text, after, outcome));
                            obj[key] = after;
                        }
                    }
                    else
                    {
                        results.AddRange(RewritePaths(value, profile, rewriter, childPath));
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var text) && profile.IsPathField(path, path))
                    {
                        var (after, outcome) = rewriter.Rewrite(text);
                        if (outcome != RewriteOutcome.Unchanged)
                        {
                            results.Add(($"{path}[{i}]", text, after, outcome));
                            array[i] = after;
                        }
                    }
                    else
                    {
                        results.AddRange(RewritePaths(array[i], profile, rewriter, $"{path}[{i}]"));
                    }
                }

                break;
        }

        return results;
    }

    /// <summary>Absolute source paths in values that are not declared path fields: reported, never rewritten.</summary>
    public static IEnumerable<(string Path, string Value)> UndeclaredPaths(JsonNode? node, string sourcePrefix, string path = "")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    foreach (var hit in UndeclaredPaths(value, sourcePrefix, path.Length == 0 ? key : $"{path}.{key}"))
                    {
                        yield return hit;
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var hit in UndeclaredPaths(array[i], sourcePrefix, $"{path}[{i}]"))
                    {
                        yield return hit;
                    }
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text)
                                      && text.Replace('/', '\\').Contains(sourcePrefix.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase):
                yield return (path, text);
                break;
        }
    }

    private static bool IsAtomic(string path, MergeProfile profile)
        => profile.AtomicPaths.Any(p => p == path || (p.EndsWith(".*", StringComparison.Ordinal) && path.StartsWith(p[..^1], StringComparison.Ordinal) && !path[(p.Length - 1)..].Contains('.', StringComparison.Ordinal)));

    /// <summary>A short, display-safe rendering. Values that look like secrets are never shown.</summary>
    public static string? Describe(JsonNode? node, string? key = null)
    {
        if (node is null)
        {
            return "null";
        }

        var text = node.ToJsonString();
        if ((key is not null && SecretDetector.IsSecretName(key)) || SecretDetector.LooksLikeSecretValue(text))
        {
            return "[hidden]";
        }

        return text.Length <= 120 ? text : text[..117] + "…";
    }

    private static string Short(string? id) => id is null ? "?" : id.Length <= 40 ? id : id[..37] + "…";
}
