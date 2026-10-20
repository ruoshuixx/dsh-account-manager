namespace AccountManager.Core.Dsh;

/// <summary>
/// The two wire protocols the manager edits for API providers. The value doubles as the provider's
/// <c>api</c> field in dsh's <c>llm-pi-ai</c> configuration, and as the "package" of the
/// provider view the upper layers work with.
/// </summary>
public static class OpenAiApiProtocol
{
    public const string ChatPackage = "openai-completions";
    public const string ResponsesPackage = "openai-responses";

    public static string PackageFor(bool useResponses) => useResponses ? ResponsesPackage : ChatPackage;

    public static bool UsesResponses(string? package) => Equal(package, ResponsesPackage);

    public static bool IsManagedPackage(string? package) => Equal(package, ChatPackage) || UsesResponses(package);

    /// <summary>Cockpit's <c>api_wire_api</c> spelling of the protocol, or null when unmanaged.</summary>
    public static string? WireApiFor(string? package)
        => UsesResponses(package) ? "responses" : Equal(package, ChatPackage) ? "chat_completions" : null;

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.Ordinal);
}
