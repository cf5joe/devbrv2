using DevBR.Application.Machine;
using DevBR.Backup;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Restore;

/// <summary>A source path prefix and where it goes on the target.</summary>
public sealed record RootMapping(string SourcePrefix, string TargetPrefix, PathMappingOrigin Origin);

/// <summary>The source computer's known folders, recovered from the logical roots recorded in the backup.</summary>
public sealed record SourceFolders(string? UserProfile, string? RoamingAppData, string? LocalAppData, string? ProgramData)
{
    public static SourceFolders From(IEnumerable<ArtifactIndexRecord> artifacts)
    {
        string? profile = null, roaming = null, local = null, programData = null;
        foreach (var root in artifacts.SelectMany(a => a.Roots))
        {
            var basePath = Strip(root.SourcePath, root.Logical.RelativePath);
            if (basePath is null)
            {
                continue;
            }

            switch (root.Logical.Root)
            {
                case LogicalRootKind.UserProfile: profile ??= basePath; break;
                case LogicalRootKind.RoamingAppData: roaming ??= basePath; break;
                case LogicalRootKind.LocalAppData: local ??= basePath; break;
                case LogicalRootKind.ProgramData: programData ??= basePath; break;
            }
        }

        // AppData nests inside the profile; fill gaps from whichever is known.
        profile ??= roaming is not null ? Paths.Parent(Paths.Parent(roaming)!) : local is not null ? Paths.Parent(Paths.Parent(local)!) : null;
        roaming ??= profile is null ? null : Paths.Combine(profile, "AppData", "Roaming");
        local ??= profile is null ? null : Paths.Combine(profile, "AppData", "Local");
        return new SourceFolders(profile, roaming, local, programData ?? @"C:\ProgramData");
    }

    private static string? Strip(string sourcePath, string relative)
    {
        var path = Paths.Normalize(sourcePath);
        if (relative.Length == 0)
        {
            return path;
        }

        var suffix = "\\" + relative.Trim('\\');
        return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path[..^suffix.Length] : null;
    }
}

/// <summary>
/// Maps source paths to target paths by the longest matching root, at path-segment boundaries only:
/// mapping C:\Projects to D:\Projects never touches C:\ProjectsOld or anything else on C:.
/// </summary>
public sealed class PathMapper
{
    private readonly List<RootMapping> _mappings;

    public PathMapper(IEnumerable<RootMapping> mappings)
    {
        _mappings = [.. mappings
            .Select(m => m with { SourcePrefix = Paths.Normalize(m.SourcePrefix), TargetPrefix = Paths.Normalize(m.TargetPrefix) })
            .OrderByDescending(m => m.SourcePrefix.Length)];
    }

    public IReadOnlyList<RootMapping> Mappings => _mappings;

    /// <summary>Known-folder mappings: the source user's profile and AppData to the target user's.</summary>
    public static IEnumerable<RootMapping> KnownFolders(SourceFolders source, MachineFolders target)
    {
        // Most specific first; the sort in the constructor keeps the order stable regardless.
        if (source.RoamingAppData is { } roaming) { yield return new(roaming, target.RoamingAppData, PathMappingOrigin.KnownFolder); }
        if (source.LocalAppData is { } local) { yield return new(local, target.LocalAppData, PathMappingOrigin.KnownFolder); }
        if (source.UserProfile is { } profile) { yield return new(profile, target.UserProfile, PathMappingOrigin.KnownFolder); }
        if (source.ProgramData is { } programData) { yield return new(programData, target.ProgramData, PathMappingOrigin.KnownFolder); }
    }

    /// <returns>The mapped path, or null when no root covers it (an unresolved absolute path).</returns>
    public string? Map(string sourcePath)
    {
        var path = Paths.Normalize(sourcePath);
        var mapping = _mappings.FirstOrDefault(m => Paths.IsUnder(path, m.SourcePrefix));
        if (mapping is null)
        {
            return null;
        }

        var relative = Paths.Relative(mapping.SourcePrefix, path);
        return relative.Length == 0 ? mapping.TargetPrefix : Paths.Combine(mapping.TargetPrefix, relative);
    }

    public RootMapping? MappingFor(string sourcePath)
        => _mappings.FirstOrDefault(m => Paths.IsUnder(Paths.Normalize(sourcePath), m.SourcePrefix));
}

public enum RewriteOutcome
{
    Unchanged,
    Rewritten,
    Unresolved,
}

public sealed record PathRewrite(string ArtifactId, string Field, string Before, string After, RewriteOutcome Outcome);

/// <summary>
/// Rewrites only values the adapter declares as paths. Environment-variable expressions are preserved,
/// and an absolute path with no mapping is reported, never guessed.
/// </summary>
public sealed class PathRewriter(PathMapper mapper)
{
    public (string Value, RewriteOutcome Outcome) Rewrite(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('%', StringComparison.Ordinal) || value.StartsWith('~') || value.StartsWith('$'))
        {
            return (value, RewriteOutcome.Unchanged);
        }

        var forwardSlashes = value.Contains('/', StringComparison.Ordinal) && !value.Contains('\\', StringComparison.Ordinal);
        var candidate = value.Replace('/', '\\');
        if (candidate.Length < 3 || candidate[1] != ':' || candidate[2] != '\\' || !char.IsAsciiLetter(candidate[0]))
        {
            return (value, RewriteOutcome.Unchanged);
        }

        var mapped = mapper.Map(candidate);
        if (mapped is null)
        {
            return (value, RewriteOutcome.Unresolved);
        }

        if (forwardSlashes)
        {
            mapped = mapped.Replace('\\', '/');
        }

        return string.Equals(mapped, value, StringComparison.Ordinal) ? (value, RewriteOutcome.Unchanged) : (mapped, RewriteOutcome.Rewritten);
    }

    /// <summary>A PATH-style list: each entry rewritten independently, separators and order kept.</summary>
    public (string Value, IReadOnlyList<string> Unresolved) RewriteList(string value)
    {
        var unresolved = new List<string>();
        var parts = value.Split(';').Select(part =>
        {
            var (rewritten, outcome) = Rewrite(part);
            if (outcome == RewriteOutcome.Unresolved)
            {
                unresolved.Add(part);
            }

            return rewritten;
        });
        return (string.Join(';', parts), unresolved);
    }
}
