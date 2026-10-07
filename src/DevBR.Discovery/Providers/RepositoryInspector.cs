using System.Globalization;
using DevBR.Application.Machine;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Providers;

/// <summary>
/// Reads a Git repository's metadata files directly (no git process): branch, remotes with credentials
/// redacted, linked worktrees, submodules, external git directories, object alternates, LFS, stashes
/// and active locks.
/// </summary>
public static class RepositoryInspector
{
    public const string AdapterId = "git-repository";

    public static InventoryItem? Inspect(IMachine machine, string path, bool bare)
    {
        var fs = machine.FileSystem;
        path = Paths.Normalize(path);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        string gitDir;
        string kind;

        if (bare)
        {
            gitDir = path;
            kind = "bare";
        }
        else
        {
            var dotGit = Paths.Combine(path, ".git");
            if (fs.DirectoryExists(dotGit))
            {
                gitDir = dotGit;
                kind = "standard";
            }
            else if (fs.ReadText(dotGit, 4096) is { } pointer && pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                gitDir = Resolve(path, pointer["gitdir:".Length..].Trim());
                kind = gitDir.Contains(@"\worktrees\", StringComparison.OrdinalIgnoreCase) ? "linked worktree"
                    : gitDir.Contains(@"\modules\", StringComparison.OrdinalIgnoreCase) ? "submodule"
                    : "external git directory";
                properties["gitDir"] = gitDir;
                if (!fs.DirectoryExists(gitDir))
                {
                    properties["gitDirMissing"] = "true";
                }
            }
            else
            {
                return null;
            }
        }

        properties["kind"] = kind;

        // Linked worktrees keep shared data (config, objects, refs) in the common directory.
        var commonDir = gitDir;
        if (fs.ReadText(Paths.Combine(gitDir, "commondir"), 4096) is { } common)
        {
            commonDir = Resolve(gitDir, common.Trim());
        }

        var head = fs.ReadText(Paths.Combine(gitDir, "HEAD"), 4096)?.Trim();
        if (head is not null)
        {
            properties["head"] = head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
                ? head["ref: refs/heads/".Length..]
                : $"detached at {head[..Math.Min(12, head.Length)]}";
        }

        var config = GitConfig.Parse(fs.ReadText(Paths.Combine(commonDir, "config"), ConfigReaders.MaxConfigBytes));
        if (string.Equals(config.Get("core", null, "bare"), "true", StringComparison.OrdinalIgnoreCase))
        {
            properties["kind"] = kind = "bare";
        }

        var remotes = new List<string>();
        foreach (var remote in config.Subsections("remote"))
        {
            if (config.Get("remote", remote, "url") is { } url)
            {
                var redacted = SecretDetector.RedactUrl(url);
                if (redacted != url)
                {
                    properties["credentialsInRemoteUrl"] = "true";
                }

                remotes.Add($"{remote} {redacted}");
            }
        }

        if (remotes.Count > 0)
        {
            properties["remotes"] = string.Join('\n', remotes);
        }

        if (fs.ReadText(Paths.Combine(commonDir, "objects", "info", "alternates"), 64 * 1024) is { } alternates)
        {
            properties["objectAlternates"] = string.Join('\n', alternates.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var attributes = bare ? null : fs.ReadText(Paths.Combine(path, ".gitattributes"), 1024 * 1024);
        if (fs.DirectoryExists(Paths.Combine(commonDir, "lfs")) || (attributes?.Contains("filter=lfs", StringComparison.Ordinal) ?? false))
        {
            properties["lfs"] = "true";
        }

        if (fs.FileExists(Paths.Combine(gitDir, "index.lock")) || fs.FileExists(Paths.Combine(gitDir, "HEAD.lock")) || fs.FileExists(Paths.Combine(commonDir, "packed-refs.lock")))
        {
            properties["locked"] = "true";
        }

        if (fs.FileExists(Paths.Combine(commonDir, "refs", "stash")) || fs.FileExists(Paths.Combine(commonDir, "logs", "refs", "stash")))
        {
            properties["stash"] = "true";
        }

        if (commonDir == gitDir && fs.DirectoryExists(Paths.Combine(gitDir, "worktrees")))
        {
            try
            {
                var count = fs.EnumerateEntries(Paths.Combine(gitDir, "worktrees")).Count(e => e.IsDirectory);
                if (count > 0)
                {
                    properties["linkedWorktrees"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        if (!bare && fs.ReadText(Paths.Combine(path, ".gitmodules"), 1024 * 1024) is { } modules)
        {
            var count = modules.Split('\n').Count(l => l.TrimStart().StartsWith("[submodule", StringComparison.Ordinal));
            if (count > 0)
            {
                properties["submodules"] = count.ToString(CultureInfo.InvariantCulture);
            }
        }

        var name = bare ? Path.GetFileName(path) : Path.GetFileName(path.TrimEnd('\\'));
        var locations = properties.ContainsKey("gitDir") ? new[] { path, gitDir } : [path];
        return new InventoryItem(
            ItemIds.For("Repository", name, path),
            InventoryCategory.Repository,
            string.IsNullOrEmpty(name) ? path : name,
            null,
            null,
            InstallScope.Unknown,
            locations,
            [new DiscoveryEvidence("filesystem", bare ? "Bare repository layout (HEAD, config, objects, refs)" : kind == "standard" ? ".git directory" : $".git file pointing to {gitDir}", path)],
            head is null ? Confidence.Medium : Confidence.Confirmed,
            DetectionStatus.Detected,
            AdapterId,
            "git",
            properties);
    }

    private static string Resolve(string baseDirectory, string relativeOrAbsolute)
    {
        var candidate = relativeOrAbsolute.Replace('/', '\\');
        var combined = candidate.Length >= 2 && candidate[1] == ':' ? candidate : Paths.Combine(baseDirectory, candidate);
        return Paths.Normalize(Path.GetFullPath(combined));
    }
}
