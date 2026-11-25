namespace AccountManager.Core.Actions;

public sealed record ModelQueryResult(IReadOnlyList<string> Models, string Source);
