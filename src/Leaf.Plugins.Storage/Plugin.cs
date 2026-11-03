using System.Reflection;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Storage;

public sealed record CleanupPreviewRequest(string NodeId, string? SnapshotId);
public sealed record CleanupCommitRequest(string Token);
public sealed record PurgeRequest(string Confirmation);

public sealed class StoragePlugin : ILeafPlugin
{
    public const string PluginId = "storage";

    public PluginManifest Manifest { get; } = LoadManifest();

    private static PluginManifest LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json")
            ?? throw new InvalidOperationException("Embedded plugin.json is missing");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
        => services.AddSingleton<StorageService>();

    /// <summary>
    /// Endpoints are pre-namespaced at /api/apps/storage with auth, telemetry,
    /// and an exception boundary already attached. Kernel services shared across
    /// plugins are keyed by plugin id — inject them with [FromKeyedServices(PluginId)]
    /// (a plain parameter binds nothing and crashes the kernel at startup).
    /// </summary>
    public void MapEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/status", (HttpContext ctx, StorageService storage) =>
        {
            try
            {
                StorageAccess.RequireAdministrator(ctx.User);
                return Results.Ok(storage.Status());
            }
            catch (StorageAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        }).WithSummary("Returns the current storage scan progress and the latest snapshot, which updates while a scan runs.");

        group.MapPost("/scan", async (HttpContext ctx, StorageService storage,
            [FromKeyedServices(PluginId)] IEntityStore store,
            IAgentWorkspacePathResolver workspacePaths, CancellationToken ct) =>
        {
            try
            {
                StorageAccess.RequireAdministrator(ctx.User);
                var agents = await store.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 500 }, ct);
                var workspaces = new List<WorkspaceRoot>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Nova", "Axl" };
                foreach (var agent in agents)
                {
                    names.Add(agent.Name);
                    var path = await workspacePaths.ResolveAsync(agent.Id, ct);
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                        && !workspaces.Any(item => Path.GetFullPath(item.Path)
                            .Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                        workspaces.Add(new WorkspaceRoot(agent.Id, agent.Name, path));
                }
                return Results.Accepted(value: storage.StartScan(workspaces, names));
            }
            catch (StorageAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        }).WithSummary("Starts a background scan of scratch, Agent workspaces and RedLeaf data and returns 202 with the progress. If a scan is already running it returns that scan's progress instead; poll GET /status for results.");

        group.MapPost("/browse", (BrowseRequest request, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(
                storage.Browse(request.NodeId, request.SnapshotId, request.Offset))))
            .WithSummary("Loads one page of the concrete files and folders under a scanned directory or an Other items group. Body: nodeId (required), snapshotId and offset (optional).");

        group.MapPost("/analyze", async (AnalyzeRequest request, HttpContext ctx,
            StorageService storage, IComputeJobs compute,
            CancellationToken ct) => await GuardAsync(ctx, async () =>
        {
            var evidence = storage.PrepareAnalysis(request.NodeId, request.SnapshotId);
            return Results.Ok(await StorageAnalyzer.AnalyzeAsync(evidence, compute, ct));
        })).WithSummary("Returns an advisory summary and suggested next action for a scanned item, based on names, sizes and counts only. It never reads file contents or changes anything. Body: nodeId (required), snapshotId (optional).");

        group.MapPost("/cleanup/preview", (CleanupPreviewRequest request, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(
                storage.PreviewCleanup(request.NodeId, request.SnapshotId))))
            .WithSummary("Checks that a scanned item can be cleaned up and returns a confirmation token valid for two minutes. Nothing is moved; it needs a finished scan, and protected items are rejected with 409. Body: nodeId (required), snapshotId (optional).");

        group.MapPost("/cleanup/quarantine", async (CleanupCommitRequest request, HttpContext ctx,
            StorageService storage, CancellationToken ct) =>
            await GuardAsync(ctx, async () => Results.Ok(await storage.QuarantineAsync(request.Token, ct))))
            .WithSummary("Moves the item from a cleanup preview token into quarantine, where it can be restored. The token is single use. Body: token (required) from POST /cleanup/preview.");

        group.MapGet("/quarantine", (HttpContext ctx, StorageService storage)
            => Guard(ctx, () => Results.Ok(new { items = storage.ListQuarantine() })))
            .WithSummary("Lists the quarantined items that can be restored or permanently deleted.");

        group.MapPost("/quarantine/{id}/restore", (string id, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(storage.Restore(id))))
            .WithSummary("Moves a quarantined item back to its original path. Fails with 409 if something already exists at that path.");

        group.MapPost("/quarantine/{id}/purge", (string id, PurgeRequest request,
            HttpContext ctx, StorageService storage) =>
            Guard(ctx, () => Results.Ok(storage.Purge(id, request.Confirmation))))
            .WithSummary("Permanently deletes a quarantined item, which cannot be undone. Body: confirmation (required) must equal the quarantine id exactly.");
    }

    public Task OnStartupAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

    private static IResult Guard(HttpContext ctx, Func<IResult> action)
    {
        try
        {
            StorageAccess.RequireAdministrator(ctx.User);
            return action();
        }
        catch (StorageAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (StorageConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Windows denied access to the selected item." }, statusCode: 403); }
    }

    private static async Task<IResult> GuardAsync(HttpContext ctx, Func<Task<IResult>> action)
    {
        try
        {
            StorageAccess.RequireAdministrator(ctx.User);
            return await action();
        }
        catch (StorageAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (StorageConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = "Windows denied access to the selected item." }, statusCode: 403); }
    }
}
