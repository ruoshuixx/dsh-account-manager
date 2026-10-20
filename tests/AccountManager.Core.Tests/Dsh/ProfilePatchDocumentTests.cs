using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class ProfilePatchDocumentTests
{
    private static JsonObject Volc() => new()
    {
        ["openai-codex"] = new JsonObject { ["models"] = new JsonArray("gpt-5", "gpt-5-codex") },
        ["volcengine-coding"] = new JsonObject
        {
            ["displayName"] = "火山Codingplan",
            ["apiKeyEnv"] = "ARK_CODING_API_KEY",
            ["api"] = "openai-responses",
            ["baseURL"] = "https://ark.cn-beijing.volces.com/api/coding/v3",
            ["models"] = new JsonArray(new JsonObject
            {
                ["id"] = "doubao-seed-code",
                ["name"] = "Doubao Seed Code",
                ["contextWindow"] = 256000,
                ["maxTokens"] = 32000,
                ["input"] = new JsonArray("text", "image"),
                ["reasoningEfforts"] = new JsonObject { ["low"] = "low", ["high"] = "high" },
            }),
        },
    };

    [Fact]
    public void ReadsTheProvidersOfTheLlmPiAiEntry()
    {
        var doc = ProfilePatchDocument.Parse(Fixtures.Patch, "p/cordis.patch.yml");

        var providers = doc.GetProviders()!;

        Assert.Equal("gpt-5", providers["openai-codex"]!["models"]![0]!.GetValue<string>());
        Assert.Equal("keep", doc.GetEntry()!["config"]!["other"]!.GetValue<string>());
        Assert.Equal("Pi AI", doc.GetEntry()!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ReplacesOnlyTheProvidersAndKeepsEveryOtherByteIncludingComments()
    {
        var doc = ProfilePatchDocument.Parse(Fixtures.Patch, "p/cordis.patch.yml");

        Assert.True(doc.SetProviders(Volc()));

        var original = Fixtures.Patch;
        var from = original.IndexOf("    providers:", StringComparison.Ordinal);
        var to = original.IndexOf("    other: keep", StringComparison.Ordinal);
        Assert.StartsWith(original[..from], doc.Text);
        Assert.EndsWith(original[to..], doc.Text);
        // Other entries and their inline comments are byte-identical.
        Assert.Contains("# top comment kept\n- id: other-plugin\n  config:\n    a: 1   # inline comment\n# between entries\n- id: llm-pi-ai\n  name: Pi AI\n", doc.Text);
        Assert.EndsWith("- id: last-plugin\n  enabled: true\n", doc.Text);

        var read = ProfilePatchDocument.Parse(doc.Text, "p");
        Assert.Equal(Volc().ToJsonString(), read.GetProviders()!.ToJsonString());
        Assert.Equal("keep", read.GetEntry()!["config"]!["other"]!.GetValue<string>());
        Assert.Equal("Pi AI", read.GetEntry()!["name"]!.GetValue<string>());
    }

    [Fact]
    public void AppendsAnEntryWhenAbsentAndKeepsTheRestByteEqual()
    {
        const string original = "# header\n- id: other\n  x: 1  # c\n";
        var doc = ProfilePatchDocument.Parse(original, "p");

        Assert.True(doc.SetProviders(Volc()));

        Assert.StartsWith(original, doc.Text);
        var read = ProfilePatchDocument.Parse(doc.Text, "p");
        Assert.Equal(Volc().ToJsonString(), read.GetProviders()!.ToJsonString());
        Assert.Equal("other", YamlIds(doc.Text)[0]);
        Assert.Equal("llm-pi-ai", YamlIds(doc.Text)[1]);
    }

    [Fact]
    public void AppendsToANoTrailingNewlineFile()
    {
        var doc = ProfilePatchDocument.Parse("- id: other\n  x: 1", "p");

        doc.SetProviders(Volc());

        Assert.StartsWith("- id: other\n  x: 1\n- id: llm-pi-ai\n", doc.Text);
        Assert.NotNull(ProfilePatchDocument.Parse(doc.Text, "p").GetProviders());
    }

    [Fact]
    public void FillsInMissingConfigOnTheEntry()
    {
        var doc = ProfilePatchDocument.Parse("- id: llm-pi-ai\n  name: Pi\n- id: z\n", "p");

        doc.SetProviders(Volc());

        var read = ProfilePatchDocument.Parse(doc.Text, "p");
        Assert.Equal("Pi", read.GetEntry()!["name"]!.GetValue<string>());
        Assert.Equal(Volc().ToJsonString(), read.GetProviders()!.ToJsonString());
        Assert.EndsWith("- id: z\n", doc.Text);
    }

    [Fact]
    public void CreatesTheSequenceForAnEmptyOrCommentOnlyFile()
    {
        var empty = ProfilePatchDocument.Empty("p");
        empty.SetProviders(Volc());
        Assert.StartsWith("- id: llm-pi-ai\n  config:\n    providers:\n", empty.Text);
        Assert.Equal(Volc().ToJsonString(), ProfilePatchDocument.Parse(empty.Text, "p").GetProviders()!.ToJsonString());

        var comment = ProfilePatchDocument.Parse("# nothing yet\n", "p");
        comment.SetProviders(Volc());
        Assert.StartsWith("# nothing yet\n- id: llm-pi-ai\n", comment.Text);
    }

    [Fact]
    public void SettingTheSameProvidersIsANoOp()
    {
        var doc = ProfilePatchDocument.Parse(Fixtures.Patch, "p");
        doc.SetProviders(Volc());
        var text = doc.Text;

        Assert.False(doc.SetProviders(Volc()));
        Assert.Equal(text, doc.Text);
    }

    [Fact]
    public void PreservesCrLfAndNonAsciiPrefix()
    {
        var original = "# 注释 🙂\r\n- id: other\r\n  x: 1\r\n- id: llm-pi-ai\r\n  config:\r\n    providers: {}\r\n- id: tail\r\n";
        var doc = ProfilePatchDocument.Parse(original, "p");

        Assert.True(doc.SetProviders(Volc()));

        Assert.StartsWith("# 注释 🙂\r\n- id: other\r\n  x: 1\r\n- id: llm-pi-ai\r\n  config:\r\n    providers:\r\n", doc.Text);
        Assert.EndsWith("- id: tail\r\n", doc.Text);
        foreach (var line in doc.Text.Split('\n').SkipLast(1)) { Assert.EndsWith("\r", line); }
        Assert.Equal(Volc().ToJsonString(), ProfilePatchDocument.Parse(doc.Text, "p").GetProviders()!.ToJsonString());
    }

    [Fact]
    public void IndentedSequenceItemsAreHandled()
    {
        var original = "  - id: a\n    k: 1\n  - id: llm-pi-ai\n    config:\n      providers:\n        old: {}\n  - id: c\n";
        var doc = ProfilePatchDocument.Parse(original, "p");

        doc.SetProviders(Volc());
        Assert.Equal(Volc().ToJsonString(), ProfilePatchDocument.Parse(doc.Text, "p").GetProviders()!.ToJsonString());
        Assert.StartsWith("  - id: a\n    k: 1\n  - id: llm-pi-ai\n    config:\n      providers:\n", doc.Text);
        Assert.EndsWith("  - id: c\n", doc.Text);

        var appended = ProfilePatchDocument.Parse("  - id: a\n    k: 1\n", "p");
        appended.SetProviders(Volc());
        Assert.StartsWith("  - id: a\n    k: 1\n  - id: llm-pi-ai\n    config:\n", appended.Text);
        Assert.Equal(Volc().ToJsonString(), ProfilePatchDocument.Parse(appended.Text, "p").GetProviders()!.ToJsonString());
    }

    [Fact]
    public void FlowStyleFileIsReRenderedWithoutLosingEntries()
    {
        var doc = ProfilePatchDocument.Parse("[{id: other, x: 1}, {id: llm-pi-ai, name: Pi}]", "p");

        doc.SetProviders(Volc());

        var read = ProfilePatchDocument.Parse(doc.Text, "p");
        Assert.Equal("Pi", read.GetEntry()!["name"]!.GetValue<string>());
        Assert.Contains("id: other", doc.Text);
        Assert.Equal(Volc().ToJsonString(), read.GetProviders()!.ToJsonString());
    }

    [Fact]
    public void RejectsADocumentThatIsNotASequence()
    {
        Assert.Throws<DshFormatException>(() => ProfilePatchDocument.Parse("id: llm-pi-ai\n", "p"));
        Assert.Throws<DshFormatException>(() => ProfilePatchDocument.Parse("- id: [unclosed\n", "p"));
    }

    [Fact]
    public void UpdateWritesUnderTheLockWithBackupAndCreatesTheFile()
    {
        using var temp = new TempDir();
        var path = temp.File("profiles/tauri/cordis.patch.yml");
        var backups = temp.File("bak");

        Assert.True(ProfilePatchDocument.Update(path, d => d.SetProviders(Volc()), backups));
        Assert.False(File.Exists(path + ".lock"));
        Assert.Empty(Directory.Exists(backups) ? Directory.GetFiles(backups) : Array.Empty<string>()); // nothing to back up yet

        File.WriteAllText(path, File.ReadAllText(path) + "# trailing user comment\n");
        var before = File.ReadAllText(path);
        var smaller = new JsonObject { ["openai-codex"] = new JsonObject { ["models"] = new JsonArray("gpt-5") } };
        Assert.True(ProfilePatchDocument.Update(path, d => d.SetProviders(smaller), backups));

        var backup = Assert.Single(Directory.GetFiles(backups));
        Assert.Equal(before, File.ReadAllText(backup));
        Assert.Contains("# trailing user comment", File.ReadAllText(path));
        Assert.Equal(smaller.ToJsonString(), ProfilePatchDocument.Load(path).GetProviders()!.ToJsonString());
    }

    [Fact]
    public void UpdateRefusesToWriteOverAnInvalidFile()
    {
        using var temp = new TempDir();
        var path = temp.File("cordis.patch.yml");
        File.WriteAllText(path, "not: a sequence\n");

        Assert.Throws<DshFormatException>(() => ProfilePatchDocument.Update(path, d => d.SetProviders(Volc())));

        Assert.Equal("not: a sequence\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".lock"));
    }

    private static List<string> YamlIds(string text)
        => text.Split('\n').Where(l => l.TrimStart().StartsWith("- id:", StringComparison.Ordinal)).Select(l => l.Split(':')[1].Trim()).ToList();
}
