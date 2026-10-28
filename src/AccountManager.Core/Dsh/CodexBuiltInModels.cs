using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>
/// The Codex models this program knows by heart: used when OpenAI's list for the account cannot be fetched (a newly created route) and
/// as the last source of a model's facts. Facts are those of the pi-ai catalog installed with dsh 0.2.1-rc.2 (@earendil-works/pi-ai 1.3.0,
/// read 2026-10-25), except <c>gpt-6.1-terra</c>, which that catalog does not have yet: its numbers are what OpenAI's model list
/// returned on 2026-10-26 (text and image input). OpenAI's list has no maximum output tokens, so 128000 (the value every gpt-6 catalog
/// entry uses) is assumed; its reasoning levels are the common five, spelled as themselves. The context window of all four is 872000:
/// the live account catalog's <c>max_context_window</c> of 2026-10-28 (about 1M minus a 128000 output reserve; the backend refuses input
/// beyond roughly 920K, so never write more, and never 1,000,000).
/// </summary>
internal static class CodexBuiltInModels
{
    /// <summary>Output-token ceiling assumed for a model whose source has none.</summary>
    public const int DefaultMaxTokens = 128000;

    /// <summary>The context window written when OpenAI's list cannot be fetched (see the class comment).</summary>
    public const int DefaultContextWindow = 872000;

    internal sealed record Model(string Id, string Name, int ContextWindow, int MaxTokens, string[] Input, (string Level, string? Wire)[] Levels, bool InBundledCatalog);

    private static readonly (string, string?)[] Standard =
        { ("off", null), ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") };

    public static readonly IReadOnlyList<Model> All = new[]
    {
        new Model("gpt-6.1-sol", "GPT-6.1 Sol", DefaultContextWindow, DefaultMaxTokens, new[] { "text", "image" }, Standard, true),
        new Model("gpt-6.1-terra", "GPT-6.1 Terra", DefaultContextWindow, DefaultMaxTokens, new[] { "text", "image" },
            new (string, string?)[] { ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") }, false),
        new Model("gpt-6-astra", "GPT-6 Astra", DefaultContextWindow, DefaultMaxTokens, new[] { "text", "image" }, Standard, true),
        new Model("gpt-6-luna", "GPT-6 Luna", DefaultContextWindow, DefaultMaxTokens, new[] { "text", "image" },
            new (string, string?)[] { ("off", "none"), ("minimal", "low"), ("low", "low"), ("medium", "medium"), ("high", "high"), ("xhigh", "xhigh"), ("max", "max") }, true),
    };

    public static IReadOnlyList<string> Ids { get; } = All.Select(m => m.Id).ToList();

    /// <summary>Ids dsh 0.2.1-rc.2's bundled catalog knows (written id-only when its catalog cannot be read).</summary>
    public static IReadOnlyCollection<string> BundledCatalogIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna", "gpt-6-sol", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.3-codex-spark",
    };

    public static Model? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The route entry of a built-in model: id and context window when dsh's catalog has it (dsh fills the rest from its catalog),
    /// otherwise with explicit facts.
    /// </summary>
    public static JsonObject RouteEntry(Model model)
        => model.InBundledCatalog
            ? new JsonObject { ["id"] = model.Id, ["contextWindow"] = model.ContextWindow }
            : new JsonObject
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["contextWindow"] = model.ContextWindow,
                ["maxTokens"] = model.MaxTokens,
                ["input"] = new JsonArray(model.Input.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["reasoningEfforts"] = CodexFastService.EffortsFromLevels(model.Levels.ToDictionary(l => l.Level, l => l.Wire)),
            };
}
