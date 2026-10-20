using YamlDotNet.RepresentationModel;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Finds every LLM model reference (a mapping with a string <c>provider</c> and a string <c>model</c>) in dsh's
/// configuration and tells which ones name a provider dsh does not compose.
/// <para>
/// Which providers dsh composes: <c>llm-pi-ai</c> is mounted dormant and registers exactly one route per key of its
/// <c>config.providers</c> (dsh-llm-pi-ai: <c>routes = [...profiles().keys()]</c>), so the catalog ids such as
/// <c>openai-codex</c> exist only when the user layer configures them. The only other adapters in the shipped
/// bundles are <c>dsh-llm-deepseek-api-key</c> (route <c>deepseek-official</c>) and <c>dsh-llm-deepseek-account</c>
/// (route <c>deepseek-account</c>). A composed config that mounts a third-party plugin or any other LLM adapter makes the answer
/// uncertain, and then nothing is reported as dangling.
/// </para>
/// </summary>
public sealed class ReferenceScanner
{
    public const string PiAiPackage = "@deepseek-ai/dsh-llm-pi-ai";
    public const string DeepSeekApiKeyPackage = "@deepseek-ai/dsh-llm-deepseek-api-key";
    public const string DeepSeekAccountPackage = "@deepseek-ai/dsh-llm-deepseek-account";

    /// <summary>Routes of the built-in DeepSeek adapters.</summary>
    public static readonly IReadOnlyList<string> BuiltInRoutes = new[] { "deepseek-official", "deepseek-account" };

    // Packages that look like LLM plugins but register no route (libraries, wrappers and extension points).
    private static readonly HashSet<string> NonRouteLlmPackages = new(StringComparer.Ordinal)
    {
        "@deepseek-ai/dsh-llm",
        "@deepseek-ai/dsh-llm-retry",
        "@deepseek-ai/dsh-llm-deepseek",
        "@deepseek-ai/dsh-deepseek-llm-api-extensions",
        "@deepseek-ai/dsh-client-ui-model-selection",
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["subagent-model-selection-settings"] = "子代理模型选择",
        ["agent-default-model"] = "默认模型",
    };

    private readonly IDshConfigDumper? _dumper;

    public ReferenceScanner(IDshConfigDumper? dumper = null) => _dumper = dumper;

    public static string LabelFor(string entryId)
        => string.IsNullOrEmpty(entryId) ? "（未命名条目）" : Labels.TryGetValue(entryId, out var label) ? label : entryId;

    /// <param name="oursKnown">
    /// Provider ids this program knows by its own records (deleted by it, recorded in its mirror, or present in its
    /// earlier backups of the provider file). A reference to one of them that dsh does not compose is dangling even when
    /// an unrecognised adapter plugin makes the general answer uncertain.
    /// </param>
    public async Task<ReferenceScan> ScanAsync(DshHome home, CancellationToken ct = default, IReadOnlySet<string>? oursKnown = null)
    {
        var patch = ProfilePatchDocument.Load(home.PatchPath);
        var userRefs = ScanUserLayer(patch.Text);
        var known = new HashSet<string>(BuiltInRoutes, StringComparer.Ordinal);
        foreach (var id in patch.GetProviders()?.Select(kv => kv.Key) ?? Enumerable.Empty<string>()) { known.Add(id); }

        string? dump = null;
        if (_dumper is not null) { dump = await _dumper.DumpConfigAsync(home, ct).ConfigureAwait(false); }

        var references = new List<ModelReference>(userRefs);
        var uncertain = false;
        var note = string.Empty;
        var composed = dump is not null;
        if (dump is not null)
        {
            try
            {
                var facts = ScanComposed(dump);
                foreach (var provider in facts.Providers) { known.Add(provider); }
                uncertain = facts.UnknownAdapters;
                foreach (var r in facts.References)
                {
                    if (!references.Any(u => u.EntryId == r.EntryId && u.Provider == r.Provider && u.Model == r.Model)) { references.Add(r); }
                }
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
            {
                composed = false;
                note = "无法解析 dsh 的合成配置，只检查了用户配置；可能有未检查到的引用。";
            }
        }
        else
        {
            note = "没有找到可运行的 dsh，只检查了用户配置（cordis.patch.yml）；可能有未检查到的引用。";
        }

        foreach (var r in references) { r.Dangling = !known.Contains(r.Provider) && (!uncertain || oursKnown?.Contains(r.Provider) == true); }
        if (uncertain) { note = "dsh 配置里有第三方插件或本工具不认识的 LLM 适配器（它们可能注册了本工具不知道的路由），所以只报告本工具自己记录过的供应商（删除过的、导入过的）失效的引用，其它引用不判断。"; }
        return new ReferenceScan { References = references, KnownProviders = known, ComposedAvailable = composed, Uncertain = uncertain, Note = note };
    }

    // ---- user layer (exact source positions) ----

    /// <summary>All references of the user layer, with their locations; they are editable.</summary>
    public static List<ModelReference> ScanUserLayer(string patchText)
    {
        var found = new List<ModelReference>();
        var root = YamlTree.Parse(patchText, "profile patch");
        if (root is null) { return found; }
        WalkUser(root, new List<string>(), string.Empty, false, true, found);
        return found;
    }

    private static void WalkUser(YNode node, List<string> path, string entryId, bool parentIsSeq, bool ancestorsBlock, List<ModelReference> into)
    {
        if (node.Kind == YKind.Map)
        {
            var id = Text(node, "id");
            if (parentIsSeq && id is not null) { entryId = id; }
            var provider = Text(node, "provider");
            var model = Text(node, "model");
            if (provider is { Length: > 0 } && model is { Length: > 0 })
            {
                into.Add(new ModelReference
                {
                    EntryId = entryId,
                    Label = LabelFor(entryId),
                    Provider = provider,
                    Model = model,
                    IsListItem = parentIsSeq,
                    Editable = true,
                    Removable = parentIsSeq && ancestorsBlock,
                    Path = path.ToArray(),
                });
            }

            foreach (var e in node.Entries)
            {
                path.Add(e.Key.Value);
                WalkUser(e.Value, path, entryId, false, ancestorsBlock && !node.Flow, into);
                path.RemoveAt(path.Count - 1);
            }
        }
        else if (node.Kind == YKind.Seq)
        {
            for (var i = 0; i < node.Items.Count; i++)
            {
                path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WalkUser(node.Items[i], path, entryId, true, ancestorsBlock && !node.Flow, into);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private static string? Text(YNode map, string key)
    {
        var entry = map.Entries.FirstOrDefault(e => e.Key.Value == key);
        return entry is not null && YamlTree.IsString(entry.Value) ? entry.Value.Value : null;
    }

    // ---- composed config (--dump-config output; custom tags such as !!js are allowed) ----

    internal sealed record ComposedFacts(List<ModelReference> References, HashSet<string> Providers, bool UnknownAdapters);

    internal static ComposedFacts ScanComposed(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var references = new List<ModelReference>();
        var providers = new HashSet<string>(StringComparer.Ordinal);
        var unknown = false;
        foreach (var doc in stream.Documents) { WalkComposed(doc.RootNode, string.Empty, false, references, providers, ref unknown); }
        return new ComposedFacts(references, providers, unknown);
    }

    private static void WalkComposed(YamlNode node, string entryId, bool parentIsSeq, List<ModelReference> refs, HashSet<string> providers, ref bool unknown)
    {
        if (node is YamlMappingNode map)
        {
            var id = Scalar(map, "id");
            if (parentIsSeq && id is not null) { entryId = id; }
            var name = Scalar(map, "name");
            if (name is not null)
            {
                if (name == PiAiPackage)
                {
                    if (Child(map, "config") is YamlMappingNode config && Child(config, "providers") is YamlMappingNode providerMap)
                    {
                        foreach (var key in providerMap.Children.Keys.OfType<YamlScalarNode>()) { if (key.Value is not null) { providers.Add(key.Value); } }
                    }
                }
                else if (name == DeepSeekApiKeyPackage) { providers.Add("deepseek-official"); }
                else if (name == DeepSeekAccountPackage) { providers.Add("deepseek-account"); }
                else if (IsUnknownAdapter(name)) { unknown = true; }
            }

            var provider = Scalar(map, "provider");
            var model = Scalar(map, "model");
            if (provider is { Length: > 0 } && model is { Length: > 0 })
            {
                refs.Add(new ModelReference { EntryId = entryId, Label = LabelFor(entryId), Provider = provider, Model = model, IsListItem = parentIsSeq });
            }

            foreach (var (_, value) in map.Children) { WalkComposed(value, entryId, false, refs, providers, ref unknown); }
        }
        else if (node is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children) { WalkComposed(item, entryId, true, refs, providers, ref unknown); }
        }
    }

    private static bool IsUnknownAdapter(string package)
    {
        if (NonRouteLlmPackages.Contains(package)) { return false; }
        // Unknown adapters inside dsh's own scope, and any third-party plugin: a plugin such as
        // @goodandready/dsh-subscriptions can register LLM routes under an id this program cannot enumerate.
        return package.StartsWith("@deepseek-ai/dsh-llm-", StringComparison.Ordinal)
            || !package.StartsWith("@deepseek-ai/", StringComparison.Ordinal) && !package.StartsWith("cordis:", StringComparison.Ordinal);
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var (k, v) in map.Children) { if (k is YamlScalarNode s && s.Value == key) { return v; } }
        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key)
        => Child(map, key) is YamlScalarNode { Value: { } v } ? v : null;
}
