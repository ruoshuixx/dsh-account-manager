using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

public class CredentialsDocumentTests
{
    private const string CodexKey = "llm-pi-ai/openai-codex";

    [Fact]
    public void ParsesTheRealLayout()
    {
        var doc = CredentialsDocument.Parse(Fixtures.Credentials, "/x/.credentials.yaml");

        Assert.Equal(3, doc.Refs.Count);
        Assert.Equal("fake-ark-key-123", doc.GetRef("ARK_CODING_API_KEY"));
        Assert.Equal("{\"access\":\"fake.a\",\"refresh\":\"fake_r\",\"expires\":1760000000000}", doc.GetRef("CODEX_OAUTH_1"));
        Assert.Equal("{\"x\":1}", doc.GetRef("CODEX_OAUTH_2"));
        Assert.Equal(new[] { CodexKey, "client-connection/browser-session", "openai/api" }, doc.Records.Select(r => r.Key).ToArray());
        Assert.Equal(new[] { "x-foreign" }, doc.IgnoredKeys);

        var grant = doc.GetGrantPayload(CodexKey)!.AsObject();
        Assert.Equal(1760000000000L, grant["expires"]!.GetValue<long>());
        Assert.Equal("oauth", grant["type"]!.GetValue<string>());

        var browser = doc.GetGrantPayload("client-connection/browser-session")!.AsObject();
        Assert.Equal(0.5, browser["nested"]!["ratio"]!.GetValue<double>());
        Assert.False(browser["nested"]!["flag"]!.GetValue<bool>());
        Assert.Equal(4, browser["nested"]!["list"]!.AsArray().Count);
        Assert.Null(browser["nested"]!["list"]![3]);

        var api = doc.GetRecord("openai/api")!;
        Assert.Equal(CredentialRecord.ApiKeyKind, api.Kind);
        Assert.Equal("fake-key", api.Key);
        Assert.Equal("fake-env", api.Env!.Single().Value);
    }

    [Fact]
    public void SetGrantReplacesOnlyThatRecordAndLeavesEveryOtherByteIdentical()
    {
        var doc = CredentialsDocument.Parse(Fixtures.Credentials, "/x/.credentials.yaml");
        var payload = Fixtures.Grant("new.access.jwt", 1799999999999L);

        Assert.True(doc.SetGrant(CodexKey, payload));

        var original = Fixtures.Credentials;
        var startMarker = "  llm-pi-ai/openai-codex:";
        var endMarker = "  client-connection/browser-session:";
        var prefix = original[..original.IndexOf(startMarker, StringComparison.Ordinal)];
        var suffix = original[original.IndexOf(endMarker, StringComparison.Ordinal)..];
        Assert.StartsWith(prefix + startMarker, doc.Text);
        Assert.EndsWith(suffix, doc.Text);

        var read = CredentialsDocument.Parse(doc.Text, "/x/.credentials.yaml");
        Assert.Equal(1799999999999L, read.GetGrantPayload(CodexKey)!["expires"]!.GetValue<long>());
        Assert.Equal("new.access.jwt", read.GetGrantPayload(CodexKey)!["access"]!.GetValue<string>());
        Assert.Equal(new[] { CodexKey, "client-connection/browser-session", "openai/api" }, read.Records.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void SettingTheSameValueIsANoOp()
    {
        var doc = CredentialsDocument.Parse(Fixtures.Credentials, "/x/.credentials.yaml");
        var current = doc.GetGrantPayload(CodexKey)!;

        Assert.False(doc.SetGrant(CodexKey, current));
        Assert.Equal(Fixtures.Credentials, doc.Text);
    }

    [Fact]
    public void NewRefAndRecordAreAppendedAndOthersStayByteEqual()
    {
        var doc = CredentialsDocument.Parse(Fixtures.Credentials, "/x/.credentials.yaml");

        Assert.True(doc.SetRef("VOLC_KEY", "fake-volc"));
        Assert.True(doc.SetApiKey("llm-pi-ai/volcengine-coding", "k-1", new[] { KeyValuePair.Create("VOLC_ENV", "v") }));

        var refsEnd = Fixtures.Credentials.IndexOf("records:", StringComparison.Ordinal);
        var beforeRefs = Fixtures.Credentials[..refsEnd].TrimEnd('\n');
        Assert.StartsWith(beforeRefs, doc.Text);
        // The old records block and the foreign key are untouched.
        Assert.Contains(Fixtures.Credentials[refsEnd..Fixtures.Credentials.IndexOf("x-foreign", StringComparison.Ordinal)].TrimEnd('\n'), doc.Text);
        Assert.Contains("x-foreign: 1", doc.Text);

        var read = CredentialsDocument.Parse(doc.Text, "p");
        Assert.Equal("fake-volc", read.GetRef("VOLC_KEY"));
        Assert.Equal("k-1", read.GetRecord("llm-pi-ai/volcengine-coding")!.Key);
        Assert.Equal(4, read.Refs.Count);
        Assert.Equal(4, read.Records.Count);
    }

    [Fact]
    public void RemoveRefAndRecordTouchOnlyTheirLines()
    {
        var doc = CredentialsDocument.Parse(Fixtures.Credentials, "p");

        Assert.True(doc.RemoveRef("CODEX_OAUTH_2"));
        Assert.True(doc.RemoveRecord("client-connection/browser-session"));
        Assert.False(doc.RemoveRef("NOT_THERE"));

        var expected = Fixtures.Credentials
            .Replace("  CODEX_OAUTH_2: \"{\\\"x\\\":1}\"\n", string.Empty);
        var from = expected.IndexOf("  client-connection/browser-session:", StringComparison.Ordinal);
        var to = expected.IndexOf("  openai/api:", StringComparison.Ordinal);
        expected = expected.Remove(from, to - from);
        Assert.Equal(expected, doc.Text);
    }

    [Fact]
    public void CreatesAVersionedDocumentFromNothingAndRoundTrips()
    {
        var doc = CredentialsDocument.Empty("/x/.credentials.yaml");

        Assert.True(doc.SetRef("A_KEY", "v1"));
        Assert.True(doc.SetGrant(CodexKey, Fixtures.Grant("a.b.c", 1760000000000L)));

        Assert.StartsWith("version: 1\n", doc.Text);
        var read = CredentialsDocument.Parse(doc.Text, "p");
        Assert.Equal("v1", read.GetRef("A_KEY"));
        Assert.Equal(1760000000000L, read.GetGrantPayload(CodexKey)!["expires"]!.GetValue<long>());
    }

    [Fact]
    public void PayloadRoundTripsLosslesslyWithAwkwardValues()
    {
        var payload = new JsonObject
        {
            ["int"] = 1760000000000L,
            ["neg"] = -5,
            ["float"] = 1.5,
            ["bool"] = true,
            ["nil"] = null,
            ["str"] = "line1\nline2 \"q\" \\ tab\t end",
            ["numlike"] = "123",
            ["boollike"] = "true",
            ["nulllike"] = "null",
            ["empty"] = string.Empty,
            ["colon"] = "a: b",
            ["hash"] = "x #y",
            ["url"] = "https://example.com/a?b=c",
            ["cjk"] = "火山Codingplan 🙂",
            ["jwt"] = "eyJhbGciOi.eyJzdWIiOiJ4In0.sig-_x",
            ["emptyObj"] = new JsonObject(),
            ["emptyArr"] = new JsonArray(),
            ["arr"] = new JsonArray(1, "two", new JsonObject { ["k"] = "v", ["n"] = new JsonArray(3, 4) }, new JsonArray("x", "y")),
            ["nested"] = new JsonObject { ["deep"] = new JsonObject { ["deeper"] = new JsonArray() } },
        };
        var doc = CredentialsDocument.Empty("p");

        doc.SetGrant("scope/id", payload);

        var read = CredentialsDocument.Parse(doc.Text, "p");
        Assert.Equal(payload.ToJsonString(), read.GetGrantPayload("scope/id")!.ToJsonString());
    }

    [Fact]
    public void IntegersStayIntegers()
    {
        var doc = CredentialsDocument.Empty("p");
        doc.SetGrant("scope/id", new JsonObject { ["expires"] = 1760000000000L });

        Assert.Contains("expires: 1760000000000", doc.Text);
        Assert.DoesNotContain("E+", doc.Text);
    }

    [Fact]
    public void CrLfDocumentsKeepTheirLineEndings()
    {
        var crlf = Fixtures.Credentials.Replace("\n", "\r\n");
        var doc = CredentialsDocument.Parse(crlf, "p");

        Assert.True(doc.SetGrant(CodexKey, Fixtures.Grant("n.a.b", 5)));
        Assert.True(doc.SetRef("NEW_REF", "v"));

        foreach (var line in doc.Text.Split('\n').SkipLast(1)) { Assert.EndsWith("\r", line); }
        var read = CredentialsDocument.Parse(doc.Text, "p");
        Assert.Equal("v", read.GetRef("NEW_REF"));
        Assert.Contains("x-foreign: 1", doc.Text);
    }

    [Fact]
    public void NonAsciiTextBeforeAnEntryDoesNotShiftTheSpans()
    {
        var text = "# 注释 🙂 emoji and 中文\nversion: 1\nrefs:\n  A_REF: 值一\n  B_REF: two\nrecords:\n  scope/it:\n    kind: grant\n    payload: {a: 1}\n";
        var doc = CredentialsDocument.Parse(text, "p");

        doc.SetRef("A_REF", "新值");
        doc.SetGrant("scope/it", new JsonObject { ["a"] = 2 });

        Assert.Equal("# 注释 🙂 emoji and 中文\nversion: 1\nrefs:\n  A_REF: 新值\n  B_REF: two\nrecords:\n  scope/it:\n    kind: grant\n    payload:\n      a: 2\n", doc.Text);
    }

    [Fact]
    public void FlowStyleOrEmptySectionsAreRenderedFromTheirContent()
    {
        var doc = CredentialsDocument.Parse("version: 1\nrefs: {A_REF: one}\nrecords:\n", "p");

        Assert.True(doc.SetRef("B_REF", "two"));
        Assert.True(doc.SetGrant("scope/it", new JsonObject { ["a"] = 1 }));

        var read = CredentialsDocument.Parse(doc.Text, "p");
        Assert.Equal(new[] { "A_REF", "B_REF" }, read.Refs.Select(r => r.Key).ToArray());
        Assert.Single(read.Records);
    }

    [Theory]
    [InlineData("refs:\n  A_REF: one\n", "pre-release")]                                // no version
    [InlineData("version: 2\nrefs:\n  A_REF: one\n", "version")]
    [InlineData("version: 1\nrefs:\n  A_REF: 123\n", "must be a string")]               // non-string ref
    [InlineData("version: 1\nrefs:\n  A_REF: true\n", "must be a string")]
    [InlineData("version: 1\nrefs:\n  A_REF: \"\"\n", "empty")]
    [InlineData("version: 1\nrefs:\n  1BAD: x\n", "valid identifier")]
    [InlineData("version: 1\nrefs: [a, b]\n", "must be a mapping")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: password\n", "unknown kind")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    key: x\n", "no kind")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: api-key\n    token: x\n", "unknown field")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: grant\n", "no payload")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: grant\n    payload: {a: 1}\n    extra: 1\n", "unknown field")]
    [InlineData("version: 1\nrecords:\n  Scope/Id:\n    kind: grant\n    payload: {}\n", "<scope>/<id>")]
    [InlineData("version: 1\nrecords:\n  noslash:\n    kind: grant\n    payload: {}\n", "<scope>/<id>")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: api-key\n    key: 5\n", "non-string")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: api-key\n    env:\n      A_B: 5\n", "non-empty string")]
    [InlineData("version: 1\nrecords:\n  scope/id:\n    kind: grant\n    payload: .inf\n", "non-finite")]
    [InlineData("version: 1\nrefs:\n  A_REF: x\n  A_REF: y\n", "duplicate")]
    [InlineData("version: 1\nrefs: &a\n  A_REF: x\n", "anchor")]
    [InlineData("version: 1\nrefs: [unclosed\n", "not valid YAML")]
    [InlineData("- a\n- b\n", "mapping")]
    public void RejectsWhatDshWouldReject(string text, string expectedFragment)
    {
        var ex = Assert.Throws<DshFormatException>(() => CredentialsDocument.Parse(text, "/x/.credentials.yaml"));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorMessagesDoNotLeakSecretValues()
    {
        var ex = Assert.Throws<DshFormatException>(() => CredentialsDocument.Parse("version: 1\nrefs:\n  A_REF: 12345\nrecords: [SECRET-VALUE\n", "p"));
        Assert.DoesNotContain("SECRET-VALUE", ex.Message);
        ex = Assert.Throws<DshFormatException>(() => CredentialsDocument.Parse("version: 1\nrefs:\n  A_REF: 99887766\n", "p"));
        Assert.DoesNotContain("99887766", ex.Message);
    }

    [Fact]
    public void AnEmptyOrCommentOnlyFileIsAnEmptyStoreWithoutVersion()
    {
        Assert.Empty(CredentialsDocument.Parse(string.Empty, "p").Refs);
        Assert.Empty(CredentialsDocument.Parse("# nothing\n", "p").Records);
        Assert.Empty(CredentialsDocument.Parse("---\n", "p").Records);

        var doc = CredentialsDocument.Parse("# only a comment\n", "p");
        doc.SetRef("A_B", "v");
        Assert.Equal("# only a comment\nversion: 1\nrefs:\n  A_B: v\n", doc.Text);
    }

    [Fact]
    public void RejectsInvalidInputsToMutators()
    {
        var doc = CredentialsDocument.Empty("p");
        Assert.Throws<ArgumentException>(() => doc.SetRef("bad-name", "v"));
        Assert.Throws<ArgumentException>(() => doc.SetRef("OK", ""));
        Assert.Throws<ArgumentException>(() => doc.SetGrant("nokey", new JsonObject()));
        Assert.Throws<ArgumentException>(() => doc.SetGrant("a/b", new JsonObject { ["x"] = double.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => doc.SetApiKey("a/b", ""));
    }

    // ---- file persistence ----

    [Fact]
    public void UpdateCreatesTheFileAt0600WithDirectory0700AndLeavesNoLockOrTemp()
    {
        using var temp = new TempDir();
        var path = temp.File("sub/.credentials.yaml");

        var changed = CredentialsDocument.Update(path, d => d.SetRef("A_B", "v"));

        Assert.True(changed);
        Assert.Equal("version: 1\nrefs:\n  A_B: v\n", File.ReadAllText(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }

        Assert.Equal(new[] { ".credentials.yaml" }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void UpdateRepairsAWiderFileModeOnWrite()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var temp = new TempDir();
        var path = temp.File(".credentials.yaml");
        File.WriteAllText(path, Fixtures.Credentials);
        File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);

        CredentialsDocument.Update(path, d => d.SetRef("NEW_ONE", "v"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void UpdateRereadsInsideTheLockSoOthersChangesSurvive()
    {
        using var temp = new TempDir();
        var path = temp.File(".credentials.yaml");
        File.WriteAllText(path, Fixtures.Credentials);

        CredentialsDocument.Update(path, d =>
        {
            // Another writer changes the file after our first read but before our commit window closes.
            // The lock is ours, so it can only happen through a stale snapshot; verify the fresh read wins.
            Assert.Equal("fake-ark-key-123", d.GetRef("ARK_CODING_API_KEY"));
            return d.SetRef("MINE", "1");
        });
        File.WriteAllText(path, File.ReadAllText(path).Replace("fake-ark-key-123", "changed-by-other"));
        CredentialsDocument.Update(path, d => d.SetRef("MINE_TOO", "2"));

        var read = CredentialsDocument.Load(path);
        Assert.Equal("changed-by-other", read.GetRef("ARK_CODING_API_KEY"));
        Assert.Equal("1", read.GetRef("MINE"));
        Assert.Equal("2", read.GetRef("MINE_TOO"));
    }

    [Fact]
    public void UpdateWithNoChangeDoesNotTouchTheFile()
    {
        using var temp = new TempDir();
        var path = temp.File(".credentials.yaml");
        File.WriteAllText(path, Fixtures.Credentials);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var changed = CredentialsDocument.Update(path, d => d.RemoveRef("ABSENT"));

        Assert.False(changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(path + ".lock"));
    }

    [Fact]
    public void UpdateRefusesToWriteWhenTheOnDiskDocumentIsInvalid()
    {
        using var temp = new TempDir();
        var path = temp.File(".credentials.yaml");
        const string bad = "refs:\n  A_REF: legacy-flat-without-version\n";
        File.WriteAllText(path, bad);
        var called = false;

        var ex = Assert.Throws<DshFormatException>(() => CredentialsDocument.Update(path, d => { called = true; return d.SetRef("X", "y"); }));

        Assert.False(called);
        Assert.Equal(bad, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".lock"));
        Assert.DoesNotContain("legacy-flat-without-version", ex.Message);
    }

    [Fact]
    public void LockIsReleasedWhenTheMutationThrows()
    {
        using var temp = new TempDir();
        var path = temp.File(".credentials.yaml");

        Assert.Throws<InvalidOperationException>(() => CredentialsDocument.Update(path, d => throw new InvalidOperationException("boom")));

        Assert.False(File.Exists(path + ".lock"));
        Assert.False(File.Exists(path));
    }
}
