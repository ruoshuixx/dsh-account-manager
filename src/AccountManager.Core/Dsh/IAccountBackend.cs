using System.Text.Json.Nodes;

namespace AccountManager.Core.Dsh;

/// <summary>The provider configuration file as it is on disk, for backups and diagnostics.</summary>
public sealed record ConfigSnapshot(string Path, string? Raw);

public enum ProviderRuntimeState { Applied, Pending, Unavailable }

public sealed record ProviderRuntimeVerification(ProviderRuntimeState State, string Message);

/// <summary>Asks dsh itself whether it composes the provider the way it was written.</summary>
public interface IProviderRuntimeVerifier
{
    Task<ProviderRuntimeVerification> VerifyAsync(DshHome home, string providerId, string expectedApi, CancellationToken ct = default);
}

/// <summary>Reads dsh's composed configuration (all plugin layers), when dsh can be run.</summary>
public interface IDshConfigDumper
{
    /// <summary>The <c>--dump-config</c> YAML text, or null when dsh is unavailable or fails.</summary>
    Task<string?> DumpConfigAsync(DshHome home, CancellationToken ct = default);
}

/// <summary>
/// The account/credential boundary every upper-layer service works against; replaceable in tests so no real
/// dsh home is touched. <see cref="DshBackend"/> is the production implementation. Credentials keep the
/// JSON shape of the original manager (<c>id</c>, <c>integrationID</c>, <c>label</c>, <c>active</c>,
/// <c>value</c>), Codex accounts being <c>integrationID: "openai"</c> OAuth credentials and API providers
/// with a stored key being <c>type: "key"</c> credentials of their provider id.
/// </summary>
public interface IAccountBackend
{
    Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default);

    /// <summary>One <c>{ type: "file", path, info: { providers } }</c> document with the provider views.</summary>
    Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default);

    Task<ConfigSnapshot> GetConfigSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a Codex account; <paramref name="activate"/> also makes it dsh's current Codex account.</summary>
    Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null);

    Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default);

    Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces one API provider from its view and optionally stores its key.
    /// <paramref name="newKey"/> null keeps the existing key reference untouched.
    /// </summary>
    Task WriteApiProviderAsync(string providerId, JsonObject view, string? newKey, CancellationToken cancellationToken = default);

    /// <summary>Removes the provider entry and the key reference it owned.</summary>
    Task RemoveApiProviderAsync(string providerId, CancellationToken cancellationToken = default);

    Task<ProviderRuntimeVerification> VerifyProviderAsync(string providerId, string expectedApi, CancellationToken cancellationToken = default);
}
