using Leaf.Plugins.Storage;

var scratch = Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR");
if (string.IsNullOrWhiteSpace(scratch))
    throw new InvalidOperationException("REDLEAF_SCRATCH_DIR is required for storage tests.");
var root = Path.Combine(scratch, "storage-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var loose = Path.Combine(root, "old-loose.bin");
    await File.WriteAllBytesAsync(loose, new byte[2048]);
    File.SetLastWriteTimeUtc(loose, DateTime.UtcNow.AddHours(-2));
    var nova = Path.Combine(root, "Nova");
    var execution = Path.Combine(nova, "abc123");
    Directory.CreateDirectory(execution);
    await File.WriteAllTextAsync(Path.Combine(execution, ".redleaf-scratch-execution"), "marker");
    await File.WriteAllBytesAsync(Path.Combine(execution, "payload.bin"), new byte[4096]);
    Directory.SetLastWriteTimeUtc(execution, DateTime.UtcNow.AddHours(-2));
    for (var index = 0; index < 140; index++)
        await File.WriteAllBytesAsync(Path.Combine(root, $"tiny-{index:D3}.bin"), new byte[index + 1]);

    var scanner = new StorageScanner((_, _, _, _) => { });
    var area = scanner.Scan(new ScanArea("scratch", "Scratch", @"S:\", "physical", false,
        root, AgentNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nova" }),
        CancellationToken.None);

    Assert(area.Root.SizeBytes >= 2048 + 4096, "scan totals file bytes");
    var looseNode = area.Root.Children.Single(node => node.Name == "old-loose.bin");
    Assert(looseNode.CleanupKind == "quarantine", "old loose scratch is quarantinable");
    var novaNode = area.Root.Children.Single(node => node.Name == "Nova");
    Assert(novaNode.CleanupKind == "protected", "agent root stays protected");
    Assert(novaNode.Children.Single(node => node.Name == "abc123").CleanupKind == "quarantine",
        "marked inactive execution is quarantinable");
    Assert(area.Root.Children.Any(node => node.Kind == "aggregate"), "large sibling sets are bounded");

    StorageService.EnsureBelow(root, loose);
    var escaped = false;
    try { StorageService.EnsureBelow(root, Path.Combine(Path.GetDirectoryName(root)!, "outside")); }
    catch (StorageConflictException) { escaped = true; }
    Assert(escaped, "path containment rejects siblings");

    Console.WriteLine("Storage backend checks passed.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Failed: " + name);
}
