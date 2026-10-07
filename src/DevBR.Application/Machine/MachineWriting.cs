using System.Security.Cryptography;
using System.Text;

namespace DevBR.Application.Machine;

/// <summary>
/// Changes to a target computer. Used only by the restore executor (after approval) and by rollback;
/// discovery and preflight never receive one.
/// </summary>
public interface IMachineWriter
{
    /// <summary>Writes through a temporary file in the same folder and then replaces the destination in one step.</summary>
    void WriteFileAtomic(string path, byte[] content);

    void WriteFileAtomic(string path, Stream content);

    void CreateDirectory(string path);

    /// <summary>Removes a file DevBR itself created (rollback only).</summary>
    void DeleteFile(string path);

    /// <summary>Removes a folder DevBR itself created, with everything in it (rollback only).</summary>
    void DeleteDirectoryTree(string path);

    /// <summary>User-scope variables only; machine scope goes through <see cref="IElevatedSession"/>. A null value removes the variable.</summary>
    void SetUserEnvironmentVariable(string name, string? value, RegistryValueKind kind);

    /// <summary>Tells running programs that environment variables changed (WM_SETTINGCHANGE "Environment").</summary>
    void BroadcastEnvironmentChange();
}

/// <param name="ExpectedCurrentValue">The value seen just before committing; the change is refused if the registry differs (a concurrent edit).</param>
public sealed record EnvironmentChange(string Name, string? ExpectedCurrentValue, string? NewValue, bool Expandable);

/// <summary>Obtains administrator rights for specific approved changes, or reports that the user declined.</summary>
public interface IElevationProvider
{
    /// <returns>A session, or null when elevation was declined or is unavailable.</returns>
    Task<IElevatedSession?> RequestAsync(CancellationToken cancellationToken);
}

public interface IElevatedSession : IAsyncDisposable
{
    /// <summary>Applies machine environment changes; the elevated side re-validates them against the approved plan.</summary>
    Task ApplyMachineEnvironmentAsync(Guid jobId, string approvalHash, IReadOnlyList<EnvironmentChange> changes, CancellationToken cancellationToken);
}

/// <summary>
/// The canonical description of an approved effect. Produced when a plan is approved and checked again by
/// whoever performs a privileged change, so nothing outside the approved set can be applied.
/// </summary>
public static class RestoreEffects
{
    public static string Key(string action, string target, string decision, string privilege, string? recipe, string? sha256, string? expectedState)
        => string.Join('|', action, target.ToUpperInvariant(), decision, privilege, recipe ?? string.Empty, sha256 ?? string.Empty, expectedState ?? string.Empty);

    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>The approval hash of a set of effects (order-independent).</summary>
    public static string Hash(IEnumerable<string> effects) => Sha256(string.Join('\n', effects.Order(StringComparer.Ordinal)));

    /// <summary>The value of a PATH-style variable after appending one entry.</summary>
    public static string AppendPathEntry(string? current, string entry)
        => string.IsNullOrEmpty(current?.TrimEnd(';')) ? entry : current.TrimEnd(';') + ";" + entry;

    /// <summary>The value with the last exact occurrence of an entry removed, or null when it is not present.</summary>
    public static string? RemovePathEntry(string? current, string entry)
    {
        if (current is null)
        {
            return null;
        }

        var parts = current.TrimEnd(';').Split(';').ToList();
        var index = parts.FindLastIndex(p => string.Equals(p, entry, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        parts.RemoveAt(index);
        return string.Join(';', parts);
    }

    /// <summary>
    /// Whether a machine environment change is covered by an approved effect: setting a variable to the
    /// exact approved value, appending exactly one approved entry to the machine PATH, or undoing one of
    /// those (back to the state recorded when the plan was approved).
    /// </summary>
    public static bool Permits(IReadOnlySet<string> approvedEffects, EnvironmentChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Name))
        {
            return false;
        }

        var effects = approvedEffects.Select(e => e.Split('|')).Where(p => p.Length == 7 && p[3] == "Elevated").ToList();

        if (change.Name.Equals("Path", StringComparison.OrdinalIgnoreCase))
        {
            if (change.ExpectedCurrentValue is null || change.NewValue is null)
            {
                return false;
            }

            return effects.Where(p => p[0] == "AppendPathEntry" && p[1] == "PATH (MACHINE)" && p[6].Length > 0).Any(p =>
                string.Equals(change.NewValue, AppendPathEntry(change.ExpectedCurrentValue, p[6]), StringComparison.Ordinal)
                || string.Equals(change.NewValue, RemovePathEntry(change.ExpectedCurrentValue, p[6]), StringComparison.Ordinal));
        }

        var target = $"ENV:MACHINE:{change.Name.ToUpperInvariant()}";
        foreach (var p in effects.Where(p => p[0] == "SetEnvironmentVariable" && p[1] == target && p[5].Length > 0))
        {
            // Apply: exactly the approved value.
            if (change.NewValue is not null && Sha256(change.NewValue) == p[5])
            {
                return true;
            }

            // Undo: the variable still holds the approved value and goes back to the recorded previous state.
            if (change.ExpectedCurrentValue is not null && Sha256(change.ExpectedCurrentValue) == p[5])
            {
                if (p[6] == "absent" && change.NewValue is null)
                {
                    return true;
                }

                if (p[6].StartsWith("value:", StringComparison.Ordinal) && change.NewValue is not null && Sha256(change.NewValue)[..16] == p[6]["value:".Length..])
                {
                    return true;
                }
            }
        }

        return false;
    }
}
