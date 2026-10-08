using System.Globalization;

namespace DevBR.Discovery.Adapters;

/// <summary>What an adapter can do with a tool's configuration.</summary>
[Flags]
public enum AdapterCapabilities
{
    None = 0,

    /// <summary>Records installed items (extensions, modules, distributions) for reinstall guidance.</summary>
    Inventory = 1,

    /// <summary>Captures configuration files for whole-file restore (keep, replace or restore alongside).</summary>
    Capture = 2,

    /// <summary>Merges settings, servers or profiles by identity instead of replacing whole files.</summary>
    StructuredMerge = 4,

    /// <summary>Rewrites declared path fields for the new computer.</summary>
    PathRewrite = 8,

    /// <summary>Plans validated installs (extensions, MCP server runtimes) the restored configuration needs.</summary>
    DependencyRecipes = 16,
}

public enum VersionSupport
{
    Supported,
    Unsupported,
    Unknown,
}

/// <summary>A half-open version interval: <see cref="Minimum"/> inclusive, <see cref="Below"/> exclusive (open-ended when null).</summary>
public sealed record VersionRange(string Minimum, string? Below = null)
{
    public bool Contains(string version)
        => AdapterVersions.TryParse(version, out var v)
           && AdapterVersions.Compare(v, AdapterVersions.Parse(Minimum)) >= 0
           && (Below is null || AdapterVersions.Compare(v, AdapterVersions.Parse(Below)) < 0);

    public override string ToString() => Below is null ? $">= {Minimum}" : $">= {Minimum}, < {Below}";
}

/// <summary>
/// An adapter's declared support: the versions its handling is verified against, where it reads, what it can
/// do, and what restore needs first. Structured merges and path rewrites are only used for verified host
/// versions; anything else falls back to whole-file handling or inventory.
/// </summary>
/// <param name="HostToolId">The known tool whose installed version governs semantic handling ("windows" for the OS build).</param>
/// <param name="HostName">How the host and its version are described to the user.</param>
/// <param name="VerifiedVersions">Versions with verified handling (and covered by test fixtures).</param>
/// <param name="Locations">Locations read, in precedence order (environment overrides first).</param>
/// <param name="Capabilities">What restore can do within <paramref name="VerifiedVersions"/>.</param>
/// <param name="Prerequisites">Conditions preflight checks or asks the user to satisfy.</param>
public sealed record AdapterSupport(
    string HostToolId,
    string HostName,
    IReadOnlyList<VersionRange> VerifiedVersions,
    IReadOnlyList<string> Locations,
    AdapterCapabilities Capabilities,
    IReadOnlyList<string> Prerequisites)
{
    /// <summary>Capabilities that edit content semantically and therefore need a verified version.</summary>
    public const AdapterCapabilities SemanticCapabilities = AdapterCapabilities.StructuredMerge | AdapterCapabilities.PathRewrite;

    public bool HasSemanticHandling => (Capabilities & SemanticCapabilities) != 0;

    /// <summary>What restore does for an unknown or unverified version.</summary>
    public string Fallback => HostToolId == "windows"
        ? "Inventory only (no variables or PATH entries are changed)"
        : HasSemanticHandling ? "Whole-file restore (keep, replace or restore alongside); no merge or path rewrite" : "Same as supported (whole-file restore)";

    public VersionSupport Evaluate(string? version)
        => string.IsNullOrWhiteSpace(version) || !AdapterVersions.TryParse(version, out _) ? VersionSupport.Unknown
            : VerifiedVersions.Any(r => r.Contains(version)) ? VersionSupport.Supported
            : VersionSupport.Unsupported;

    public string VersionText => string.Join("; ", VerifiedVersions);
}

/// <summary>Prerequisite rules shared by several adapters, as preflight states them.</summary>
public static class AdapterPrerequisites
{
    public const string HostInstalled = "Host installed on the new computer; otherwise its items are blocked with install guidance until Recheck";
    public const string HostClosed = "Host closed during restore; preflight warns while it is running";
    public const string SignInAgain = "Sign in again after restore; credentials are excluded unless explicitly included (encrypted)";
    public const string McpRuntimes = "Runtimes used by MCP servers (Node.js, uv, Python, Docker) installed or approved for install";
}

public static class AdapterVersions
{
    /// <summary>Numeric version parts ("v1.107.0-insider" → 1, 107, 0). False when the version has no leading number.</summary>
    public static bool TryParse(string version, out long[] parts)
    {
        var core = version.Trim().TrimStart('v', 'V').Split(['+', ' ', '-'], 2)[0];
        var result = new List<long>();
        foreach (var part in core.Split('.'))
        {
            var digits = new string([.. part.TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length == 0 || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                break;
            }

            result.Add(n);
        }

        parts = [.. result];
        return parts.Length > 0;
    }

    public static long[] Parse(string version)
        => TryParse(version, out var parts) ? parts : throw new FormatException($"'{version}' is not a version.");

    public static int Compare(long[] a, long[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }
}
