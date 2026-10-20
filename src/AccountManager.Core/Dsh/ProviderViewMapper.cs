using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Translates between dsh's <c>llm-pi-ai</c> provider entries (<c>displayName</c>, <c>api</c>, <c>baseURL</c>,
/// <c>models[]</c> with <c>reasoningEfforts</c>) and the provider "view" the upper layers (mapper, import,
/// export, editing) work with: <c>{ name, package, settings.baseURL, models: { id: { name, variants } } }</c>.
/// Everything the view has no slot for (<c>apiKeyEnv</c>, headers, context sizes, compat switches, ...)
/// travels in an <c>x-dsh</c> object and is written back unchanged, so an edit never drops fields dsh knows.
/// </summary>
public static class ProviderViewMapper
{
    public const string ExtraKey = "x-dsh";

    /// <summary>The built-in Codex route; this program never edits or lists it as an API provider.</summary>
    public const string CodexRouteId = "openai-codex";

    private static readonly string[] ThinkingLevels = { "off", "minimal", "low", "medium", "high", "xhigh", "max" };

    private static readonly HashSet<string> ProviderMapped = new(StringComparer.Ordinal) { "displayName", "api", "baseURL", "models" };

    private static readonly HashSet<string> ModelMapped = new(StringComparer.Ordinal) { "id", "name" };

    /// <summary>Whether an entry is an OpenAI-compatible API provider this program manages.</summary>
    public static bool IsManaged(string providerId, JsonObject provider)
    {
        if (string.Equals(providerId, CodexRouteId, StringComparison.Ordinal)) { return false; }
        return provider["api"] is JsonValue api && api.TryGetValue<string>(out var apiText) && OpenAiApiProtocol.IsManagedPackage(apiText)
            && provider["baseURL"] is JsonValue url && url.TryGetValue<string>(out var urlText) && !string.IsNullOrWhiteSpace(urlText);
    }

    /// <summary>The view of a managed provider, or null when the entry is not one.</summary>
    public static JsonObject? ToView(string providerId, JsonObject provider)
    {
        if (!IsManaged(providerId, provider)) { return null; }

        var view = new JsonObject
        {
            ["name"] = ReadString(provider, "displayName") ?? providerId,
            ["package"] = ReadString(provider, "api"),
            ["settings"] = new JsonObject { ["baseURL"] = ReadString(provider, "baseURL") },
        };

        var models = new JsonObject();
        if (provider["models"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var id = ReadString(item, "id");
                if (string.IsNullOrEmpty(id) || models.ContainsKey(id)) { continue; }
                models[id] = ModelToView(id, item);
            }
        }

        view["models"] = models;

        var extras = new JsonObject();
        foreach (var (key, value) in provider)
        {
            if (!ProviderMapped.Contains(key)) { extras[key] = value?.DeepClone(); }
        }

        if (extras.Count > 0) { view[ExtraKey] = extras; }
        return view;
    }

    private static JsonObject ModelToView(string id, JsonObject model)
    {
        var view = new JsonObject { ["name"] = ReadString(model, "name") ?? id };
        var extras = new JsonObject();
        foreach (var (key, value) in model)
        {
            if (ModelMapped.Contains(key)) { continue; }
            if (key == "reasoningEfforts" && value is JsonObject efforts)
            {
                var variants = new JsonArray();
                foreach (var (level, wire) in efforts)
                {
                    var variant = new JsonObject { ["id"] = level };
                    if (wire is JsonValue w && w.TryGetValue<string>(out var wireText))
                    {
                        variant["body"] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = wireText } };
                    }

                    variants.Add(variant);
                }

                view["variants"] = variants;
                continue;
            }

            extras[key] = value?.DeepClone();
        }

        if (extras.Count > 0) { view[ExtraKey] = extras; }
        return view;
    }

    /// <summary>
    /// Builds the dsh provider entry from a view. <paramref name="apiKeyEnv"/>, when given, replaces any
    /// <c>apiKeyEnv</c> carried in the view's extras.
    /// </summary>
    public static JsonObject FromView(JsonObject view, string? apiKeyEnv = null)
    {
        var provider = new JsonObject();
        provider["displayName"] = ReadString(view, "name");
        var extras = view[ExtraKey] as JsonObject;
        var env = apiKeyEnv ?? ReadString(extras, "apiKeyEnv");
        if (!string.IsNullOrEmpty(env)) { provider["apiKeyEnv"] = env; }
        provider["api"] = ReadString(view, "package");
        provider["baseURL"] = ReadString(view["settings"], "baseURL");

        var models = new JsonArray();
        if (view["models"] is JsonObject modelObject)
        {
            foreach (var (id, node) in modelObject)
            {
                models.Add(ModelFromView(id, node as JsonObject ?? new JsonObject()));
            }
        }

        if (models.Count > 0) { provider["models"] = models; }

        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                if (key == "apiKeyEnv" || ProviderMapped.Contains(key)) { continue; }
                provider[key] = value?.DeepClone();
            }
        }

        return provider;
    }

    private static JsonObject ModelFromView(string id, JsonObject view)
    {
        var model = new JsonObject { ["id"] = id };
        var name = ReadString(view, "name");
        // The view shows the id when a model has no name; do not turn that into an explicit name.
        if (!string.IsNullOrEmpty(name) && name != id) { model["name"] = name; }
        var extras = view[ExtraKey] as JsonObject;
        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                if (key != "reasoningEfforts" || view["variants"] is null) { model[key] = value?.DeepClone(); }
            }
        }

        if (view["variants"] is JsonArray variants)
        {
            var efforts = new JsonObject();
            foreach (var variant in variants.OfType<JsonObject>())
            {
                var level = ReadString(variant, "id");
                if (level is null || !ThinkingLevels.Contains(level, StringComparer.Ordinal)) { continue; }
                var wire = ReadString(variant["body"]?["reasoning"], "effort");
                if (wire is not null) { efforts[level] = wire; }
                else if (level == "off") { efforts[level] = null; }
            }

            if (efforts.Count > 0) { model["reasoningEfforts"] = efforts; }
        }

        return model;
    }

    /// <summary>Provider ids the dsh credential store and refs can address safely (lowercase hyphenated).</summary>
    public static string? ReadString(JsonNode? node, string name)
        => node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
