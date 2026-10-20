using System.Diagnostics;
using System.Text.Json.Nodes;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>
/// Interoperability with dsh's own <c>withFileLock</c>, run through a real node process that imports the
/// installed <c>@deepseek-ai/dsh-atomic-write</c>. Skipped when not on Linux or when node / the package is missing.
/// Only temp directories are touched; the installed package files are read, never modified.
/// </summary>
public class DshInteropTests
{
    private const string PackageIndex =
        "/.local/share/dsh-tauri/dependencies/dsh/node_modules/@deepseek-ai/dsh-atomic-write/lib/index.js";

    /// <summary>Candidate user homes; HOME may be redirected by the test environment, so the account's own /home entry counts too.</summary>
    private static IEnumerable<string> Homes()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return "/home/" + Environment.UserName;
    }

    private static string? FindNode()
    {
        foreach (var home in Homes())
        {
            var bundled = home + "/.local/share/dsh-tauri/runtime/bin/node";
            if (File.Exists(bundled)) { return bundled; }
        }

        return new[] { "/usr/bin/node", "/usr/local/bin/node" }.FirstOrDefault(File.Exists);
    }

    private static (string Node, string Package)? Prerequisites()
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        var node = FindNode();
        var package = Homes().Select(h => h + PackageIndex).FirstOrDefault(File.Exists);
        return node is null || package is null ? null : (node, package);
    }

    private static Process StartNode(string node, string script, params string[] args)
    {
        var psi = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--input-type=module");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        return Process.Start(psi)!;
    }

    private const string HolderScript = """
        import { pathToFileURL } from 'node:url';
        const [pkg, file, holdMs, text] = process.argv.slice(1);
        const { withFileLock, writeFileAtomic } = await import(pathToFileURL(pkg).href);
        await withFileLock(file, async () => {
          console.log('LOCKED');
          await new Promise((r) => setTimeout(r, Number(holdMs)));
          await writeFileAtomic(file, text, { mode: 0o600, dirMode: 0o700 });
        }, { waitMs: 30000 });
        console.log('DONE');
        """;

    private const string ContenderScript = """
        import { pathToFileURL } from 'node:url';
        import { readFile } from 'node:fs/promises';
        const [pkg, file, text] = process.argv.slice(1);
        const { withFileLock, writeFileAtomic } = await import(pathToFileURL(pkg).href);
        const started = Date.now();
        await withFileLock(file, async () => {
          console.log('ACQUIRED after ' + (Date.now() - started));
          const prev = await readFile(file, 'utf8').catch(() => '');
          await writeFileAtomic(file, prev + text, { mode: 0o600, dirMode: 0o700 });
        }, { waitMs: 30000 });
        console.log('DONE');
        """;

    private static string ReadLineWithTimeout(Process p, int ms)
    {
        var task = p.StandardOutput.ReadLineAsync();
        Assert.True(task.Wait(ms), "node did not respond in time");
        return task.Result ?? string.Empty;
    }

    [Fact]
    public void OurUpdateWaitsForDshHoldingTheLockAndBothWritesSurvive()
    {
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        using var holder = StartNode(req.Node, HolderScript, req.Package, file, "1500", "version: 1\nrefs:\n  FROM_DSH: dsh-wrote-this\n");
        Assert.Equal("LOCKED", ReadLineWithTimeout(holder, 15000));
        Assert.True(File.Exists(file + ".lock"));
        Assert.Equal(holder.Id + "\n", File.ReadAllText(file + ".lock"));
        var clock = Stopwatch.StartNew();

        CredentialsDocument.Update(file, d => d.SetRef("FROM_US", "we-wrote-this"));

        Assert.True(clock.ElapsedMilliseconds >= 1000, "should have waited for dsh to release");
        holder.WaitForExit(10000);
        var read = CredentialsDocument.Load(file);
        Assert.Equal("dsh-wrote-this", read.GetRef("FROM_DSH"));
        Assert.Equal("we-wrote-this", read.GetRef("FROM_US"));
        Assert.False(File.Exists(file + ".lock"));
    }

    [Fact]
    public void DshWaitsForUsHoldingTheLockAndBothWritesSurvive()
    {
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        Process? contender = null;
        var releaseAfter = Stopwatch.StartNew();
        try
        {
            CredentialsDocument.Update(file, d =>
            {
                d.SetRef("FROM_US", "we-wrote-this");
                contender = StartNode(req.Node, ContenderScript, req.Package, file, "# dsh-appended-comment\n");
                Thread.Sleep(1500); // keep holding our lock while dsh retries
                Assert.False(contender.WaitForExit(200), "dsh must be waiting on our lock");
                return true;
            });

            Assert.NotNull(contender);
            Assert.True(contender!.WaitForExit(15000));
            Assert.Equal(0, contender.ExitCode);
            var text = File.ReadAllText(file);
            Assert.Contains("FROM_US: we-wrote-this", text);
            Assert.EndsWith("# dsh-appended-comment\n", text);
            Assert.False(File.Exists(file + ".lock"));
        }
        finally
        {
            if (contender is { HasExited: false }) { contender.Kill(); }
            contender?.Dispose();
        }
    }

    [Fact]
    public void DshTakesOverALockOfADeadHolderLeftByUsAndViceVersa()
    {
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        File.WriteAllText(file + ".lock", "2147483000\n");

        using var contender = StartNode(req.Node, ContenderScript, req.Package, file, "version: 1\n");
        Assert.True(contender.WaitForExit(15000));
        Assert.Equal(0, contender.ExitCode);
        Assert.False(File.Exists(file + ".lock"));

        // And the other way round: a stale lock written in dsh's format is taken over by us.
        File.WriteAllText(file + ".lock", "2147483000\n");
        Assert.True(CredentialsDocument.Update(file, d => d.SetRef("AFTER_TAKEOVER", "ok") || true));
        Assert.False(File.Exists(file + ".lock"));
    }

    [Fact]
    public void ADocumentWrittenByDshIsReadableAndByUsIsReadableByDshsParserShape()
    {
        // dsh's own writer produces files our strict parser must accept (single-quoted JSON refs, flow lists, etc.).
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        using var holder = StartNode(req.Node, HolderScript, req.Package, file, "10", "version: 1\nrefs:\n  A_B: x\n");
        Assert.True(holder.WaitForExit(15000));

        var doc = CredentialsDocument.Load(file);
        Assert.Equal("x", doc.GetRef("A_B"));
    }

    private const string YamlPackageIndex = "/.local/share/dsh-tauri/dependencies/dsh/node_modules/yaml/dist/index.js";

    private const string YamlScript = """
        import { createRequire } from 'node:module';
        import { readFileSync } from 'node:fs';
        const [pkg, file] = process.argv.slice(1);
        const { parseDocument } = createRequire(import.meta.url)(pkg);
        const doc = parseDocument(readFileSync(file, 'utf8'), { prettyErrors: true, uniqueKeys: true });
        if (doc.errors.length > 0) { console.error(doc.errors.map((e) => e.code).join(',')); process.exit(3); }
        console.log(JSON.stringify(doc.toJS()));
        """;

    private static JsonNode? ParseWithDshsYaml(string node, string file)
    {
        var yaml = Homes().Select(h => h + YamlPackageIndex).FirstOrDefault(File.Exists);
        if (yaml is null) { return JsonValue.Create("skip"); }
        using var p = StartNode(node, YamlScript, yaml, file);
        var output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(15000));
        Assert.True(p.ExitCode == 0, "dsh's yaml parser rejected our output: " + p.StandardError.ReadToEnd());
        return JsonNode.Parse(output);
    }

    [Fact]
    public void DshsYamlParserReadsExactlyWhatWeWrite()
    {
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        var payload = new JsonObject
        {
            ["expires"] = 1760000000000L,
            ["ratio"] = 1.5,
            ["flag"] = true,
            ["nil"] = null,
            ["numlike"] = "123",
            ["boollike"] = "true",
            ["colon"] = "a: b # c",
            ["multi"] = "l1\nl2 \"q\" \\ end",
            ["cjk"] = "火山Codingplan 🙂",
            ["jwt"] = "eyJhbGciOi.eyJzdWIiOiJ4In0.sig-_x",
            ["emptyObj"] = new JsonObject(),
            ["arr"] = new JsonArray(1, "two", new JsonObject { ["k"] = "v" }, new JsonArray("x")),
        };
        CredentialsDocument.Update(file, d => d.SetRef("CODEX_OAUTH_1", "{\"a\":1}") | d.SetGrant("llm-pi-ai/openai-codex", payload)
            | d.SetApiKey("openai/api", "k", new[] { KeyValuePair.Create("OPENAI_API_KEY", "v") }));

        var parsed = ParseWithDshsYaml(req.Node, file)!.AsObject();
        if (parsed is { Count: 0 }) { return; }

        Assert.Equal(1, parsed["version"]!.GetValue<int>());
        Assert.Equal("{\"a\":1}", parsed["refs"]!["CODEX_OAUTH_1"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(payload, parsed["records"]!["llm-pi-ai/openai-codex"]!["payload"]));
        Assert.Equal("grant", parsed["records"]!["llm-pi-ai/openai-codex"]!["kind"]!.GetValue<string>());
        Assert.Equal("v", parsed["records"]!["openai/api"]!["env"]!["OPENAI_API_KEY"]!.GetValue<string>());
    }

    [Fact]
    public void DshsYamlParserReadsTheProfilePatchWeWrite()
    {
        if (Prerequisites() is not { } req) { return; }
        using var temp = new TempDir();
        var file = temp.File("cordis.patch.yml");
        File.WriteAllText(file, Fixtures.Patch);
        var providers = new JsonObject
        {
            ["volcengine-coding"] = new JsonObject
            {
                ["displayName"] = "火山Codingplan",
                ["baseURL"] = "https://ark.cn-beijing.volces.com/api/coding/v3",
                ["models"] = new JsonArray(new JsonObject { ["id"] = "m", ["contextWindow"] = 256000, ["input"] = new JsonArray("text", "image") }),
            },
        };

        ProfilePatchDocument.Update(file, d => d.SetProviders(providers));

        var parsed = ParseWithDshsYaml(req.Node, file)!.AsArray();
        var entry = parsed.Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == "llm-pi-ai");
        Assert.True(JsonNode.DeepEquals(providers, entry["config"]!["providers"]));
        Assert.Equal("keep", entry["config"]!["other"]!.GetValue<string>());
        Assert.Equal(3, parsed.Count);
    }

    private const string YamlWriterScript = """
        import { createRequire } from 'node:module';
        import { writeFileSync } from 'node:fs';
        const [pkg, file] = process.argv.slice(1);
        const { Document } = createRequire(import.meta.url)(pkg);
        const longText = Array.from({ length: 40 }, (_, i) => 'word' + i).join(' ');
        const doc = new Document({
          version: 1,
          refs: {
            LONG_REF: longText,
            JSON_REF: JSON.stringify({ access: 'a.b.c', refresh: 'r', expires: 1760000000000, note: 'has: colon # and hash' }),
            MULTI_REF: 'line1\nline2\n',
          },
          records: {
            'llm-pi-ai/openai-codex': { kind: 'grant', payload: { type: 'oauth', access: 'x.y.z', expires: 1760000000000, accountId: 'u-1', note: longText, list: [1, 'two', null, true, { k: 'v' }], empty: {}, none: null } },
            'openai/api': { kind: 'api-key', key: 'sk-test', env: { OPENAI_API_KEY: 'v' } },
          },
        });
        writeFileSync(file, String(doc));
        """;

    [Fact]
    public void WeParseWhatDshsYamlWriterProduces()
    {
        if (Prerequisites() is not { } req) { return; }
        var yaml = Homes().Select(h => h + YamlPackageIndex).FirstOrDefault(File.Exists);
        if (yaml is null) { return; }
        using var temp = new TempDir();
        var file = temp.File(".credentials.yaml");
        using (var p = StartNode(req.Node, YamlWriterScript, yaml, file))
        {
            Assert.True(p.WaitForExit(15000));
            Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        }

        var doc = CredentialsDocument.Load(file);
        var longText = string.Join(' ', Enumerable.Range(0, 40).Select(i => "word" + i));

        Assert.Equal(longText, doc.GetRef("LONG_REF"));
        Assert.Equal("line1\nline2\n", doc.GetRef("MULTI_REF"));
        Assert.Contains("\"note\":\"has: colon # and hash\"", doc.GetRef("JSON_REF"));
        var payload = doc.GetGrantPayload("llm-pi-ai/openai-codex")!;
        Assert.Equal(longText, payload["note"]!.GetValue<string>());
        Assert.Equal(1760000000000L, payload["expires"]!.GetValue<long>());
        Assert.Equal(5, payload["list"]!.AsArray().Count);
        Assert.Null(payload["none"]);

        // And editing it in place keeps everything else as dsh wrote it.
        var before = File.ReadAllText(file);
        CredentialsDocument.Update(file, d => d.SetGrant("llm-pi-ai/openai-codex", Fixtures.Grant("n.e.w", 5)));
        var after = File.ReadAllText(file);
        Assert.StartsWith(before[..before.IndexOf("  llm-pi-ai/openai-codex:", StringComparison.Ordinal)], after);
        Assert.EndsWith(before[before.IndexOf("  openai/api:", StringComparison.Ordinal)..], after);
    }

    private static string? FindDshBin()
        => Homes().Select(h => h + "/.local/share/dsh-tauri/dependencies/dsh/node_modules/@deepseek-ai/dsh/lib/bin.js").FirstOrDefault(File.Exists);

    [Fact]
    public async Task RuntimeVerifierSeesAProviderWeWroteThroughDshsOwnDumpConfig()
    {
        if (Prerequisites() is not { } req || FindDshBin() is not { } bin) { return; }
        using var temp = new TempDir();
        var root = temp.File("dsh");
        Directory.CreateDirectory(root);
        // `web` is a shipped profile template: dsh creates it on first use inside our temporary home.
        var home = DshHome.Resolve(root, "web", _ => null, temp.Path);
        var launcher = new DshLauncher(req.Node, new[] { bin });
        var verifier = new DshRuntimeVerifier(() => launcher, TimeSpan.FromSeconds(60));
        Assert.Equal(ProviderRuntimeState.Pending, (await verifier.VerifyAsync(home, "api-test", "openai-completions")).State);

        var provider = new JsonObject
        {
            ["displayName"] = "Test",
            ["apiKeyEnv"] = "API_TEST_API_KEY",
            ["api"] = "openai-completions",
            ["baseURL"] = "https://api.example.test/v1",
            ["models"] = new JsonArray(new JsonObject { ["id"] = "m1", ["name"] = "M One" }),
        };
        ProfilePatchDocument.Update(home.PatchPath, d => d.SetProvider("api-test", provider));

        var applied = await verifier.VerifyAsync(home, "api-test", "openai-completions");
        var wrongProtocol = await verifier.VerifyAsync(home, "api-test", "openai-responses");

        Assert.Equal(ProviderRuntimeState.Applied, applied.State);
        Assert.Equal(DshRuntimeVerifier.AppliedMessage, applied.Message);
        Assert.Equal(ProviderRuntimeState.Pending, wrongProtocol.State);
        // The verification never touched the credentials or anything outside the temporary home.
        Assert.False(File.Exists(home.CredentialsPath));
    }

    [Fact]
    public async Task RuntimeVerifierReportsUnavailableForAMissingOrFailingDsh()
    {
        using var temp = new TempDir();
        var home = DshHome.Resolve(temp.File("dsh"), "tauri", _ => null, temp.Path);

        var missing = await new DshRuntimeVerifier(() => null).VerifyAsync(home, "p", "openai-completions");
        Assert.Equal(ProviderRuntimeState.Unavailable, missing.State);
        Assert.Contains("未能校验", missing.Message);

        if (Prerequisites() is not { } req) { return; }
        var failing = new DshLauncher(req.Node, new[] { "-e", "process.exit(3)", "--" });
        var failed = await new DshRuntimeVerifier(() => failing).VerifyAsync(home, "p", "openai-completions");
        Assert.Equal(ProviderRuntimeState.Unavailable, failed.State);

        var hanging = new DshLauncher(req.Node, new[] { "-e", "setTimeout(()=>{}, 60000)", "--" });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = await new DshRuntimeVerifier(() => hanging, TimeSpan.FromMilliseconds(700)).VerifyAsync(home, "p", "openai-completions");
        Assert.Equal(ProviderRuntimeState.Unavailable, timedOut.State);
        Assert.True(clock.ElapsedMilliseconds < 10000);
    }

    [Fact]
    public void DumpConfigOutputWithCustomTagsIsParsed()
    {
        const string dump = """
            # == @deepseek-ai/dsh-base
            - id: plugin-manager
              disabled: !!js '!ctx.get(''profileContext'')'
            - id: llm-pi-ai
              config:
                providers:
                  p1:
                    api: openai-responses
                    baseURL: https://x.example.test/v1
            """;

        Assert.Equal("openai-responses", DshRuntimeVerifier.ReadProviderApi(dump, "p1", out var found));
        Assert.True(found);
        Assert.Null(DshRuntimeVerifier.ReadProviderApi(dump, "other", out var missing));
        Assert.False(missing);
    }

    [Fact]
    public async Task ScannerReadsDshsRealComposedConfigAndKnowsItsBuiltInRoutes()
    {
        if (Prerequisites() is not { } req || FindDshBin() is not { } bin) { return; }
        using var temp = new TempDir();
        var root = temp.File("dsh");
        Directory.CreateDirectory(root);
        var home = DshHome.Resolve(root, "web", _ => null, temp.Path);
        var verifier = new DshRuntimeVerifier(() => new DshLauncher(req.Node, new[] { bin }), TimeSpan.FromSeconds(60));
        Assert.NotNull(await verifier.DumpConfigAsync(home)); // creates the profile from dsh's shipped template
        File.WriteAllText(home.PatchPath,
            "- id: llm-pi-ai\n  config:\n    providers:\n      p1:\n        api: openai-completions\n        baseURL: https://p1.example.test/v1\n        models: [{id: m1}]\n"
            + "- id: agent-default-model\n  config:\n    provider: p1\n    model: m1\n"
            + "- id: subagent-model-selection-settings\n  config:\n    allowedModels:\n      - provider: gone\n        model: m2\n      - provider: deepseek-official\n        model: deepseek-flash\n");

        var scan = await new ReferenceScanner(verifier).ScanAsync(home);

        Assert.True(scan.ComposedAvailable);
        Assert.False(scan.Uncertain);
        Assert.Contains("p1", scan.KnownProviders);
        Assert.Contains("deepseek-official", scan.KnownProviders);
        var dangling = Assert.Single(scan.Dangling);
        Assert.Equal(("gone", "m2", true), (dangling.Provider, dangling.Model, dangling.Editable));
        Assert.Contains(scan.References, r => r.Provider == "p1" && r.Model == "m1" && !r.Dangling);
        // Whatever the shipped layers reference is either known or reported; nothing outside dsh's real routes is silently accepted.
        Assert.All(scan.References.Where(r => !r.Editable), r => Assert.Equal(!scan.KnownProviders.Contains(r.Provider), r.Dangling));
    }
}
