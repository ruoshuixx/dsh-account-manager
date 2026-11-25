using System.Text.RegularExpressions;

namespace AccountManager.Core.Actions;

/// <summary>/models has no family or capability fields. Recognize stable, general GPT aliases by ID.</summary>
public static class OpenAiModelFilter
{
    private static readonly Regex GeneralAlias = new(
        @"\Agpt-(?<version>[0-9]+(?:\.[0-9]+){0,2})(?:o)?(?:-(?:(?<family>sol|astra|terra|luna)|mini|nano|pro))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool AppliesTo(Uri? knownBase) => knownBase?.Host == "api.openai.com";

    public static IReadOnlyList<string> Latest(IReadOnlyList<string> models)
    {
        var matches = new List<(Version Version, string Family, string Id)>();
        foreach (var id in models)
        {
            var match = GeneralAlias.Match(id);
            if (!match.Success) { continue; }
            var version = match.Groups["version"].Value;
            if (Version.TryParse(version.Contains('.') ? version : version + ".0", out var parsed))
                matches.Add((parsed, match.Groups["family"].Value, id));
        }
        if (matches.Count == 0) { return Array.Empty<string>(); }
        var newest = matches.Max(m => m.Version);
        var newestPerFamily = matches.Where(m => m.Family.Length > 0).GroupBy(m => m.Family)
            .ToDictionary(g => g.Key, g => g.Max(m => m.Version));
        return matches.Where(m => m.Version == newest || newestPerFamily.GetValueOrDefault(m.Family) == m.Version)
            .OrderByDescending(m => m.Version).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).ToArray();
    }

    public const string NoMatch = "未识别到通用 GPT 模型，原列表未改动。取消“仅推荐模型”后可查询全部。";
}
