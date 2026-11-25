using System.Text.RegularExpressions;

namespace AccountManager.Core.Actions;

internal static class PublishedModelGuides
{
    internal const string CodingUrl = "https://ark.volcengine.com/region:cn-beijing/docs/coding-plan-personal-plan-overview.md";
    internal const string OpenAiUrl = "https://developers.openai.com/api/docs/guides/model-selection.md";

    // Parse only the explicitly anchored section and its two-column model table, not examples,
    // pricing notes, or other tables on the page. If the layout changes, fail closed.
    internal static IReadOnlyList<string> CodingModels(string markdown)
    {
        var inSection = false;
        var inTable = false;
        var ids = new List<string>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!inSection) { inSection = line == "<span id=\"supported-models\"></span>"; continue; }
            if (!inTable)
            {
                if (line.StartsWith("<span id=", StringComparison.Ordinal)) { break; }
                inTable = line.Replace(" ", "") == "|模型|说明|";
                continue;
            }
            if (!line.StartsWith('|')) { break; }
            var end = line.IndexOf('|', 1);
            if (end < 0) { continue; }
            var cell = line[1..end].Trim().Trim('`').ToLowerInvariant();
            if (Regex.IsMatch(cell, @"\A[a-z][a-z0-9]*(?:-[a-z0-9.]+)+\z") && !line.Contains("下线"))
                ids.Add(cell);
        }
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> OpenAiPicks(string markdown, IReadOnlyList<string> visible)
    {
        // Match names against actual IDs: never send a model name invented from prose.
        var names = Regex.Matches(markdown, @"(?m)^\d+\.\s+\*\*([^*\r\n·]+) · [^*\r\n]+\*\*")
            .Select(m => m.Groups[1].Value.Trim().Replace(' ', '-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return visible.Where(names.Contains).ToArray();
    }
}
