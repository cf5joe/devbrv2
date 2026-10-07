using System.Security.Cryptography;
using System.Text;
using DevBR.Application.Machine;
using DevBR.Domain;

namespace DevBR.Discovery.Support;

public static class Paths
{
    public static string Normalize(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            return normalized + "\\";
        }

        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }

    public static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it, on a path-segment boundary.</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = r.EndsWith('\\') ? r : r + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string? Parent(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('\\');
        if (index < 0 || normalized.Length <= 3)
        {
            return null;
        }

        return index == 2 ? normalized[..3] : normalized[..index];
    }

    public static string Combine(string root, params string[] parts)
    {
        var result = Normalize(root);
        foreach (var part in parts)
        {
            result = result.EndsWith('\\') ? result + part.Trim('\\') : result + "\\" + part.Trim('\\');
        }

        return result;
    }

    public static string? Root(string path) => path.Length >= 3 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + @":\" : null;

    /// <summary>
    /// Expresses an absolute path relative to the most specific logical root, so it can be remapped on
    /// another machine. Roaming/Local AppData are checked before the profile because they nest inside it.
    /// </summary>
    public static LogicalPath ToLogical(MachineFolders folders, string absolute, string? customRootKey = null)
    {
        (LogicalRootKind Kind, string Root)[] roots =
        [
            (LogicalRootKind.RoamingAppData, folders.RoamingAppData),
            (LogicalRootKind.LocalAppData, folders.LocalAppData),
            (LogicalRootKind.UserProfile, folders.UserProfile),
            (LogicalRootKind.ProgramData, folders.ProgramData),
        ];

        foreach (var (kind, root) in roots)
        {
            if (IsUnder(absolute, root))
            {
                return new LogicalPath(kind, null, Relative(root, absolute));
            }
        }

        var key = customRootKey ?? Normalize(absolute);
        return new LogicalPath(LogicalRootKind.CustomRoot, key, IsUnder(absolute, key) ? Relative(key, absolute) : string.Empty);
    }

    public static string Relative(string root, string path)
    {
        var r = Normalize(root);
        var p = Normalize(path);
        if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return p[(r.EndsWith('\\') ? r.Length : r.Length + 1)..];
    }
}

public static class ItemIds
{
    /// <summary>Stable, readable identifier: category, slug, and a short hash of the distinguishing key.</summary>
    public static string For(string category, string name, params string?[] keys)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        var material = string.Join('|', keys.Select(k => (k ?? string.Empty).ToUpperInvariant()));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{category}|{name}|{material}")))[..10];
        return $"{category.ToLowerInvariant()}:{slug}:{hash}";
    }
}
