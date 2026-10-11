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
        });

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
        });

        group.MapPost("/browse", (BrowseRequest request, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(
                storage.Browse(request.NodeId, request.SnapshotId, request.Offset))));

        group.MapPost("/analyze", async (AnalyzeRequest request, HttpContext ctx,
            StorageService storage, [FromKeyedServices(PluginId)] IComputeJobs compute,
            CancellationToken ct) => await GuardAsync(ctx, async () =>
        {
            var evidence = storage.PrepareAnalysis(request.NodeId, request.SnapshotId);
            return Results.Ok(await StorageAnalyzer.AnalyzeAsync(evidence, compute, ct));
        }));

        group.MapPost("/cleanup/preview", (CleanupPreviewRequest request, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(
                storage.PreviewCleanup(request.NodeId, request.SnapshotId))));

        group.MapPost("/cleanup/quarantine", async (CleanupCommitRequest request, HttpContext ctx,
            StorageService storage, CancellationToken ct) =>
            await GuardAsync(ctx, async () => Results.Ok(await storage.QuarantineAsync(request.Token, ct))));

        group.MapGet("/quarantine", (HttpContext ctx, StorageService storage)
            => Guard(ctx, () => Results.Ok(new { items = storage.ListQuarantine() })));

        group.MapPost("/quarantine/{id}/restore", (string id, HttpContext ctx,
            StorageService storage) => Guard(ctx, () => Results.Ok(storage.Restore(id))));

        group.MapPost("/quarantine/{id}/purge", (string id, PurgeRequest request,
            HttpContext ctx, StorageService storage) =>
            Guard(ctx, () => Results.Ok(storage.Purge(id, request.Confirmation))));
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
