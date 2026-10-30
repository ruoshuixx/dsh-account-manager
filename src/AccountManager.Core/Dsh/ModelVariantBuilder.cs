using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Builds model catalog entries and, for the Responses protocol, synthesises <c>variants</c>
/// (reasoning tiers) only on two official endpoints: the Ark Coding/Agent Plan URLs for newly introduced models supporting
/// <c>reasoning.effort</c> (<see cref="ModelReasoningCatalog"/>), and the official OpenAI API for the GPT-6 family
/// (<see cref="OpenAiEfforts"/>). In the provider view a variant is <c>{ id, body.reasoning.effort }</c>; the dsh
/// storage form is the model's <c>reasoningEfforts</c> map (level to wire value). Existing entries are never modified and unknown models are never
/// guessed, so a model whose request body the endpoint would reject still gets only a name.
/// </summary>
public static class ModelVariantBuilder
{
    /// <summary>
    /// The tiers written for the GPT-6 family on the official OpenAI API (the user's explicit choice). <c>none</c> and <c>minimal</c> are left out
    /// on purpose: OpenAI documents that some of these models reject them.
    /// </summary>
    public static readonly IReadOnlyList<string> OpenAiEfforts = new[] { "low", "medium", "high", "xhigh", "max" };

    /// <summary>A brand-new model entry: <c>{ name }</c>, plus variants when applicable.</summary>
    public static JsonObject NewModelEntry(string modelId, bool useResponses, string? baseUrl)
    {
        var entry = new JsonObject { ["name"] = modelId };
        TryAddMissingVariants(entry, modelId, useResponses, baseUrl);
        return entry;
    }

    /// <summary>
    /// Adds variants in place only when the entry has none and the model is documented for the
    /// Responses protocol (Ark plan endpoints), or belongs to the GPT-6 family on the official OpenAI endpoint.
    /// Returns whether the entry changed.
    /// </summary>
    public static bool TryAddMissingVariants(JsonObject entry, string modelId, bool useResponses, string? baseUrl)
    {
        if (!useResponses || entry.ContainsKey("variants")) { return false; }
        if (IsArkPlanEndpoint(baseUrl))
        {
            if (!ModelReasoningCatalog.TryGetEffortValues(modelId, out var effortValues)) { return false; }
            entry["variants"] = BuildEffortVariants(effortValues);
            return true;
        }

        if (IsOpenAiOfficialEndpoint(baseUrl) && IsGpt6Family(modelId))
        {
            entry["variants"] = BuildEffortVariants(OpenAiEfforts);
            return true;
        }

        return false;
    }

    /// <summary>The official OpenAI API: https, host <c>api.openai.com</c>, default port, path <c>/v1</c>, no userinfo, query or fragment.</summary>
    public static bool IsOpenAiOfficialEndpoint(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GPT-6 family ids: <c>gpt-6</c> followed by the end or <c>-</c> / <c>.</c> (gpt-6-astra, gpt-6.1-sol, gpt-6.2-x); case-insensitive.</summary>
    public static bool IsGpt6Family(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) { return false; }
        var id = modelId.Trim();
        const string prefix = "gpt-6";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return false; }
        return id.Length == prefix.Length || id[prefix.Length] is '-' or '.';
    }

    /// <summary>Only the official Ark Coding Plan and Agent Plan endpoints qualify.</summary>
    public static bool IsArkPlanEndpoint(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Host, "ark.cn-beijing.volces.com", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/coding/v3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/plan/v3", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Responses variant shape: <c>{ id, body: { reasoning: { effort } } }</c>.</summary>
    public static JsonArray BuildEffortVariants(IReadOnlyList<string> effortValues)
    {
        var variants = new JsonArray();
        foreach (var value in effortValues)
        {
            variants.Add(new JsonObject
            {
                ["id"] = value,
                ["body"] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = value } },
            });
        }

        return variants;
    }
}
