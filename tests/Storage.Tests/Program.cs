using Leaf.Plugins.Storage;
using Leaf.Sdk.Services;
using System.Text.Json.Nodes;

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

    var partials = new List<StorageAreaSnapshot>();
    var scanner = new StorageScanner((_, _, _, _) => { }, partials.Add);
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
    var aggregate = area.Root.Children.Single(node => node.Kind == "aggregate");
    Assert(aggregate.Children.Count == 0, "bounded sibling groups do not embed fake representative layers");
    Assert(aggregate.Browsable, "bounded sibling groups expose lazy concrete browsing");
    Assert(aggregate.BrowseOffset == 64, "aggregate browsing starts after visible siblings");
    Assert(partials.Count > 0, "scan publishes a partial tree before completion");
    Assert(partials.Any(partial => partial.Root.SizeBytes > 0),
        "partial tree carries measured bytes");
    Assert(partials.All(partial => MaxDepth(partial.Root) <= 2),
        "progressive snapshots stay compact while totals grow");

    var deep = Path.Combine(execution, "one", "two", "three", "four", "five");
    Directory.CreateDirectory(deep);
    await File.WriteAllBytesAsync(Path.Combine(deep, "deep.bin"), new byte[8192]);
    var bounded = scanner.Scan(new ScanArea("scratch", "Scratch", @"S:\", "physical", false,
        root, AgentNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nova" }),
        CancellationToken.None);
    var boundedExecution = bounded.Root.Children.Single(node => node.Name == "Nova")
        .Children.Single(node => node.Name == "abc123");
    Assert(MaxDepth(boundedExecution) <= 4,
        "visual descendants keep a fixed bound including aggregate representatives");
    Assert(boundedExecution.SizeBytes >= 4096 + 8192, "depth compaction preserves exact totals");
    Assert(Flatten(boundedExecution).Any(node => node.Kind == "aggregate"),
        "depth compaction emits a protected aggregate");
    Assert(Flatten(boundedExecution).Where(node => node.Kind == "aggregate")
            .All(node => node.Children.Count == 0 && node.Browsable),
        "depth aggregates defer to concrete lazy browsing");
    Assert(scanner.DrainDirectoryNodes().Any(node => node.Name == "five" && node.SizeBytes >= 8192),
        "scanner retains exact directory facts for lazy browsing");

    var compute = new FakeComputeJobs();
    var analysis = await StorageAnalyzer.AnalyzeAsync(new StorageAnalysisEvidence(
        "scan", "node", "cache", @"S:\cache", "directory", "Nova", "Agent scratch",
        "quarantine", null, 8192, 2, 1, DateTimeOffset.UtcNow.AddDays(-2), false,
        [new StorageEvidenceItem("weights.bin", "file", 8192, 1, 0,
            DateTimeOffset.UtcNow.AddDays(-2))],
        new Dictionary<string, long> { [".bin"] = 8192 }), compute, CancellationToken.None);
    Assert(compute.Payload?["qualityTier"]?.GetValue<string>() == "standard",
        "analysis uses the standard quality tier");
    Assert(compute.Payload?["messages"]?[0]?["content"]?.GetValue<string>()
            .Contains("weights.bin", StringComparison.Ordinal) == true,
        "analysis sends bounded metadata");
    Assert(analysis.Recommendation == "inspect" && !analysis.ActionAuthority,
        "analysis is structured and grants no action authority");

    var serviceRoot = Path.Combine(root, "service");
    var serviceScratch = Path.Combine(serviceRoot, "Scratch");
    Directory.CreateDirectory(serviceScratch);
    for (var index = 0; index < 140; index++)
        await File.WriteAllBytesAsync(Path.Combine(serviceScratch, $"entry-{index:D3}.bin"),
            new byte[index + 1]);
    var service = new StorageService(serviceRoot);
    service.StartScan([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    while (service.Status().Progress.Running) await Task.Delay(20);
    var serviceStatus = service.Status();
    var serviceAggregate = serviceStatus.Snapshot!.Areas.Single(item => item.Id == "scratch")
        .Root.Children.Single(node => node.Kind == "aggregate");
    var page = service.Browse(serviceAggregate.Id, serviceStatus.Snapshot.Id);
    Assert(page.Total == 76 && page.Node.Children.Count == 76,
        "aggregate browsing returns the concrete children omitted after the first 64");
    Assert(page.Node.Children.All(node => node.Kind == "file" && node.Name.StartsWith("entry-")),
        "aggregate browsing never manufactures another summary layer");

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

static int MaxDepth(StorageNode node)
    => node.Children.Count == 0 ? 0 : 1 + node.Children.Max(MaxDepth);

static IEnumerable<StorageNode> Flatten(StorageNode node)
{
    yield return node;
    foreach (var child in node.Children)
        foreach (var descendant in Flatten(child))
            yield return descendant;
}

sealed class FakeComputeJobs : IComputeJobs
{
    public JsonObject? Payload { get; private set; }

    public Task<ComputeJob> SubmitAsync(string capabilitySlug, JsonObject payload,
        ComputeJobOptions? options = null, CancellationToken ct = default)
    {
        Payload = payload;
        var response = new JsonObject
        {
            ["text"] = """
                {"summary":"Likely a model cache.","contents":["Binary model weights"],"deletionImpact":"The model would need to be downloaded or regenerated.","recommendation":"inspect","reasoning":"The name alone is not enough to prove it is disposable.","confidence":0.7}
                """,
            ["model"] = "test-standard",
        };
        return Task.FromResult(new ComputeJob(capabilitySlug, "job", response));
    }

    public Task<ComputeJobProgress> PollAsync(string capabilitySlug, string jobId,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ComputeJobProgress> WaitAsync(string capabilitySlug, string jobId,
        Action<ComputeJobProgress>? onProgress = null, ComputeJobOptions? options = null,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ComputeJobOutput?> DownloadOutputAsync(string capabilitySlug, string jobId,
        string? artifact = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ComputeStatus?> GetStatusAsync(CancellationToken ct = default)
        => throw new NotSupportedException();
}
