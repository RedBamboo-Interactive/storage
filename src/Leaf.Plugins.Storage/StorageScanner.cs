using System.Security.Cryptography;
using System.Text;

namespace Leaf.Plugins.Storage;

internal sealed class StorageScanner(Action<string, long, long, long> report)
{
    private const int MaxChildren = 64;
    private const int MaxVisibleDepth = 4;
    private static readonly TimeSpan ActiveSafetyWindow = TimeSpan.FromMinutes(30);
    private readonly Action<string, long, long, long> _report = report;
    private long _bytes;
    private long _files;
    private long _directories;

    public StorageAreaSnapshot Scan(ScanArea area, CancellationToken ct)
    {
        try
        {
            StorageNode root;
            if (area.Workspaces is { Count: > 0 })
                root = ScanWorkspaces(area, ct);
            else if (area.RootPath is { } rootPath && Directory.Exists(rootPath))
                root = ScanDirectory(area, rootPath, area.Name, area.RootLabel, 0, ct);
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
                forcedOrigin: workspace.AgentName, forcedPurpose: "Agent workspace");
            accumulator.Add(node);
        }

        var children = accumulator.Complete(area.Id, area.RootLabel);
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
            SizeBytes = accumulator.SizeBytes,
            FileCount = accumulator.FileCount,
            DirectoryCount = accumulator.DirectoryCount,
            ModifiedAt = accumulator.ModifiedAt,
            Truncated = accumulator.Truncated,
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
        string? forcedOrigin = null,
        string? forcedPurpose = null)
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
                        forcedOrigin, forcedPurpose);
                    accumulator.Add(child);
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
                        if ((_files & 0x3ff) == 0) Report(area.Name);
                    }
                    catch { /* files can disappear while the scan is running */ }
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error ??= ex.Message;
        }

        var modified = TryModified(path);
        var policy = Policy(area, path, depth, isDirectory: true, modified,
            forcedOrigin, forcedPurpose);
        if (error is not null)
            policy = (policy.Origin, policy.Purpose, "protected",
                "The scan could not read this directory completely: " + error);
        var children = accumulator.Complete(area.Id, logicalPath);
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
            SizeBytes = accumulator.SizeBytes,
            FileCount = accumulator.FileCount,
            DirectoryCount = accumulator.DirectoryCount + 1,
            ModifiedAt = Max(modified, accumulator.ModifiedAt),
            Truncated = accumulator.Truncated,
            Children = children,
            PhysicalPath = path,
            AllowedRoot = area.RootPath,
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
                _otherBytes += size;
                _otherFiles++;
                _otherModified = Max(_otherModified, modified);
                return;
            }
            if (_largest.Count >= capacity)
            {
                _largest.TryPeek(out _, out var smallest);
                if (size <= smallest)
                {
                    _otherBytes += size;
                    _otherFiles++;
                    _otherModified = Max(_otherModified, modified);
                    return;
                }
                var removed = _largest.Dequeue();
                Drop(removed);
            }
            var node = factory();
            _largest.Enqueue(node, node.SizeBytes);
        }

        public IReadOnlyList<StorageNode> Complete(string area, string logicalPath)
        {
            var items = _largest.UnorderedItems.Select(item => item.Element)
                .OrderByDescending(node => node.SizeBytes)
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
