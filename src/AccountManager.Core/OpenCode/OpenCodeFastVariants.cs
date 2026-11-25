using System.Text.Json.Nodes;

namespace AccountManager.Core.OpenCode;

/// <summary>
/// Fast for the GPT-6 models of OpenCode's built-in <c>openai</c> provider (the ChatGPT-plan sign-in), written into <c>opencode.json</c>
/// so it does not depend on OpenCode being able to download the models.dev catalog (which is where its own speed options come from).
/// For each model, the variants <c>low-fast</c> ... <c>max-fast</c> are added: the same settings as OpenCode's own variant of that effort plus
/// <c>body.service_tier: "priority"</c> (what Codex sends for Fast, and what models.dev lists as the <c>fast</c> mode of these models).
/// Variants merge onto the built-in provider and model by id; a variant with an id already in the file is left exactly as it is.
/// </summary>
public static class OpenCodeFastVariants
{
    public const string ProviderId = "openai";
    public const string Suffix = "-fast";

    /// <summary>OpenCode V2 reads the top-level <c>providers</c> object; older versions use another shape.</summary>
    public static readonly Version MinimumOpenCodeVersion = new(2, 0, 0);

    /// <summary>The GPT-6 models models.dev lists a <c>fast</c> (priority) mode for (read 2026-11-26).</summary>
    public static readonly IReadOnlyList<string> Models = new[]
    {
        "gpt-6.2-astra", "gpt-6.2-sol", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-luna",
    };

    /// <summary>The effort tiers OpenCode's own catalog lists for these models (read from OpenCode 2.4.1).</summary>
    public static readonly IReadOnlyList<string> Efforts = new[] { "low", "medium", "high", "xhigh", "max" };

    public static string VariantId(string effort) => effort + Suffix;

    /// <summary>
    /// Adds the missing Fast variants to <c>providers.openai.models.&lt;id&gt;.variants</c> of <paramref name="config"/>.
    /// Returns whether anything was added; nothing is touched when the file's shape is not the expected one.
    /// </summary>
    public static bool Apply(JsonObject config)
    {
        if (config["providers"] is not JsonObject providers) { return false; }
        var provider = providers[ProviderId];
        if (provider is not null and not JsonObject) { return false; }
        var providerObject = provider as JsonObject;
        var models = providerObject?["models"];
        if (models is not null and not JsonObject) { return false; }

        var changed = false;
        foreach (var model in Models)
        {
            var current = (models as JsonObject)?[model];
            if (current is not null and not JsonObject) { continue; }
            var currentObject = current as JsonObject;
            var existing = currentObject?["variants"];
            if (existing is not null and not JsonArray) { continue; }

            var wanted = Efforts.Select(e => (Id: VariantId(e), Effort: e)).ToList();
            var have = (existing as JsonArray)?.OfType<JsonObject>()
                .Select(v => v["id"] is JsonValue value && value.TryGetValue<string>(out var id) ? id : null)
                .Where(id => id is not null).ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string?>(StringComparer.Ordinal);
            var missing = wanted.Where(w => !have.Contains(w.Id)).ToList();
            if (missing.Count == 0) { continue; }

            providerObject ??= new JsonObject();
            providers[ProviderId] = providerObject;
            var modelsObject = providerObject["models"] as JsonObject ?? new JsonObject();
            providerObject["models"] = modelsObject;
            var modelObject = modelsObject[model] as JsonObject ?? new JsonObject();
            modelsObject[model] = modelObject;
            var array = modelObject["variants"] as JsonArray ?? new JsonArray();
            modelObject["variants"] = array;
            foreach (var (id, effort) in missing)
            {
                array.Add(new JsonObject
                {
                    ["id"] = id,
                    // The settings are the ones OpenCode's own variant of this effort carries; only the service tier is added.
                    ["settings"] = new JsonObject
                    {
                        ["reasoningEffort"] = effort,
                        ["reasoningSummary"] = "auto",
                        ["include"] = new JsonArray("reasoning.encrypted_content"),
                    },
                    ["body"] = new JsonObject { ["service_tier"] = "priority" },
                });
            }

            models = modelsObject;
            changed = true;
        }

        return changed;
    }
}
