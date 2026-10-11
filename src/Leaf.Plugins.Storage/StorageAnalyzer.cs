using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Storage;

internal static class StorageAnalyzer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<StorageAnalysis> AnalyzeAsync(StorageAnalysisEvidence evidence,
        IComputeJobs compute, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["mode"] = "oneshot",
            ["qualityTier"] = "standard",
            ["maxTokens"] = 900,
            ["timeout"] = 120,
            ["system"] = """
                You assess storage items from bounded filesystem metadata. Never claim that you read file contents.
                Explain what the item probably contains and is used for, what would be lost if it were removed, and
                recommend exactly one of keep, quarantine, or inspect. Treat names as evidence, not certainty. A
                quarantine recommendation means reversible quarantine, never permanent deletion. Return only a JSON
                object with keys summary (string), contents (array of 2-6 short strings), deletionImpact (string),
                recommendation (keep|quarantine|inspect), reasoning (string), and confidence (number from 0 to 1).
                The answer is advisory and grants no authority to change files.
                """,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Assess this selected storage item. Metadata only:\n"
                    + JsonSerializer.Serialize(evidence, Json),
            }),
        };
        var job = await compute.SubmitAsync("ai-session", payload, new ComputeJobOptions
        {
            Async = false,
            JobName = "Storage item analysis",
            Rationale = "Explain a user-selected storage item and recommend a safe next action",
            IdempotencyKey = $"storage-analysis:{evidence.SnapshotId}:{evidence.NodeId}",
        }, ct).ConfigureAwait(false);
        var result = job.InlineResult
            ?? throw new ComputeJobException("Storage analysis returned no response.");
        var text = String(result, "text");
        if (string.IsNullOrWhiteSpace(text))
            throw new ComputeJobException("Storage analysis returned empty text.");
        var parsed = ParseObject(text)
            ?? throw new ComputeJobException("Storage analysis returned an invalid structured response.");
        var recommendation = String(parsed, "recommendation")?.Trim().ToLowerInvariant();
        if (recommendation is not ("keep" or "quarantine" or "inspect")) recommendation = "inspect";
        var contents = parsed["contents"] is JsonArray array
            ? array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim()).Take(6).ToList()
            : [];
        var confidence = parsed["confidence"]?.GetValue<double>() ?? 0.5;
        return new StorageAnalysis(
            Required(parsed, "summary"),
            contents,
            Required(parsed, "deletionImpact"),
            recommendation,
            Required(parsed, "reasoning"),
            Math.Clamp(confidence, 0, 1),
            String(result, "model"),
            false);
    }

    private static JsonObject? ParseObject(string text)
    {
        var value = text.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = value.IndexOf('\n');
            var closing = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && closing > firstNewline)
                value = value[(firstNewline + 1)..closing].Trim();
        }
        try { return JsonNode.Parse(value) as JsonObject; }
        catch (JsonException)
        {
            var start = value.IndexOf('{');
            var end = value.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try { return JsonNode.Parse(value[start..(end + 1)]) as JsonObject; }
            catch (JsonException) { return null; }
        }
    }

    private static string Required(JsonObject value, string name)
        => String(value, name)?.Trim() is { Length: > 0 } result ? result
            : throw new ComputeJobException($"Storage analysis omitted {name}.");

    private static string? String(JsonObject value, string name)
        => value[name] is JsonValue item && item.TryGetValue<string>(out var result) ? result : null;
}
