using CommunityToolkit.Mvvm.ComponentModel;
using DevBR.App.Controls;
using DevBR.App.Services;
using DevBR.Backup;
using DevBR.Domain;
using DevBR.Restore;

namespace DevBR.App.ViewModels;

public sealed partial class RestoreItemRow(ArtifactSummary summary, Action changed) : ObservableObject
{
    public ArtifactSummary Summary { get; } = summary;

    public string Key => Summary.Record.Key;

    public string Name => Summary.Record.Artifact.DisplayName;

    public string Owner => DiscoveryText.Owner(Summary.Record.Artifact.OwnerToolId);

    public bool CanSelect => Summary.Record.Status != "Blocked";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = summary.Record.Status != "Blocked";

    partial void OnIsSelectedChanged(bool value) => changed();
}

public sealed record MappingRow(string Source, string Target, string Origin, bool IsValid, bool CanChange)
{
    public string StatusLabel => IsValid ? "Ready" : "Needs a destination";
}

public sealed record FindingView(InfoSeverity Severity, string Title, string Message, string Label)
{
    public static FindingView From(PreflightFinding finding)
    {
        var steps = finding.NextSteps.Count == 0 ? string.Empty : "Next: " + string.Join(" ", finding.NextSteps);
        var (severity, label) = finding.Severity switch
        {
            FindingSeverity.Blocking => (InfoSeverity.Error, "Blocking"),
            FindingSeverity.Warning => (InfoSeverity.Warning, "Warning"),
            _ => (InfoSeverity.Information, "Note"),
        };
        return new FindingView(severity, $"{label}: {finding.Problem}", $"{finding.WhyItMatters} {steps}".Trim(), label);
    }
}

/// <summary>One planned change, with its conflict choice when there is one.</summary>
public sealed partial class OperationRow : ObservableObject
{
    private readonly Action<OperationRow> _decisionChanged;
    private bool _loading = true;

    public OperationRow(PlannedOperation operation, Action<OperationRow> decisionChanged)
    {
        Operation = operation;
        _decisionChanged = decisionChanged;
        Decision = operation.Operation.ConflictDecision == ConflictDecision.NoConflict ? null : operation.Operation.ConflictDecision;
        _loading = false;
    }

    public PlannedOperation Operation { get; }

    public string Id => Operation.Operation.Id;

    public string Title => Operation.Title;

    public string? Detail => Operation.Detail;

    public bool Enabled => Operation.Enabled;

    public bool HasChoice => Operation.AllowedDecisions.Count > 0;

    public IReadOnlyList<ConflictDecision> Choices => Operation.AllowedDecisions;

    public bool NeedsElevation => Operation.Operation.Privilege == PrivilegeRequirement.Elevated && Operation.Operation.Action != RestoreAction.Skip;

    public bool IsIrreversible => Operation.Operation.Reversibility == Reversibility.Irreversible && Operation.Operation.Action == RestoreAction.InstallDependency;

    public string? MergeSummary => Operation.Merge is null ? null
        : string.Join("\n", Operation.Merge.Changes.Where(c => c.Kind != MergeChangeKind.Unchanged).Take(12).Select(c => c.Kind switch
        {
            MergeChangeKind.Added => $"+ {c.Path} = {c.BackupValue}",
            MergeChangeKind.ConflictKeptTarget => $"= {c.Path}: keeps {c.TargetValue} (backup: {c.BackupValue})",
            MergeChangeKind.ConflictUsedBackup => $"~ {c.Path}: {c.TargetValue} → {c.BackupValue}",
            _ => c.Path,
        }));

    [ObservableProperty]
    public partial ConflictDecision? Decision { get; set; }

    partial void OnDecisionChanged(ConflictDecision? value)
    {
        if (!_loading && value is not null)
        {
            _decisionChanged(this);
        }
    }
}

public sealed record OperationGroup(string Title, string Description, IReadOnlyList<OperationRow> Rows);

public static class DecisionLabels
{
    public static System.Windows.Data.IValueConverter Label { get; } = new Converter();

    private sealed class Converter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value switch
        {
            ConflictDecision.Merge => "Merge (keep my values)",
            ConflictDecision.KeepExisting => "Keep this computer's file",
            ConflictDecision.UseBackup => "Use the backup's version",
            ConflictDecision.RestoreAlongside => "Restore next to it",
            _ => string.Empty,
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}

public static class RestoreLabels
{
    public static System.Windows.Data.IValueConverter PreflightLabel { get; } = new Converter();

    private sealed class Converter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is true ? "Run preflight again" : "Run preflight";

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
