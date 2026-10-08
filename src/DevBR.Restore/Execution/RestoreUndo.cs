using System.Text;
using System.Text.Json;
using DevBR.Application.Machine;
using DevBR.Application.Restore;

namespace DevBR.Restore.Execution;

public sealed record UndoOutcome(bool Succeeded, string Message);

/// <summary>
/// Reverses one journaled change, but only while the target still holds exactly what the restore wrote.
/// Anything changed since then is left alone and reported, so rollback never destroys newer work.
/// </summary>
internal sealed class RestoreUndo(IRestoreJournal journal, IRollbackStore store, IMachine target, IMachineWriter writer, Func<Task<IElevatedSession?>>? elevate)
{
    private IElevatedSession? _session;
    private bool _elevationRequested;

    public bool EnvironmentChanged { get; private set; }

    public (bool Ok, string Message) Undo(Guid jobId, string operationId, JournalIntent intent, ref int sequence)
    {
        if (intent.Scope == "Machine" && intent.Kind is "env" or "path")
        {
            throw new InvalidOperationException("Machine-wide changes are undone with UndoAsync.");
        }

        var next = ++sequence;
        return UndoAsync(jobId, operationId, intent, next, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<(bool Ok, string Message)> UndoAsync(Guid jobId, string operationId, JournalIntent intent, int sequence, string? approvalHash, CancellationToken cancellationToken)
    {
        var undoId = Journal.UndoPrefix + operationId;
        journal.RecordIntent(jobId, undoId, sequence, intent.ToJson());
        (bool Ok, string Message) result;
        try
        {
            result = intent.Kind switch
            {
                "file" => UndoFile(jobId, intent),
                "directory" => UndoDirectory(intent),
                "repository" => UndoRepository(jobId, intent),
                "env" => await UndoVariableAsync(jobId, intent, approvalHash, cancellationToken).ConfigureAwait(false),
                "path" => await UndoPathAsync(jobId, intent, approvalHash, cancellationToken).ConfigureAwait(false),
                _ => (false, "This change cannot be undone."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            result = (false, ex.Message);
        }

        journal.RecordOutcome(jobId, undoId, new JournalOutcome(result.Ok ? Journal.Undone : Journal.Failed, result.Message).ToJson());
        return result;
    }

    public async ValueTask DisposeSessionAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }

    private (bool, string) UndoFile(Guid jobId, JournalIntent intent)
    {
        var state = TargetProbe.FileState(target, intent.Target);
        if (state == intent.Before)
        {
            return (true, "Already as it was before the restore.");
        }

        if (state != intent.After)
        {
            return (false, $"{intent.Target} changed after the restore, so it was left as it is.");
        }

        if (intent.Before == "absent")
        {
            writer.DeleteFile(intent.Target);
            return (true, $"Removed {intent.Target}.");
        }

        if (intent.Copy is null)
        {
            return (false, "No copy of the previous file was kept.");
        }

        using (var check = store.Open(jobId, intent.Copy))
        {
            if (Journal.Sha(check) != intent.Before)
            {
                return (false, "The saved copy of the previous file is damaged.");
            }
        }

        using var copy = store.Open(jobId, intent.Copy);
        writer.WriteFileAtomic(intent.Target, copy);
        return (true, $"Put back the previous {intent.Target}.");
    }

    private (bool, string) UndoDirectory(JournalIntent intent)
    {
        if (!target.FileSystem.DirectoryExists(intent.Target))
        {
            return (true, "Already removed.");
        }

        if (!TargetProbe.DirectoryIsEmpty(target, intent.Target))
        {
            return (false, $"{intent.Target} now contains other files, so it was kept.");
        }

        writer.DeleteDirectoryTree(intent.Target);
        return (true, $"Removed folder {intent.Target}.");
    }

    private (bool, string) UndoRepository(Guid jobId, JournalIntent intent)
    {
        var destination = intent.Target;
        if (!target.FileSystem.DirectoryExists(destination))
        {
            return (true, "Already removed.");
        }

        if (intent.Copy is null)
        {
            return (false, "The list of restored files is missing.");
        }

        RepositoryManifest manifest;
        using (var copy = store.Open(jobId, intent.Copy))
        {
            manifest = JsonSerializer.Deserialize<RepositoryManifest>(copy, Journal.Json) ?? throw new InvalidDataException("The list of restored files is damaged.");
        }

        var files = manifest.Entries.Where(e => !e.Directory).ToDictionary(e => e.RelativePath, e => e.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, entry) in TargetProbe.Walk(target, destination))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (!files.TryGetValue(relative, out var expected) || TargetProbe.HashFile(target, entry.FullPath) != expected)
            {
                return (false, $"{destination} has changed since the restore ({relative}), so it was kept. Remove it yourself if you no longer need it.");
            }
        }

        writer.DeleteDirectoryTree(destination);
        if (intent.Before == "dir-empty")
        {
            writer.CreateDirectory(destination);
        }

        return (true, $"Removed {destination}.");
    }

    private async Task<(bool, string)> UndoVariableAsync(Guid jobId, JournalIntent intent, string? approvalHash, CancellationToken cancellationToken)
    {
        var current = TargetProbe.ReadVariable(target, intent.Scope, intent.Target)?.AsString();
        var state = current is null ? "absent" : Journal.Sha(current);
        if (state == intent.Before)
        {
            return (true, "Already as it was before the restore.");
        }

        if (state != intent.After)
        {
            return (false, $"{intent.Target} changed after the restore, so it was left as it is.");
        }

        string? previous = null;
        if (intent.Before != "absent")
        {
            if (intent.Copy is null)
            {
                return (false, "No copy of the previous value was kept.");
            }

            using var copy = store.Open(jobId, intent.Copy);
            using var reader = new StreamReader(copy, Encoding.UTF8);
            previous = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (Journal.Sha(previous) != intent.Before)
            {
                return (false, "The saved previous value is damaged.");
            }
        }

        var kind = intent.PreviousExpandable ? RegistryValueKind.ExpandString : RegistryValueKind.String;
        if (intent.Scope == "Machine")
        {
            var session = await SessionAsync().ConfigureAwait(false);
            if (session is null)
            {
                return (false, "Administrator approval was declined, so the machine-wide value was not changed back.");
            }

            await session.ApplyMachineEnvironmentAsync(jobId, approvalHash ?? string.Empty, [new EnvironmentChange(intent.Target, current, previous, intent.PreviousExpandable)], cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            writer.SetUserEnvironmentVariable(intent.Target, previous, kind);
        }

        EnvironmentChanged = true;
        return (true, previous is null ? $"Removed {intent.Target}." : $"Put back the previous {intent.Target}.");
    }

    private async Task<(bool, string)> UndoPathAsync(Guid jobId, JournalIntent intent, string? approvalHash, CancellationToken cancellationToken)
    {
        var entry = intent.After!;
        var current = TargetProbe.ReadVariable(target, intent.Scope, "Path");
        var value = current?.AsString();
        if (!TargetProbe.HasPathEntry(value, entry))
        {
            return (true, "The entry is no longer on PATH.");
        }

        var updated = RestoreEffects.RemovePathEntry(value, entry);
        if (updated is null)
        {
            return (false, $"The PATH entry {entry} was changed after the restore, so it was kept.");
        }

        var expandable = (current?.Kind ?? RegistryValueKind.ExpandString) == RegistryValueKind.ExpandString;
        if (intent.Scope == "Machine")
        {
            var session = await SessionAsync().ConfigureAwait(false);
            if (session is null)
            {
                return (false, "Administrator approval was declined, so the machine PATH was not changed back.");
            }

            await session.ApplyMachineEnvironmentAsync(jobId, approvalHash ?? string.Empty, [new EnvironmentChange("Path", value, updated, expandable)], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            writer.SetUserEnvironmentVariable("Path", updated.Length == 0 && intent.Before == "absent" ? null : updated,
                expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }

        EnvironmentChanged = true;
        return (true, $"Removed {entry} from PATH.");
    }

    private async Task<IElevatedSession?> SessionAsync()
    {
        if (!_elevationRequested && elevate is not null)
        {
            _elevationRequested = true;
            _session = await elevate().ConfigureAwait(false);
        }

        return _session;
    }
}
