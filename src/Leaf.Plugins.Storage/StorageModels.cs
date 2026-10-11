using System.Text.Json.Serialization;

namespace Leaf.Plugins.Storage;

public sealed class StorageNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string LogicalPath { get; init; }
    public required string Kind { get; init; }
    public required string Origin { get; init; }
    public required string Purpose { get; init; }
    public required string CleanupKind { get; init; }
    public string? ProtectionReason { get; init; }
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public long DirectoryCount { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool Truncated { get; init; }
    public bool Browsable { get; init; }
    public int BrowseOffset { get; init; }
    public IReadOnlyList<StorageNode> Children { get; init; } = [];

    [JsonIgnore]
    public string? PhysicalPath { get; init; }

    [JsonIgnore]
    public string? AllowedRoot { get; init; }
}

public sealed record StorageAreaSnapshot(
    string Id,
    string Name,
    string RootLabel,
    string Kind,
    bool LogicalOnly,
    long? VolumeTotalBytes,
    long? VolumeFreeBytes,
    StorageNode Root,
    string? Error);

public sealed record StorageSnapshot(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long PhysicalBytes,
    long LogicalBytes,
    IReadOnlyList<StorageAreaSnapshot> Areas);

public sealed record StorageProgress(
    bool Running,
    string? ScanId,
    DateTimeOffset? StartedAt,
    string? CurrentArea,
    long BytesVisited,
    long FilesVisited,
    long DirectoriesVisited,
    string? Error);

public sealed record StorageStatus(StorageProgress Progress, StorageSnapshot? Snapshot);

public sealed record WorkspaceRoot(Guid AgentId, string AgentName, string Path);

internal sealed record ScanArea(
    string Id,
    string Name,
    string RootLabel,
    string Kind,
    bool LogicalOnly,
    string? RootPath,
    IReadOnlyList<WorkspaceRoot>? Workspaces = null,
    IReadOnlySet<string>? AgentNames = null);

public sealed record CleanupPreview(
    string Token,
    string NodeId,
    string LogicalPath,
    string Name,
    long SizeBytes,
    long FileCount,
    DateTimeOffset ExpiresAt,
    string Action);

public sealed record BrowseRequest(string NodeId, string? SnapshotId, int? Offset = null);

public sealed record StorageBrowsePage(
    StorageNode Node,
    int Offset,
    int Limit,
    int Total,
    bool HasPrevious,
    bool HasNext);

public sealed record AnalyzeRequest(string NodeId, string? SnapshotId);

public sealed record StorageAnalysis(
    string Summary,
    IReadOnlyList<string> Contents,
    string DeletionImpact,
    string Recommendation,
    string Reasoning,
    double Confidence,
    string? Model,
    bool ActionAuthority = false);

internal sealed record StorageAnalysisEvidence(
    string SnapshotId,
    string NodeId,
    string Name,
    string LogicalPath,
    string Kind,
    string Origin,
    string Purpose,
    string CleanupKind,
    string? ProtectionReason,
    long SizeBytes,
    long FileCount,
    long DirectoryCount,
    DateTimeOffset? ModifiedAt,
    bool Aggregate,
    IReadOnlyList<StorageEvidenceItem> RepresentativeItems,
    IReadOnlyDictionary<string, long> Extensions);

internal sealed record StorageEvidenceItem(
    string Name,
    string Kind,
    long SizeBytes,
    long FileCount,
    long DirectoryCount,
    DateTimeOffset? ModifiedAt);

public sealed record QuarantineItem(
    string Id,
    string Name,
    string OriginalLogicalPath,
    long SizeBytes,
    long FileCount,
    bool Directory,
    DateTimeOffset QuarantinedAt);

internal sealed record QuarantineManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string OriginalPath,
    string OriginalLogicalPath,
    long SizeBytes,
    long FileCount,
    bool Directory,
    DateTimeOffset QuarantinedAt);
