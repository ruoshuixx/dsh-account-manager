namespace AccountManager.Core.Accounts;

/// <summary>
/// The one program a switch writes to. Exactly one is selected at a time: a refresh token shared by several
/// programs is rejected (<c>refresh_token_reused</c>) as soon as one of them refreshes it.
/// </summary>
public enum SwitchTarget
{
    /// <summary>dsh's current Codex credential (<c>$DSH_HOME/.credentials.yaml</c>).</summary>
    Dsh,

    /// <summary><c>$CODEX_HOME/auth.json</c>, shared by the Codex CLI and the Codex desktop app.</summary>
    Codex,

    /// <summary>The active <c>openai</c> OAuth credential of OpenCode.</summary>
    OpenCode,
}

public static class SwitchTargetNames
{
    /// <summary>The name shown to the user ("dsh", "Codex", "OpenCode").</summary>
    public static string Display(this SwitchTarget target) => target switch
    {
        SwitchTarget.Codex => "Codex",
        SwitchTarget.OpenCode => "OpenCode",
        _ => "dsh",
    };
}
