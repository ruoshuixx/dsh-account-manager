namespace AccountManager.Core.Dsh;

/// <summary>One provider template offered in the add / edit dialogs' name dropdown.</summary>
public sealed record ApiProviderTemplate(string Name, string BaseUrl, bool UseResponses, IReadOnlyList<string> Models);

/// <summary>The values of the API form fields the template rule works on (models: one id per line).</summary>
public sealed record ApiFormValues(string Name, string BaseUrl, string Models, bool UseResponses);

/// <summary>
/// The provider templates, defined once for both dialogs and the tests, and the rule for applying one. Picking a template never
/// overwrites what the user already typed: only empty fields are filled.
/// </summary>
public static class ApiProviderTemplates
{
    private static readonly string[] ArkModels =
    {
        "ark-code-latest", "deepseek-v4.1-flash", "glm-5.3-flash", "kimi-k2.7-code", "minimax-m3",
        "doubao-seed-2.1-turbo", "glm-5.3", "doubao-seed-evolving", "deepseek-v4-pro",
    };

    public static IReadOnlyList<ApiProviderTemplate> All { get; } = new[]
    {
        new ApiProviderTemplate("OpenAI", "https://api.openai.com/v1", true,
            new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna" }),
        new ApiProviderTemplate("DeepSeek", "https://api.deepseek.com", false, new[] { "deepseek-flash", "deepseek-v4-pro" }),
        new ApiProviderTemplate("火山 Coding Plan", "https://ark.cn-beijing.volces.com/api/coding/v3", true, ArkModels),
        new ApiProviderTemplate("火山 Agent Plan", "https://ark.cn-beijing.volces.com/api/plan/v3", true, ArkModels),
    };

    /// <summary>The names, in dropdown order.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(t => t.Name).ToArray();

    public static ApiProviderTemplate? Find(string? name)
        => All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// What the form looks like after the user picks <paramref name="template"/>: the name becomes the template's; the URL and the models
    /// are filled only when empty (a non-empty field stays as typed); the protocol follows the template only when the URL was empty, i.e.
    /// when the template's URL was filled in. The API key is not part of the form values and is never touched.
    /// </summary>
    public static ApiFormValues Apply(ApiProviderTemplate template, string? url, string? models, bool useResponses)
    {
        var urlEmpty = string.IsNullOrWhiteSpace(url);
        var newUrl = urlEmpty ? template.BaseUrl : url!;
        var newModels = string.IsNullOrWhiteSpace(models) ? string.Join(Environment.NewLine, template.Models) : models!;
        return new ApiFormValues(template.Name, newUrl, newModels, urlEmpty ? template.UseResponses : useResponses);
    }

    /// <summary>What dsh will be configured with automatically for this address and protocol, as a one-line hint; empty when nothing is.</summary>
    public static string AutoConfigHint(string? baseUrl, bool useResponses)
    {
        if (ModelVariantBuilder.IsDeepSeekOfficialEndpoint(baseUrl)) { return "将自动配置思考程度 " + string.Join(" / ", ModelVariantBuilder.DeepSeekEfforts) + "，上下文 100 万 / 输出 38.4 万（deepseek-flash 支持图片，deepseek-v4-pro 仅文字）"; }
        if (!useResponses) { return string.Empty; }
        if (ModelVariantBuilder.IsOpenAiOfficialEndpoint(baseUrl)) { return "将自动配置思考程度 " + string.Join(" / ", ModelVariantBuilder.OpenAiEfforts) + "，上下文 92.2 万 / 输出 12.8 万 / 支持图片（GPT-6 系列模型；超过 27.2 万输入按长上下文价计费）"; }
        if (ModelVariantBuilder.IsArkPlanEndpoint(baseUrl)) { return "将按方舟文档为支持的模型配置思考程度"; }
        return string.Empty;
    }
}
