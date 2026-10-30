using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>What the model list of an API provider should do with its Fast twins when saved.</summary>
public enum FastTwinMode
{
    /// <summary>No twins: existing ones are removed.</summary>
    Remove,

    /// <summary>Existing twins stay (when their base is still listed); none are created.</summary>
    Keep,

    /// <summary>Every qualifying base model gets a twin (an existing one is kept as it is).</summary>
    Create,
}

/// <summary>
/// Fast twins of API providers: on the official OpenAI endpoint with the Responses protocol, a GPT-6 family model <c>X</c> can have a twin
/// <c>X-fast</c> (name "... (Fast)", same tiers as <c>X</c>). The <c>dsh-account-manager-fast</c> plugin sends a twin's requests as <c>X</c>
/// with <c>service_tier: "priority"</c> (premium billing). Twins are managed with a tick in the dialogs and are not shown in the model list.
/// Everything here works on the provider "view" (<see cref="ProviderViewMapper"/>).
/// </summary>
public static class ApiFastTwins
{
    /// <summary>Said after the component was installed or updated: dsh reads its plugin list at start.</summary>
    public const string RestartNote = "已安装 / 更新 Fast 组件：需要完全退出 DSH 再打开后 Fast 模型才可用；Fast 按 OpenAI 优先处理的更高单价计费。";

    /// <summary>Whether a provider at this address and protocol can have twins at all.</summary>
    public static bool Qualifies(string? baseUrl, bool useResponses) => useResponses && ModelVariantBuilder.IsOpenAiOfficialEndpoint(baseUrl);

    public static string TwinId(string baseId) => baseId + CodexFastService.FastSuffix;

    private static bool HasOurShape(string id, JsonNode? entry, JsonObject models)
        => id.Length > CodexFastService.FastSuffix.Length
           && id.EndsWith(CodexFastService.FastSuffix, StringComparison.Ordinal)
           && models.ContainsKey(id[..^CodexFastService.FastSuffix.Length])
           && ModelVariantBuilder.IsGpt6Family(id[..^CodexFastService.FastSuffix.Length])
           && ProviderViewMapper.ReadString(entry, "name") is { } name
           && name.EndsWith(CodexFastService.FastNameSuffix, StringComparison.Ordinal);

    /// <summary>
    /// The twin ids in a view's <c>models</c>: ids <c>X-fast</c> with the name shape this program writes whose base <c>X</c> (GPT-6 family)
    /// is listed too, on a qualifying provider. A <c>-fast</c> id whose base is not listed is an ordinary model and is not reported.
    /// </summary>
    public static IReadOnlyList<string> FindTwins(JsonNode? models, string? baseUrl, bool useResponses)
    {
        if (!Qualifies(baseUrl, useResponses) || models is not JsonObject list) { return Array.Empty<string>(); }
        return list.Where(kv => HasOurShape(kv.Key, kv.Value, list)).Select(kv => kv.Key).ToList();
    }

    /// <summary>The twins of a raw dsh provider entry (<c>api</c>, <c>baseURL</c>, <c>models[]</c>); none for an entry this program does not manage.</summary>
    public static IReadOnlyList<string> FindTwinsInProvider(string providerId, JsonObject provider)
    {
        var view = ProviderViewMapper.ToView(providerId, provider);
        if (view is null) { return Array.Empty<string>(); }
        return FindTwins(view["models"], ProviderViewMapper.ReadString(view["settings"], "baseURL"), OpenAiApiProtocol.UsesResponses(ProviderViewMapper.ReadString(view, "package")));
    }

    /// <summary>
    /// The model list (view form) with the twins applied: <paramref name="merged"/> is the list the user wants (no twins), <paramref name="old"/>
    /// the list as stored. A twin is placed right after its base. An existing twin is kept as it is (only missing tiers are filled in);
    /// a new one is the base entry with " (Fast)" appended to the name, so it carries the same tiers and facts.
    /// </summary>
    public static JsonObject Apply(JsonObject merged, JsonNode? old, string? baseUrl, bool useResponses, FastTwinMode mode)
    {
        if (mode == FastTwinMode.Remove || !Qualifies(baseUrl, useResponses)) { return merged; }
        var result = new JsonObject();
        foreach (var (id, entry) in merged)
        {
            result[id] = entry?.DeepClone();
            if (!ModelVariantBuilder.IsGpt6Family(id)) { continue; }
            var twinId = TwinId(id);
            if (merged.ContainsKey(twinId)) { continue; }
            var oldTwin = old is JsonObject oldList && oldList[twinId] is JsonObject o && HasOurShape(twinId, o, oldList) ? o : null;
            if (oldTwin is not null)
            {
                var kept = (JsonObject)oldTwin.DeepClone();
                ModelVariantBuilder.TryAddMissingVariants(kept, twinId, useResponses, baseUrl);
                result[twinId] = kept;
            }
            else if (mode == FastTwinMode.Create && entry is JsonObject baseEntry)
            {
                var twin = (JsonObject)baseEntry.DeepClone();
                twin["name"] = (ProviderViewMapper.ReadString(baseEntry, "name") ?? id) + CodexFastService.FastNameSuffix;
                result[twinId] = twin;
            }
        }

        return result;
    }
}
