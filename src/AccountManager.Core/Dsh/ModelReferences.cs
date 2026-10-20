namespace AccountManager.Core.Dsh;

/// <summary>
/// One place in dsh's configuration that names an LLM route: a mapping with both a string <c>provider</c> and a
/// string <c>model</c>. (A <c>provider</c> without a <c>model</c>, such as the sub-agent mode <c>provider: spawn</c>,
/// is not a reference.)
/// </summary>
public sealed class ModelReference
{
    /// <summary>Id of the enclosing plugin entry (for example <c>subagent-model-selection-settings</c>).</summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>Human label, Chinese for the entries we know and the entry id otherwise.</summary>
    public string Label { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    /// <summary>True when the mapping is an item of a list (so it can be removed); false for a single value.</summary>
    public bool IsListItem { get; init; }

    /// <summary>True when the reference is in the user layer (<c>cordis.patch.yml</c>), the only layer this program edits.</summary>
    public bool Editable { get; init; }

    /// <summary>True when the reference can be removed (a list item in block style, in the user layer).</summary>
    public bool Removable { get; init; }

    /// <summary>The provider is not a route dsh composes.</summary>
    public bool Dangling { get; set; }

    /// <summary>Location in the user layer: map keys and list indexes from the root; empty for non-editable references.</summary>
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

    public string Key => EntryId + "\u0001" + string.Join("/", Path) + "\u0001" + Provider + "\u0001" + Model;

    public string Describe() => Label + " · " + Provider + "/" + Model;
}

/// <summary>The result of scanning dsh's configuration for model references.</summary>
public sealed class ReferenceScan
{
    public IReadOnlyList<ModelReference> References { get; init; } = Array.Empty<ModelReference>();

    /// <summary>Provider routes dsh composes (llm-pi-ai providers plus the built-in DeepSeek routes).</summary>
    public IReadOnlySet<string> KnownProviders { get; init; } = new HashSet<string>();

    /// <summary>False when dsh could not be run, so only the user layer was read.</summary>
    public bool ComposedAvailable { get; init; }

    /// <summary>True when the composed config holds an LLM adapter plugin this program does not know: no reference is called dangling then.</summary>
    public bool Uncertain { get; init; }

    public string Note { get; init; } = string.Empty;

    public IReadOnlyList<ModelReference> Dangling => References.Where(r => r.Dangling).ToList();

    public IReadOnlyList<ModelReference> ReferencesTo(string providerId, IReadOnlyCollection<string>? models = null)
        => References.Where(r => r.Provider == providerId && (models is null || models.Contains(r.Model))).ToList();
}

/// <summary>A provider offering models, as a replacement target.</summary>
public sealed record ProviderChoice(string ProviderId, string DisplayName, IReadOnlyList<string> Models);

public enum ReferenceActionKind { Retarget, Remove }

/// <summary>One edit of one reference.</summary>
public sealed record ReferenceAction(ModelReference Reference, ReferenceActionKind Kind, string? Provider = null, string? Model = null);

/// <summary>How the references to a provider being deleted (or losing models) are resolved.</summary>
public sealed class ReferenceResolution
{
    private ReferenceResolution() { }

    public ReferenceActionKind Kind { get; private init; }

    public string? Provider { get; private init; }

    public string? Model { get; private init; }

    /// <summary>Every editable reference now points at <paramref name="provider"/>/<paramref name="model"/>.</summary>
    public static ReferenceResolution ReplaceWith(string provider, string model)
        => new() { Kind = ReferenceActionKind.Retarget, Provider = provider, Model = model };

    /// <summary>List-item references are removed (single-value references cannot be).</summary>
    public static ReferenceResolution RemoveItems() => new() { Kind = ReferenceActionKind.Remove };
}

/// <summary>Raised instead of leaving dangling references: the caller must choose how to resolve them.</summary>
public sealed class ReferencesInUseException : DshException
{
    public ReferencesInUseException(string providerId, IReadOnlyList<ModelReference> references)
        : base("dsh 的设置仍在引用供应商 " + providerId + "（" + references.Count + " 处）；请先选择如何处理这些引用。")
    {
        ProviderId = providerId;
        References = references;
    }

    public string ProviderId { get; }

    public IReadOnlyList<ModelReference> References { get; }
}
