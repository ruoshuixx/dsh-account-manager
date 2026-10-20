using AccountManager.Core.Dsh;
using AccountManager.Core.Models;
using AccountManager.Core.Quota;

namespace AccountManager.App.Services;

/// <summary>Input for the API provider editor dialog.</summary>
public sealed class ApiConfigInput
{
    public string ProviderId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    public bool UseResponses { get; init; }

    /// <summary>True when a local key credential exists (blank key preserves it).</summary>
    public bool HasCredential { get; init; }
}

/// <summary>Input for the settings dialog.</summary>
public sealed class SettingsInput
{
    /// <summary>DSH_HOME override; empty = automatic.</summary>
    public string DshHome { get; init; } = string.Empty;

    /// <summary>Configuration profile override; empty = automatic.</summary>
    public string DshProfile { get; init; } = string.Empty;

    /// <summary>Profiles found under the resolved home, shown as a hint.</summary>
    public IReadOnlyList<string> DetectedProfiles { get; init; } = Array.Empty<string>();

    public int AutoRefreshMinutes { get; init; } = 5;

    /// <summary>Automatically switch accounts when the active one runs out of quota.</summary>
    public bool AutoSwitchAccount { get; init; }

    // --- WebDAV sync ---
    public string WebDavUrl { get; init; } = string.Empty;

    public string WebDavUsername { get; init; } = string.Empty;

    /// <summary>True when a password is already stored, so the dialog can hint "leave blank to keep".</summary>
    public bool WebDavPasswordStored { get; init; }
}

/// <summary>Optional action chosen from the settings dialog's sync buttons.</summary>
public enum SettingsAction
{
    None,
    Upload,
    Download,
}

/// <summary>Result of the settings dialog. Null when cancelled.</summary>
public sealed class SettingsResult
{
    public string DshHome { get; init; } = string.Empty;

    public string DshProfile { get; init; } = string.Empty;

    public int AutoRefreshMinutes { get; init; } = 5;

    public bool AutoSwitchAccount { get; init; }

    // --- WebDAV sync ---
    public string WebDavUrl { get; init; } = string.Empty;

    public string WebDavUsername { get; init; } = string.Empty;

    /// <summary>New app password; null/empty keeps the stored one.</summary>
    public string? WebDavPassword { get; init; }

    /// <summary>When set, the view model runs this sync action after applying the settings.</summary>
    public SettingsAction Action { get; init; } = SettingsAction.None;
}

/// <summary>Result of the API provider editor dialog. Null when cancelled.</summary>
public sealed class ApiConfigResult
{
    public string DisplayName { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public List<string> Models { get; init; } = new();

    public bool UseResponses { get; init; }

    /// <summary>New key, or null/empty to keep the current one.</summary>
    public string? NewKey { get; init; }
}

/// <summary>Input for the dialog shown before deleting a provider (or models) that dsh's settings still refer to.</summary>
public sealed class ReferenceGuardInput
{
    public string Title { get; init; } = string.Empty;

    /// <summary>One sentence naming what is about to disappear.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Every reference, editable or not.</summary>
    public IReadOnlyList<ModelReference> References { get; init; } = Array.Empty<ModelReference>();

    /// <summary>Providers (other than the one going away) that can take over, with the models they offer.</summary>
    public IReadOnlyList<ProviderChoice> Choices { get; init; } = Array.Empty<ProviderChoice>();

    /// <summary>False when a reference is a single value, which cannot be removed (only replaced).</summary>
    public bool CanRemoveItems { get; init; }

    /// <summary>Label of the primary action, for example "改用其他供应商后删除".</summary>
    public string ReplaceText { get; init; } = "改用其他供应商后删除";

    public string RemoveText { get; init; } = "连同引用一起删除";
}

/// <summary>How the user wants the references resolved; null result = cancelled.</summary>
public sealed class ReferenceGuardResult
{
    public ReferenceResolution Resolution { get; init; } = null!;
}

public enum RepairKind { Skip, Retarget, Restore, Remove }

/// <summary>One user decision in the repair dialog.</summary>
public sealed class RepairChoice
{
    public ModelReference Reference { get; init; } = null!;

    public RepairKind Kind { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    /// <summary>For <see cref="RepairKind.Restore"/>: the backup to restore <see cref="ModelReference.Provider"/> from.</summary>
    public string? BackupPath { get; init; }
}

/// <summary>Input for the repair dialog opened from the health-check chip.</summary>
public sealed class ReferenceRepairInput
{
    public IReadOnlyList<ModelReference> References { get; init; } = Array.Empty<ModelReference>();

    public IReadOnlyList<ProviderChoice> Choices { get; init; } = Array.Empty<ProviderChoice>();

    /// <summary>Provider id to the newest backup that contains it.</summary>
    public IReadOnlyDictionary<string, string> Backups { get; init; } = new Dictionary<string, string>();

    /// <summary>Why the check may be incomplete (empty when it is complete).</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>Input for the reset-credits confirmation dialog.</summary>
public sealed class ResetCreditsInput
{
    public string AccountTitle { get; init; } = string.Empty;

    public ResetCreditList List { get; init; } = new();

    /// <summary>The credit that will be consumed (earliest-expiring available), or null when none.</summary>
    public ResetCredit? Target { get; init; }
}

/// <summary>
/// All user-facing dialogs the view models need. Implemented by the view layer so the
/// view models stay free of Avalonia window types.
/// </summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, bool dangerous = false);

    Task ShowInfoAsync(string title, string message);

    Task ShowWarningAsync(string title, string message);

    Task<string?> PickOpenFileAsync(string title, string suggestedName);

    Task<string?> PickSaveFileAsync(string title, string suggestedName);

    Task<ApiConfigResult?> ShowApiConfigAsync(ApiConfigInput input);

    Task<AccountAddResult?> ShowAddAccountAsync(AppServices services);

    /// <summary>Shows the reset-credits list and returns true when the user confirms redeeming <see cref="ResetCreditsInput.Target"/>.</summary>
    Task<bool> ShowResetCreditsAsync(ResetCreditsInput input);

    Task<SettingsResult?> ShowSettingsAsync(SettingsInput input);

    /// <summary>Lists the references to something about to be deleted and returns how to resolve them (null = cancel).</summary>
    Task<ReferenceGuardResult?> ShowReferenceGuardAsync(ReferenceGuardInput input);

    /// <summary>Lets the user choose a fix per dangling reference; null = cancelled.</summary>
    Task<IReadOnlyList<RepairChoice>?> ShowReferenceRepairAsync(ReferenceRepairInput input);
}
