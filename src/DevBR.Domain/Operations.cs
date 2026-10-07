namespace DevBR.Domain;

public enum OperationStage
{
    Discovery,
    Planning,
    Staging,
    Compression,
    Verification,
    Inspection,
    Extraction,
    Preflight,
    Installation,
    Restore,
    Validation,
    Rollback,
}

public enum EventSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>How much a displayed ETA can be trusted. <see cref="None"/> means no ETA is shown.</summary>
public enum EtaConfidence
{
    None,
    Low,
    High,
}

/// <param name="FilesTotal">Null while totals are unknown; the UI then shows indeterminate progress.</param>
public sealed record OperationEvent(
    Guid JobId,
    OperationStage Stage,
    string? ItemId,
    long FilesProcessed,
    long? FilesTotal,
    long BytesProcessed,
    long? BytesTotal,
    string Message,
    EventSeverity Severity,
    DateTimeOffset Timestamp,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    EtaConfidence EtaConfidence);
