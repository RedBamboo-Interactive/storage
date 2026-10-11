using System.Collections.Concurrent;
using System.Text.Json;

namespace Leaf.Plugins.Storage;

public sealed class StorageService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, PendingCleanup> _pending = new();
    private readonly string _leafRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedLeaf");
    private readonly string _scratchRoot;
    private readonly string _quarantineRoot;
    private CancellationTokenSource? _scanCts;
    private StorageProgress _progress = new(false, null, null, null, 0, 0, 0, null);
    private StorageSnapshot? _snapshot;

    public StorageService()
    {
        _scratchRoot = Path.Combine(_leafRoot, "Scratch");
        _quarantineRoot = Path.Combine(_leafRoot, "Storage", "Quarantine");
    }

    public StorageStatus Status()
    {
        lock (_gate) return new StorageStatus(_progress, _snapshot);
    }

    public StorageProgress StartScan(IReadOnlyList<WorkspaceRoot> workspaces,
        IReadOnlySet<string> agentNames)
    {
        string scanId;
        DateTimeOffset started;
        lock (_gate)
        {
            if (_progress.Running) return _progress;
            scanId = Guid.NewGuid().ToString("N");
            started = DateTimeOffset.UtcNow;
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            _progress = new StorageProgress(true, scanId, started, "Preparing", 0, 0, 0, null);
        }

        var specs = new List<ScanArea>
        {
            new("scratch", "Scratch", ScratchLabel(), "physical", false, _scratchRoot,
                AgentNames: agentNames),
        };
        if (workspaces.Count > 0)
        {
            var workspaceLabel = WorkspaceLabel(workspaces);
            specs.Add(new ScanArea("workspaces", "Agent workspaces", workspaceLabel,
                "logical", true, null, workspaces, agentNames));
        }
        specs.Add(new ScanArea("leaf-data", "RedLeaf data", "%LOCALAPPDATA%\\RedLeaf",
            "physical", false, _leafRoot));

        var token = _scanCts.Token;
        _ = Task.Run(() => RunScan(scanId, started, specs, token), CancellationToken.None);
        return Status().Progress;
    }

    public CleanupPreview PreviewCleanup(string nodeId, string? snapshotId)
    {
        StorageSnapshot snapshot;
        lock (_gate)
        {
            if (_progress.Running)
                throw new StorageConflictException("Wait for the storage scan to finish before cleaning up.");
            snapshot = _snapshot ?? throw new StorageConflictException(
                "Run a storage scan before cleaning up.");
            if (snapshotId is not null && snapshot.Id != snapshotId)
                throw new StorageConflictException("The scan changed. Review the item again.");
        }

        var node = FindNode(snapshot, nodeId)
            ?? throw new KeyNotFoundException("The selected item is not present in the current scan.");
        if (node.CleanupKind != "quarantine" || node.PhysicalPath is null || node.AllowedRoot is null)
            throw new StorageConflictException(node.ProtectionReason ?? "This item is protected.");
        EnsureBelow(node.AllowedRoot, node.PhysicalPath);
        EnsureBelow(_scratchRoot, node.PhysicalPath);
        if (!File.Exists(node.PhysicalPath) && !Directory.Exists(node.PhysicalPath))
            throw new StorageConflictException("The selected item no longer exists. Scan again.");
        if (IsReparsePoint(node.PhysicalPath))
            throw new StorageConflictException("Linked paths cannot be cleaned up.");

        var token = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        _pending[token] = new PendingCleanup(token, snapshot.Id, node, expires);
        PrunePending();
        return new CleanupPreview(token, node.Id, node.LogicalPath, node.Name,
            node.SizeBytes, node.FileCount, expires, "quarantine");
    }

    public async Task<QuarantineItem> QuarantineAsync(string token, CancellationToken ct)
    {
        if (!_pending.TryRemove(token, out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow)
            throw new StorageConflictException("The cleanup confirmation expired. Review the item again.");
        var node = pending.Node;
        var source = node.PhysicalPath!;
        EnsureBelow(_scratchRoot, source);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(source) && !Directory.Exists(source))
            throw new StorageConflictException("The selected item no longer exists. Scan again.");
        if (IsReparsePoint(source))
            throw new StorageConflictException("Linked paths cannot be cleaned up.");
        if (node.ModifiedAt is { } observed)
        {
            var current = File.GetLastWriteTimeUtc(source);
            if (current > observed.UtcDateTime.AddSeconds(1))
                throw new StorageConflictException("The item changed after the scan and was left untouched.");
        }

        var id = Guid.NewGuid().ToString("N");
        var itemRoot = Path.Combine(_quarantineRoot, id);
        var content = Path.Combine(itemRoot, "content");
        Directory.CreateDirectory(itemRoot);
        var directory = Directory.Exists(source);
        var manifest = new QuarantineManifest(1, id, node.Name, Path.GetFullPath(source),
            node.LogicalPath, node.SizeBytes, node.FileCount, directory, DateTimeOffset.UtcNow);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(itemRoot, "manifest.json"),
                JsonSerializer.Serialize(manifest, Json), ct);
            if (directory) Directory.Move(source, content);
            else File.Move(source, content);
        }
        catch
        {
            try
            {
                if (Directory.Exists(itemRoot) && !Directory.EnumerateFileSystemEntries(itemRoot)
                        .Any(path => Path.GetFileName(path) == "content"))
                    Directory.Delete(itemRoot, recursive: true);
            }
            catch { /* preserve the original failure */ }
            throw;
        }

        return ToItem(manifest);
    }

    public IReadOnlyList<QuarantineItem> ListQuarantine()
    {
        if (!Directory.Exists(_quarantineRoot)) return [];
        var result = new List<QuarantineItem>();
        foreach (var directory in Directory.EnumerateDirectories(_quarantineRoot))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<QuarantineManifest>(
                    File.ReadAllText(Path.Combine(directory, "manifest.json")), Json);
                if (manifest is { SchemaVersion: 1 } && manifest.Id == Path.GetFileName(directory)
                    && (File.Exists(Path.Combine(directory, "content"))
                        || Directory.Exists(Path.Combine(directory, "content"))))
                    result.Add(ToItem(manifest));
            }
            catch { /* malformed quarantine entries are never acted on */ }
        }
        return result.OrderByDescending(item => item.QuarantinedAt).ToList();
    }

    public QuarantineItem Restore(string id)
    {
        var (root, manifest) = ReadQuarantine(id);
        EnsureBelow(_scratchRoot, manifest.OriginalPath);
        if (File.Exists(manifest.OriginalPath) || Directory.Exists(manifest.OriginalPath))
            throw new StorageConflictException("The original path is occupied. The quarantined item was left untouched.");
        var parent = Path.GetDirectoryName(manifest.OriginalPath)
            ?? throw new StorageConflictException("The original parent is unavailable.");
        EnsureBelowOrEqual(_scratchRoot, parent);
        Directory.CreateDirectory(parent);
        var content = Path.Combine(root, "content");
        if (manifest.Directory) Directory.Move(content, manifest.OriginalPath);
        else File.Move(content, manifest.OriginalPath);
        DeleteTreeWithoutFollowingLinks(root);
        return ToItem(manifest);
    }

    public QuarantineItem Purge(string id, string confirmation)
    {
        if (!string.Equals(id, confirmation, StringComparison.Ordinal))
            throw new StorageConflictException("Permanent deletion requires the exact quarantine ID.");
        var (root, manifest) = ReadQuarantine(id);
        EnsureBelow(_quarantineRoot, root);
        var content = Path.Combine(root, "content");
        if (Directory.Exists(content)) DeleteTreeWithoutFollowingLinks(content);
        else if (File.Exists(content))
        {
            if (IsReparsePoint(content))
                throw new StorageConflictException("Linked quarantine content cannot be deleted.");
            File.Delete(content);
        }
        DeleteTreeWithoutFollowingLinks(root);
        return ToItem(manifest);
    }

    private void RunScan(string scanId, DateTimeOffset started, IReadOnlyList<ScanArea> specs,
        CancellationToken ct)
    {
        try
        {
            var areas = new List<StorageAreaSnapshot>();
            var scanner = new StorageScanner((area, bytes, files, directories) =>
            {
                lock (_gate)
                {
                    if (_progress.ScanId == scanId)
                        _progress = _progress with
                        {
                            CurrentArea = area,
                            BytesVisited = bytes,
                            FilesVisited = files,
                            DirectoriesVisited = directories,
                        };
                }
            }, partial => PublishProgressSnapshot(scanId, started, areas, partial));
            foreach (var spec in specs)
            {
                var area = scanner.Scan(spec, ct);
                areas.Add(area);
                PublishProgressSnapshot(scanId, started, areas, null);
            }
            var snapshot = new StorageSnapshot(scanId, started, DateTimeOffset.UtcNow,
                areas.Where(area => !area.LogicalOnly).Sum(area => area.Root.SizeBytes),
                areas.Where(area => area.LogicalOnly).Sum(area => area.Root.SizeBytes),
                areas);
            lock (_gate)
            {
                if (_progress.ScanId != scanId) return;
                _snapshot = snapshot;
                _progress = _progress with
                {
                    Running = false,
                    CurrentArea = null,
                    Error = null,
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
                if (_progress.ScanId == scanId)
                    _progress = _progress with { Running = false, Error = "Scan cancelled" };
        }
        catch (Exception ex)
        {
            lock (_gate)
                if (_progress.ScanId == scanId)
                    _progress = _progress with { Running = false, Error = ex.Message };
        }
    }

    private void PublishProgressSnapshot(string scanId, DateTimeOffset started,
        IReadOnlyList<StorageAreaSnapshot> completed, StorageAreaSnapshot? partial)
    {
        var areas = completed.ToList();
        if (partial is not null)
        {
            var existing = areas.FindIndex(area => area.Id == partial.Id);
            if (existing >= 0) areas[existing] = partial;
            else areas.Add(partial);
        }
        if (areas.Count == 0) return;
        var snapshot = new StorageSnapshot(scanId, started, DateTimeOffset.UtcNow,
            areas.Where(area => !area.LogicalOnly).Sum(area => area.Root.SizeBytes),
            areas.Where(area => area.LogicalOnly).Sum(area => area.Root.SizeBytes),
            areas);
        lock (_gate)
        {
            if (_progress.ScanId == scanId && _progress.Running)
                _snapshot = snapshot;
        }
    }

    private static StorageNode? FindNode(StorageSnapshot snapshot, string id)
    {
        foreach (var area in snapshot.Areas)
        {
            var found = FindNode(area.Root, id);
            if (found is not null) return found;
        }
        return null;
    }

    private static StorageNode? FindNode(StorageNode root, string id)
    {
        if (root.Id == id) return root;
        foreach (var child in root.Children)
        {
            var found = FindNode(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private (string Root, QuarantineManifest Manifest) ReadQuarantine(string id)
    {
        if (id.Length != 32 || id.Any(ch => !Uri.IsHexDigit(ch)))
            throw new KeyNotFoundException("Quarantine item not found.");
        var root = Path.Combine(_quarantineRoot, id);
        EnsureBelow(_quarantineRoot, root);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath)) throw new KeyNotFoundException("Quarantine item not found.");
        QuarantineManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<QuarantineManifest>(
                File.ReadAllText(manifestPath), Json)
                ?? throw new InvalidDataException();
        }
        catch
        {
            throw new StorageConflictException("The quarantine manifest is invalid.");
        }
        if (manifest.SchemaVersion != 1 || manifest.Id != id)
            throw new StorageConflictException("The quarantine manifest does not match its directory.");
        return (root, manifest);
    }

    private static QuarantineItem ToItem(QuarantineManifest manifest) => new(
        manifest.Id, manifest.Name, manifest.OriginalLogicalPath, manifest.SizeBytes,
        manifest.FileCount, manifest.Directory, manifest.QuarantinedAt);

    private void PrunePending()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _pending)
            if (item.Value.ExpiresAt < now) _pending.TryRemove(item.Key, out _);
    }

    private static string WorkspaceLabel(IReadOnlyList<WorkspaceRoot> workspaces)
    {
        try
        {
            var root = Path.GetPathRoot(workspaces[0].Path)?.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(root) ? "Agent workspaces" : root + "\\Workspaces";
        }
        catch { return "Agent workspaces"; }
    }

    private string ScratchLabel()
    {
        try
        {
            var marker = File.ReadAllText(Path.Combine(_scratchRoot, ".redleaf-drive")).Trim();
            if (marker.Length == 1 && char.IsLetter(marker[0]))
                return char.ToUpperInvariant(marker[0]) + @":\";
        }
        catch { /* S: is the product default when no marker is readable */ }
        return @"S:\";
    }

    internal static void EnsureBelow(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new StorageConflictException("The selected path is outside the managed storage root.");
    }

    private static void EnsureBelowOrEqual(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var canonicalPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!canonicalPath.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new StorageConflictException("The selected path is outside the managed storage root.");
    }

    private static bool IsReparsePoint(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void DeleteTreeWithoutFollowingLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(entry, false);
                else File.Delete(entry);
            }
            else if (attributes.HasFlag(FileAttributes.Directory))
                DeleteTreeWithoutFollowingLinks(entry);
            else
                File.Delete(entry);
        }
        Directory.Delete(directory, false);
    }

    private sealed record PendingCleanup(
        string Token,
        string SnapshotId,
        StorageNode Node,
        DateTimeOffset ExpiresAt);
}

public sealed class StorageConflictException(string message) : Exception(message);
