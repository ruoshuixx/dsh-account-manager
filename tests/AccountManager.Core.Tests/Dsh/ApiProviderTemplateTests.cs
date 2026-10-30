using AccountManager.Core.Accounts;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>The provider templates, the reasoning tiers of the official OpenAI endpoint and the address display.</summary>
public class ApiProviderTemplateTests
{
    private const string OpenAi = "https://api.openai.com/v1";

    private static ApiProviderTemplate Template(string name) => ApiProviderTemplates.Find(name)!;

    [Fact]
    public void TheThreeTemplatesHaveTheAgreedValues()
    {
        Assert.Equal(new[] { "OpenAI", "火山 Coding Plan", "火山 Agent Plan" }, ApiProviderTemplates.Names);
        var openai = Template("OpenAI");
        Assert.Equal(OpenAi, openai.BaseUrl);
        Assert.True(openai.UseResponses);
        Assert.Equal(new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna" }, openai.Models);
        Assert.Equal("https://ark.cn-beijing.volces.com/api/coding/v3", Template("火山 Coding Plan").BaseUrl);
        Assert.Equal("https://ark.cn-beijing.volces.com/api/plan/v3", Template("火山 Agent Plan").BaseUrl);
        Assert.Equal(new[] { "ark-code-latest", "deepseek-v4.1-flash", "glm-5.3-flash", "kimi-k2.7-code", "minimax-m3", "doubao-seed-2.1-turbo", "glm-5.3", "doubao-seed-evolving", "deepseek-v4-pro" },
            Template("火山 Coding Plan").Models);
        Assert.Equal(Template("火山 Coding Plan").Models, Template("火山 Agent Plan").Models);
        Assert.All(ApiProviderTemplates.All, t => Assert.True(t.UseResponses));
    }

    [Fact]
    public void PickingATemplateFillsOnlyEmptyFieldsAndSetsTheProtocolWithTheUrl()
    {
        var template = Template("OpenAI");

        var empty = ApiProviderTemplates.Apply(template, "", "  ", useResponses: false);
        Assert.Equal(new ApiFormValues("OpenAI", OpenAi, string.Join(Environment.NewLine, template.Models), true), empty);

        var typedUrl = ApiProviderTemplates.Apply(template, "https://gw.example.test/v1", "", useResponses: false);
        Assert.Equal("https://gw.example.test/v1", typedUrl.BaseUrl);
        Assert.False(typedUrl.UseResponses); // the URL was not filled, so the protocol stays
        Assert.Equal(string.Join(Environment.NewLine, template.Models), typedUrl.Models);

        var typedModels = ApiProviderTemplates.Apply(template, null, "my-model", useResponses: false);
        Assert.Equal("my-model", typedModels.Models);
        Assert.Equal(OpenAi, typedModels.BaseUrl);
        Assert.True(typedModels.UseResponses);

        var all = ApiProviderTemplates.Apply(Template("火山 Agent Plan"), "https://x.example.test/v1", "m1", useResponses: false);
        Assert.Equal(new ApiFormValues("火山 Agent Plan", "https://x.example.test/v1", "m1", false), all);
    }

    [Fact]
    public void TheHintFollowsTheAddressAndProtocol()
    {
        Assert.Contains("low / medium / high / xhigh / max", ApiProviderTemplates.AutoConfigHint(OpenAi, true));
        Assert.Contains("方舟", ApiProviderTemplates.AutoConfigHint("https://ark.cn-beijing.volces.com/api/plan/v3", true));
        Assert.Equal(string.Empty, ApiProviderTemplates.AutoConfigHint(OpenAi, false));
        Assert.Equal(string.Empty, ApiProviderTemplates.AutoConfigHint("https://gw.example.test/v1", true));
        Assert.Equal(string.Empty, ApiProviderTemplates.AutoConfigHint("", true));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com/v1/", true)]
    [InlineData("https://API.OpenAI.com/v1", true)]
    [InlineData("http://api.openai.com/v1", false)]
    [InlineData("https://api.openai.com:8443/v1", false)]
    [InlineData("https://api.openai.com/v1?x=1", false)]
    [InlineData("https://user@api.openai.com/v1", false)]
    [InlineData("https://api.openai.com/v2", false)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("https://api.openai.com.evil.test/v1", false)]
    [InlineData("https://eu.api.openai.com/v1", false)]
    public void OnlyTheOfficialOpenAiEndpointQualifies(string url, bool expected)
        => Assert.Equal(expected, ModelVariantBuilder.IsOpenAiOfficialEndpoint(url));

    [Theory]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("gpt-6.2-x", true)]
    [InlineData("GPT-6.1-terra", true)]
    [InlineData("gpt-60", false)]
    [InlineData("gpt-6x", false)]
    [InlineData("gpt-5.5", false)]
    [InlineData("o3", false)]
    [InlineData("", false)]
    public void TheGpt6FamilyMatchesOnTheBoundary(string id, bool expected)
        => Assert.Equal(expected, ModelVariantBuilder.IsGpt6Family(id));

    [Fact]
    public void Gpt6ModelsOnTheOfficialEndpointGetFiveTiersAndNothingElseDoes()
    {
        foreach (var id in new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna", "gpt-6.2-x" })
        {
            var entry = ModelVariantBuilder.NewModelEntry(id, true, OpenAi);
            var variants = entry["variants"]!.AsArray();
            Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, variants.Select(v => v!["id"]!.GetValue<string>()));
            Assert.All(variants, v => Assert.Equal(v!["id"]!.GetValue<string>(), v["body"]!["reasoning"]!["effort"]!.GetValue<string>()));
        }

        Assert.False(ModelVariantBuilder.NewModelEntry("gpt-5.5", true, OpenAi).ContainsKey("variants"));
        Assert.False(ModelVariantBuilder.NewModelEntry("gpt-6-astra", false, OpenAi).ContainsKey("variants")); // chat protocol
        Assert.False(ModelVariantBuilder.NewModelEntry("gpt-6-astra", true, "https://gw.example.test/v1").ContainsKey("variants"));
        Assert.False(ModelVariantBuilder.NewModelEntry("gpt-6-astra", true, "https://api.openai.com/v1?x=1").ContainsKey("variants"));
    }

    [Fact]
    public void ExistingVariantsOnTheOfficialEndpointAreNeverOverwritten()
    {
        var entry = ModelVariantBuilder.NewModelEntry("gpt-6-astra", true, "https://gw.example.test/v1");
        Assert.False(entry.ContainsKey("variants"));
        entry["variants"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "high" });
        Assert.False(ModelVariantBuilder.TryAddMissingVariants(entry, "gpt-6-astra", true, OpenAi));
        Assert.Single(entry["variants"]!.AsArray());

        var bare = new System.Text.Json.Nodes.JsonObject { ["name"] = "gpt-6-astra" };
        Assert.True(ModelVariantBuilder.TryAddMissingVariants(bare, "gpt-6-astra", true, OpenAi)); // backfill on re-save
    }

    [Fact]
    public void ADisplayedAddressIsNotBlankedByARegisteredSecretThatOccursInIt()
    {
        var secrets = new SecretRegistry(applyLongValueFilter: false);
        var mapper = new AccountMapper(secrets);
        secrets.RegisterValue("https://gw.example.test/v1");
        secrets.RegisterValue("s");
        Assert.Equal("https://gw.example.test/v1", mapper.SafeBaseUrl("https://gw.example.test/v1"));

        // Pattern-based redaction still applies to the address.
        var shown = mapper.SafeBaseUrl("https://gw.example.test/sk-abcdefghijkl/v1");
        Assert.DoesNotContain("abcdefghijkl", shown);
        Assert.Contains("[REDACTED-KEY]", shown);
    }
}
