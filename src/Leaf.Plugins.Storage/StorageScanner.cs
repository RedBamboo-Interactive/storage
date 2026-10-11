using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Leaf.Plugins.Storage;

internal sealed class StorageScanner(
    Action<string, long, long, long> report,
    Action<StorageAreaSnapshot>? publishPartial = null)
{
    private const int MaxChildren = 64;
    private const int MaxVisibleDepth = 4;
    private const int MaxProgressChildren = 24;
    private const int MaxProgressDepth = 2;
    private static readonly TimeSpan PartialPublishInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ActiveSafetyWindow = TimeSpan.FromMinutes(30);
    private readonly Action<string, long, long, long> _report = report;
    private readonly Action<StorageAreaSnapshot>? _publishPartial = publishPartial;
    private readonly Dictionary<string, StorageNode> _directoryNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private long _files;
    private long _directories;
    private long _lastPartialTimestamp;

    public IReadOnlyList<StorageNode> DrainDirectoryNodes()
    {
        var nodes = _directoryNodes.Values.ToList();
        _directoryNodes.Clear();
        return nodes;
    }

    public StorageAreaSnapshot Scan(ScanArea area, CancellationToken ct)
    {
        try
        {
            StorageNode root;
            if (area.Workspaces is { Count: > 0 })
                root = ScanWorkspaces(area, ct);
            else if (area.RootPath is { } rootPath && Directory.Exists(rootPath))
                root = ScanDirectory(area, rootPath, area.Name, area.RootLabel, 0, ct,
                    partial => PublishArea(area, partial));
            else
                throw new DirectoryNotFoundException(area.RootPath ?? area.RootLabel);

            var (total, free) = area.LogicalOnly || area.RootPath is null
                ? ((long?)null, (long?)null)
                : VolumeSpace(area.RootPath);
            return new StorageAreaSnapshot(area.Id, area.Name, area.RootLabel, area.Kind,
                area.LogicalOnly, total, free, root, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var empty = EmptyNode(area.Id, area.Name, area.RootLabel,
                "The storage area could not be scanned.");
            return new StorageAreaSnapshot(area.Id, area.Name, area.RootLabel, area.Kind,
                area.LogicalOnly, null, null, empty, ex.Message);
        }
    }

    private StorageNode ScanWorkspaces(ScanArea area, CancellationToken ct)
    {
        var accumulator = new NodeAccumulator(MaxChildren);
        foreach (var workspace in area.Workspaces!)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(workspace.Path)) continue;
            var node = ScanDirectory(area, workspace.Path, workspace.AgentName,
                CombineLogical(area.RootLabel, workspace.AgentName), 1, ct,
                partial => PublishArea(area, BuildWorkspaceRoot(area, accumulator, partial)),
                forcedOrigin: workspace.AgentName, forcedPurpose: "Agent workspace",
                forcedAllowedRoot: workspace.Path);
            accumulator.Add(node);
            PublishArea(area, BuildWorkspaceRoot(area, accumulator));
        }

        return BuildWorkspaceRoot(area, accumulator);
    }

    private static StorageNode BuildWorkspaceRoot(ScanArea area, NodeAccumulator accumulator,
        StorageNode? current = null)
    {
        var children = accumulator.Complete(area.Id, area.RootLabel, null, null, current);
        return new StorageNode
        {
            Id = NodeId(area.Id, ""),
            Name = area.Name,
            LogicalPath = area.RootLabel,
            Kind = "directory",
            Origin = "Agents",
            Purpose = "Entity-backed Agent workspaces",
            CleanupKind = "protected",
            ProtectionReason = "Workspace files are durable entity data. Remove them through the owning Agent or editor.",
            SizeBytes = accumulator.SizeBytes + (current?.SizeBytes ?? 0),
            FileCount = accumulator.FileCount + (current?.FileCount ?? 0),
            DirectoryCount = accumulator.DirectoryCount + (current?.DirectoryCount ?? 0),
            ModifiedAt = Max(accumulator.ModifiedAt, current?.ModifiedAt),
            Truncated = accumulator.Truncated || current?.Truncated == true,
            Children = children,
        };
    }

    private StorageNode ScanDirectory(
        ScanArea area,
        string path,
        string name,
        string logicalPath,
        int depth,
        CancellationToken ct,
        Action<StorageNode>? publish = null,
        string? forcedOrigin = null,
        string? forcedPurpose = null,
        string? forcedAllowedRoot = null)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _directories);
        Report(area.Name);

        // The scanner still walks the complete tree so totals remain exact, but the
        // browser snapshot is deliberately shallow. Retaining even a bounded set at
        // every directory can still produce hundreds of thousands of JSON nodes on
        // package caches and PostgreSQL data directories.
        var accumulator = new NodeAccumulator(depth >= MaxVisibleDepth ? 0 : MaxChildren);
        string? error = null;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            entries = [];
            error = ex.Message;
        }

        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                var childName = Path.GetFileName(entry);
                if (depth == 0 && area.Id == "leaf-data"
                    && (childName.Equals("Scratch", StringComparison.OrdinalIgnoreCase)
                        || childName.Equals("Storage", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var childLogical = CombineLogical(logicalPath, childName);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    var child = ScanDirectory(area, entry, childName, childLogical, depth + 1, ct,
                        partial => publish?.Invoke(BuildDirectoryNode(area, path, name, logicalPath,
                            depth, accumulator, error, partial, forcedOrigin, forcedPurpose,
                            forcedAllowedRoot)),
                        forcedOrigin, forcedPurpose, forcedAllowedRoot);
                    accumulator.Add(child);
                    if (depth <= 1 || ShouldPublishPartial())
                        publish?.Invoke(BuildDirectoryNode(area, path, name, logicalPath,
                            depth, accumulator, error, null, forcedOrigin, forcedPurpose,
                            forcedAllowedRoot));
                }
                else
                {
                    try
                    {
                        var file = new FileInfo(entry);
                        var length = file.Length;
                        Interlocked.Add(ref _bytes, length);
                        Interlocked.Increment(ref _files);
                        accumulator.AddFile(() => BuildFileNode(area, entry, childName, childLogical,
                            depth + 1, length, file.LastWriteTimeUtc, forcedOrigin, forcedPurpose),
                            length, file.LastWriteTimeUtc);
                        if ((_files & 0x3ff) == 0)
                        {
                            Report(area.Name);
                            if (ShouldPublishPartial())
                                publish?.Invoke(BuildDirectoryNode(area, path, name, logicalPath,
                                    depth, accumulator, error, null, forcedOrigin, forcedPurpose,
                                    forcedAllowedRoot));
                        }
                    }
                    catch { /* files can disappear while the scan is running */ }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error ??= ex.Message;
        }

        var result = BuildDirectoryNode(area, path, name, logicalPath, depth, accumulator, error,
            null, forcedOrigin, forcedPurpose, forcedAllowedRoot);
        _directoryNodes[Path.GetFullPath(path)] = Shallow(result);
        return result;
    }

    private static StorageNode BuildDirectoryNode(
        ScanArea area,
        string path,
        string name,
        string logicalPath,
        int depth,
        NodeAccumulator accumulator,
        string? error,
        StorageNode? current,
        string? forcedOrigin,
        string? forcedPurpose,
        string? forcedAllowedRoot)
    {
        var modified = TryModified(path);
        var policy = Policy(area, path, depth, isDirectory: true, modified,
            forcedOrigin, forcedPurpose);
        if (error is not null)
            policy = (policy.Origin, policy.Purpose, "protected",
                "The scan could not read this directory completely: " + error);
        var allowedRoot = forcedAllowedRoot ?? area.RootPath;
        var children = accumulator.Complete(area.Id, logicalPath, path, allowedRoot, current);
        return new StorageNode
        {
            Id = NodeId(area.Id, logicalPath),
            Name = name,
            LogicalPath = logicalPath,
            Kind = "directory",
            Origin = policy.Origin,
            Purpose = policy.Purpose,
            CleanupKind = policy.CleanupKind,
            ProtectionReason = policy.ProtectionReason,
            SizeBytes = accumulator.SizeBytes + (current?.SizeBytes ?? 0),
            FileCount = accumulator.FileCount + (current?.FileCount ?? 0),
            DirectoryCount = accumulator.DirectoryCount + (current?.DirectoryCount ?? 0) + 1,
            ModifiedAt = Max(modified, Max(accumulator.ModifiedAt, current?.ModifiedAt)),
            Truncated = accumulator.Truncated || current?.Truncated == true,
            Browsable = allowedRoot is not null,
            Children = children,
            PhysicalPath = path,
            AllowedRoot = allowedRoot,
        };
    }

    private StorageNode BuildFileNode(ScanArea area, string path, string name, string logicalPath,
        int depth, long length, DateTime modified, string? forcedOrigin, string? forcedPurpose)
    {
        var policy = Policy(area, path, depth, isDirectory: false, modified,
            forcedOrigin, forcedPurpose);
        return new StorageNode
        {
            Id = NodeId(area.Id, logicalPath),
            Name = name,
            LogicalPath = logicalPath,
            Kind = "file",
            Origin = policy.Origin,
            Purpose = policy.Purpose,
            CleanupKind = policy.CleanupKind,
            ProtectionReason = policy.ProtectionReason,
            SizeBytes = length,
            FileCount = 1,
            DirectoryCount = 0,
            ModifiedAt = modified,
            PhysicalPath = path,
            AllowedRoot = area.RootPath,
        };
    }

    private static (string Origin, string Purpose, string CleanupKind, string? ProtectionReason) Policy(
        ScanArea area, string path, int depth, bool isDirectory, DateTimeOffset? modified,
        string? forcedOrigin, string? forcedPurpose)
    {
        if (area.Id == "workspaces")
            return (forcedOrigin ?? "Agent", forcedPurpose ?? "Agent workspace", "protected",
                "Workspace content is durable entity data.");
        if (area.Id == "leaf-data")
        {
            var component = depth == 1 ? Path.GetFileName(path) : FirstRelativeSegment(area.RootPath!, path);
            return ("RedLeaf", ComponentPurpose(component), "protected",
                "Application data is managed by RedLeaf and cannot be removed here.");
        }

        if (area.Id != "scratch")
            return ("RedLeaf", "Managed storage", "protected", "This area is read-only.");

        var relative = Path.GetRelativePath(area.RootPath!, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stale = modified is { } timestamp && DateTimeOffset.UtcNow - timestamp >= ActiveSafetyWindow;
        if (depth == 1)
        {
            var isAgentRoot = isDirectory && area.AgentNames?.Contains(parts[0]) == true;
            if (isAgentRoot)
                return (parts[0], "Agent scratch", "protected",
                    "Agent roots are containers. Select an individual execution inside them.");
            return ("Loose scratch", "Legacy or untracked scratch", stale ? "quarantine" : "protected",
                stale ? null : "Recently modified scratch is protected for 30 minutes.");
        }

        var owner = parts.Length > 0 ? parts[0] : "Scratch";
        if (depth == 2 && isDirectory
            && File.Exists(Path.Combine(path, ".redleaf-scratch-execution")))
            return (owner, "Managed Agent execution", stale ? "quarantine" : "protected",
                stale ? null : "Recently modified execution scratch is protected for 30 minutes.");

        return (owner, "Scratch contents", "protected",
            "Select the execution or loose root item rather than deleting inside it.");
    }

    private static string ComponentPurpose(string component) => component.ToLowerInvariant() switch
    {
        "assets" => "Assets",
        "postgres" => "Database",
        "runtimes" => "Provider runtimes and models",
        "logs" => "Logs",
        "backups" => "Backups",
        "plugins" or "extensions" => "Installed extensions",
        "gallery" => "Gallery index and curation",
        _ => "RedLeaf application data",
    };

    private static string FirstRelativeSegment(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }

    private void Report(string area) => _report(area,
        Interlocked.Read(ref _bytes), Interlocked.Read(ref _files), Interlocked.Read(ref _directories));

    private void PublishArea(ScanArea area, StorageNode root)
    {
        if (_publishPartial is null) return;
        var (total, free) = area.LogicalOnly || area.RootPath is null
            ? ((long?)null, (long?)null)
            : VolumeSpace(area.RootPath);
        _publishPartial(new StorageAreaSnapshot(area.Id, area.Name, area.RootLabel, area.Kind,
            area.LogicalOnly, total, free, CompactForProgress(area.Id, root, 0), null));
    }

    private static StorageNode CompactForProgress(string area, StorageNode node, int depth)
    {
        if (depth >= MaxProgressDepth || node.Children.Count == 0)
            return CopyNode(node, [], node.Truncated || node.Children.Count > 0);

        var ordered = node.Children.OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var children = ordered.Take(MaxProgressChildren)
            .Select(child => CompactForProgress(area, child, depth + 1))
            .ToList();
        var omitted = ordered.Skip(MaxProgressChildren).ToList();
        if (omitted.Count > 0)
        {
            children.Add(new StorageNode
            {
                Id = NodeId(area, node.LogicalPath + "\\__progress_other"),
                Name = "Other items still scanning",
                LogicalPath = CombineLogical(node.LogicalPath, "Other items still scanning"),
                Kind = "aggregate",
                Origin = "Mixed",
                Purpose = "Bounded live scan summary",
                CleanupKind = "protected",
                ProtectionReason = "Wait for the scan to finish before cleaning up.",
                SizeBytes = omitted.Sum(child => child.SizeBytes),
                FileCount = omitted.Sum(child => child.FileCount),
                DirectoryCount = omitted.Sum(child => child.DirectoryCount),
                ModifiedAt = omitted.Select(child => child.ModifiedAt).Aggregate(Max),
                Truncated = true,
                Children = [],
            });
        }
        return CopyNode(node, children, node.Truncated || omitted.Count > 0);
    }

    private static StorageNode CopyNode(StorageNode node, IReadOnlyList<StorageNode> children,
        bool truncated) => new()
    {
        Id = node.Id,
        Name = node.Name,
        LogicalPath = node.LogicalPath,
        Kind = node.Kind,
        Origin = node.Origin,
        Purpose = node.Purpose,
        CleanupKind = node.CleanupKind,
        ProtectionReason = node.ProtectionReason,
        SizeBytes = node.SizeBytes,
        FileCount = node.FileCount,
        DirectoryCount = node.DirectoryCount,
        ModifiedAt = node.ModifiedAt,
        Truncated = truncated,
        Browsable = node.Browsable,
        BrowseOffset = node.BrowseOffset,
        Children = children,
        PhysicalPath = node.PhysicalPath,
        AllowedRoot = node.AllowedRoot,
    };

    private bool ShouldPublishPartial()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastPartialTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < PartialPublishInterval)
            return false;
        Interlocked.Exchange(ref _lastPartialTimestamp, now);
        return true;
    }

    private static DateTimeOffset? TryModified(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
        => left is null ? right : right is null ? left : left > right ? left : right;

    private static string CombineLogical(string parent, string child)
        => parent.EndsWith('\\') || parent.EndsWith('/') ? parent + child : parent + "\\" + child;

    internal static string NodeId(string area, string logicalPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(area + "\n" + logicalPath.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static (long? Total, long? Free) VolumeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return (null, null);
            var drive = new DriveInfo(root);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch { return (null, null); }
    }

    private static StorageNode EmptyNode(string area, string name, string logicalPath, string reason) => new()
    {
        Id = NodeId(area, logicalPath),
        Name = name,
        LogicalPath = logicalPath,
        Kind = "directory",
        Origin = "RedLeaf",
        Purpose = "Unavailable",
        CleanupKind = "protected",
        ProtectionReason = reason,
    };

    internal static StorageNode Shallow(StorageNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        LogicalPath = node.LogicalPath,
        Kind = node.Kind,
        Origin = node.Origin,
        Purpose = node.Purpose,
        CleanupKind = node.CleanupKind,
        ProtectionReason = node.ProtectionReason,
        SizeBytes = node.SizeBytes,
        FileCount = node.FileCount,
        DirectoryCount = node.DirectoryCount,
        ModifiedAt = node.ModifiedAt,
        Truncated = node.Truncated || node.Children.Count > 0,
        Browsable = node.Browsable,
        BrowseOffset = node.BrowseOffset,
        PhysicalPath = node.PhysicalPath,
        AllowedRoot = node.AllowedRoot,
    };

    private sealed class NodeAccumulator(int capacity)
    {
        private readonly PriorityQueue<StorageNode, long> _largest = new();
        private long _otherBytes;
        private long _otherFiles;
        private long _otherDirectories;
        private DateTimeOffset? _otherModified;

        public long SizeBytes { get; private set; }
        public long FileCount { get; private set; }
        public long DirectoryCount { get; private set; }
        public DateTimeOffset? ModifiedAt { get; private set; }
        public bool Truncated => _otherFiles > 0 || _otherDirectories > 0;

        public void Add(StorageNode node)
        {
            Accumulate(node.SizeBytes, node.FileCount, node.DirectoryCount, node.ModifiedAt);
            if (capacity <= 0)
            {
                Drop(node);
                return;
            }
            if (_largest.Count < capacity)
            {
                _largest.Enqueue(node, node.SizeBytes);
                return;
            }
            _largest.TryPeek(out _, out var smallest);
            if (node.SizeBytes <= smallest)
            {
                Drop(node);
                return;
            }
            var removed = _largest.Dequeue();
            Drop(removed);
            _largest.Enqueue(node, node.SizeBytes);
        }

        public void AddFile(Func<StorageNode> factory, long size, DateTimeOffset modified)
        {
            Accumulate(size, 1, 0, modified);
            if (capacity <= 0)
            {
                DropFile(factory, size, modified);
                return;
            }
            if (_largest.Count >= capacity)
            {
                _largest.TryPeek(out _, out var smallest);
                if (size <= smallest)
                {
                    DropFile(factory, size, modified);
                    return;
                }
                var removed = _largest.Dequeue();
                Drop(removed);
            }
            var node = factory();
            _largest.Enqueue(node, node.SizeBytes);
        }

        private void DropFile(Func<StorageNode> factory, long size, DateTimeOffset modified)
        {
            _otherBytes += size;
            _otherFiles++;
            _otherModified = Max(_otherModified, modified);
        }

        public IReadOnlyList<StorageNode> Complete(string area, string logicalPath,
            string? physicalPath, string? allowedRoot, StorageNode? current = null)
        {
            var items = _largest.UnorderedItems.Select(item => item.Element)
                .OrderByDescending(node => node.SizeBytes)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (current is not null) items.Add(current);
            items = items.OrderByDescending(node => node.SizeBytes)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (Truncated)
            {
                items.Add(new StorageNode
                {
                    Id = NodeId(area, logicalPath + "\\__other"),
                    Name = "Other items",
                    LogicalPath = CombineLogical(logicalPath, "Other items"),
                    Kind = "aggregate",
                    Origin = "Mixed",
                    Purpose = "Smaller items grouped for a bounded scan",
                    CleanupKind = "protected",
                    ProtectionReason = "Aggregates are summaries. Select a concrete item to clean it up.",
                    SizeBytes = _otherBytes,
                    FileCount = _otherFiles,
                    DirectoryCount = _otherDirectories,
                    ModifiedAt = _otherModified,
                    Truncated = true,
                    Browsable = physicalPath is not null && allowedRoot is not null,
                    BrowseOffset = capacity,
                    PhysicalPath = physicalPath,
                    AllowedRoot = allowedRoot,
                });
            }
            return items;
        }

        private void Accumulate(long size, long files, long directories, DateTimeOffset? modified)
        {
            SizeBytes += size;
            FileCount += files;
            DirectoryCount += directories;
            ModifiedAt = Max(ModifiedAt, modified);
        }

        private void Drop(StorageNode node)
        {
            _otherBytes += node.SizeBytes;
            _otherFiles += node.FileCount;
            _otherDirectories += node.DirectoryCount;
            _otherModified = Max(_otherModified, node.ModifiedAt);
        }
    }
}
