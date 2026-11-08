using System.Text.Json.Nodes;
using AccountManager.Core.Sharing;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class CodexProjectStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-project-state-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, ".codex-global-state.json");
    private string BackupPath => StatePath + ".ai-account-manager.bak";

    public CodexProjectStateTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(StatePath))!;

    private static JsonObject Globals(JsonObject root, string owner)
        => (JsonObject)root["electron-account-renderer-state-v1"]!["inactive"]![owner]!["globals"]!;

    [Fact]
    public void SharesAllLocalKeysInBothDirectionsWithActiveAndThenJsonOrderWinning()
    {
        const string before = """
            {
              "local-projects": {"p1":{"name":"active","rootPaths":["/workspace/one"]}},
              "project-order": ["p1"],
              "thread-project-assignments": {"t1":{"projectKind":"local","projectId":"p1"}},
              "thread-project-membership-host-ids": {"t1":"local"},
              "app-server-project-id-by-legacy-project-id-by-host": {"local:test":{"p1":"server1"}},
              "app-server-projects-migration-by-host": {"local:test":{"p1":true}},
              "pinned-project-ids": ["p1"],
              "sidebar-project-thread-orders": {"p1":["t1"]},
              "project-appearances": {"p1":{"color":"red"}},
              "projectless-thread-ids": ["loose1"],
              "electron-saved-workspace-roots": ["/workspace/one"],
              "electron-workspace-root-labels": {"/workspace/one":"one"},
              "project-writable-roots": {"p1":["/workspace/one"]},
              "thread-writable-roots": {"t1":["/workspace/one"]},
              "thread-workspace-root-hints": {"t1":"/workspace/one"},
              "remote-projects": {"r1":{"keep":true}},
              "selected-remote-host-id": "host-active",
              "desktop-app-beta-preferences-by-account-id": {"scope-a":{"beta":true}},
              "electron-persisted-atom-state": {"account-specific":17},
              "unknown": [1,2.0,{"literal":"untouched"}],
              "electron-account-renderer-state-v1": {
                "owner":"scope-a", "extra":{"keep":true}, "inactive": {
                  "scope-z": {"atoms":{"selected":"z"}, "extra":[1], "globals":{
                    "local-projects":{"p2":{"name":"first inactive"},"p1":{"name":"loser","extra":true}},
                    "project-order":["p2","p1"],
                    "thread-project-assignments":{"t2":{"projectKind":"local","projectId":"p2"}},
                    "thread-project-membership-host-ids":{"t2":"local"},
                    "app-server-project-id-by-legacy-project-id-by-host":{"local:test":{"p2":"server2"}},
                    "app-server-projects-migration-by-host":{"local:test":{"p2":true}},
                    "pinned-project-ids":["p2"], "sidebar-project-thread-orders":{"p2":["t2"]},
                    "project-appearances":{"p2":{"color":"blue"}}, "projectless-thread-ids":["loose2"],
                    "electron-saved-workspace-roots":["/workspace/two"],
                    "electron-workspace-root-labels":{"/workspace/two":"two"},
                    "project-writable-roots":{"p2":["/workspace/two"]},
                    "thread-writable-roots":{"t2":["/workspace/two"]},
                    "thread-workspace-root-hints":{"t2":"/workspace/two"},
                    "remote-projects":{"r2":{}}, "selected-remote-host-id":"host-z", "unknown":{"keep":2.00}
                  }},
                  "scope-b": {"atoms":{}, "globals":{
                    "local-projects":{"p3":{"name":"third"},"p2":{"name":"loser"}}, "project-order":["p3","p2"]
                  }}
                }
              }
            }
            """;
        File.WriteAllText(StatePath, before);
        var original = Read();

        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out var note));

        var root = Read();
        Assert.Equal("已让各账号共用 3 个本地项目", note);
        Assert.Equal(before, File.ReadAllText(BackupPath));
        Assert.Equal("""["p1","p2","p3"]""", root["project-order"]!.ToJsonString());
        Assert.Equal("active", root["local-projects"]!["p1"]!["name"]!.GetValue<string>());
        Assert.Null(root["local-projects"]!["p1"]!["extra"]);
        Assert.Equal("first inactive", root["local-projects"]!["p2"]!["name"]!.GetValue<string>());
        Assert.Equal("server2", root["app-server-project-id-by-legacy-project-id-by-host"]!["local:test"]!["p2"]!.GetValue<string>());
        Assert.Equal("""["/workspace/one","/workspace/two"]""", root["electron-saved-workspace-roots"]!.ToJsonString());
        var sharedKeys = root.Select(p => p.Key).Except(new[]
        {
            "electron-account-renderer-state-v1", "remote-projects", "selected-remote-host-id",
            "desktop-app-beta-preferences-by-account-id", "electron-persisted-atom-state", "unknown",
        }).ToArray();
        Assert.Equal(15, sharedKeys.Length);
        foreach (var owner in new[] { "scope-z", "scope-b" })
        {
            var globals = Globals(root, owner);
            foreach (var key in sharedKeys) { Assert.True(JsonNode.DeepEquals(root[key], globals[key]), key); }
            foreach (var (key, value) in Globals(original, owner).Where(p => !sharedKeys.Contains(p.Key)))
            {
                Assert.Equal(value?.ToJsonString(), globals[key]?.ToJsonString());
            }
            Assert.Equal(original["electron-account-renderer-state-v1"]!["inactive"]![owner]!["atoms"]!.ToJsonString(),
                root["electron-account-renderer-state-v1"]!["inactive"]![owner]!["atoms"]!.ToJsonString());
        }
        foreach (var key in new[] { "remote-projects", "selected-remote-host-id", "desktop-app-beta-preferences-by-account-id", "electron-persisted-atom-state", "unknown" })
        {
            Assert.Equal(original[key]!.ToJsonString(), root[key]!.ToJsonString());
        }
        Assert.Equal("scope-a", root["electron-account-renderer-state-v1"]!["owner"]!.GetValue<string>());
        Assert.Equal(original["electron-account-renderer-state-v1"]!["extra"]!.ToJsonString(), root["electron-account-renderer-state-v1"]!["extra"]!.ToJsonString());
    }

    [Theory]
    [InlineData("codex.chatGptBacking", false)]
    [InlineData("codex.chatGptBackingCreation", false)]
    [InlineData("codex.retiredChatGptBacking", false)]
    [InlineData("codex.chatGptBacking", true)]
    [InlineData("codex.chatGptBackingCreation", true)]
    [InlineData("codex.retiredChatGptBacking", true)]
    public void CloudProjectsAndNonlocalMembershipsStayOnlyInTheirOriginalScope(string cloudKey, bool serialized)
    {
        var root = JsonNode.Parse("""
            {
              "local-projects":{"cloud-a":{"metadata":{}},"local":{"name":"local"}},
              "thread-project-assignments":{"remote":{"projectKind":"remote","projectId":"r"},"missing-kind":{"projectId":"x"}},
              "thread-project-membership-host-ids":{"remote":"remote-host","unknown":null},
              "electron-account-renderer-state-v1":{"owner":null,"inactive":{
                "scope-b":{"atoms":{},"globals":{
                  "local-projects":{"cloud-b":{},"local-b":{}},
                  "thread-project-assignments":{"cloud":{"projectKind":"cloud","projectId":"c"},"local":{"projectKind":"local","projectId":"local-b"}},
                  "thread-project-membership-host-ids":{"cloud":"cloud-host","local":"local"}
                }}
              }}
            }
            """)!.AsObject();
        if (serialized)
        {
            var metadata = new JsonObject { [cloudKey] = null }.ToJsonString();
            root["local-projects"]!["cloud-a"]!["metadata"] = metadata;
            Globals(root, "scope-b")["local-projects"]!["cloud-b"]!["metadata"] = metadata;
        }
        else
        {
            root["local-projects"]!["cloud-a"]!["metadata"]![cloudKey] = null;
            Globals(root, "scope-b")["local-projects"]!["cloud-b"]![cloudKey] = new JsonObject();
        }
        File.WriteAllText(StatePath, root.ToJsonString());

        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out _));

        root = Read();
        var parked = Globals(root, "scope-b");
        Assert.Null(root["local-projects"]!["cloud-b"]);
        Assert.Null(parked["local-projects"]!["cloud-a"]);
        Assert.NotNull(root["local-projects"]!["cloud-a"]);
        Assert.NotNull(parked["local-projects"]!["cloud-b"]);
        Assert.NotNull(root["local-projects"]!["local-b"]);
        Assert.NotNull(parked["local-projects"]!["local"]);
        Assert.Null(root["thread-project-assignments"]!["cloud"]);
        Assert.Null(parked["thread-project-assignments"]!["remote"]);
        Assert.Null(parked["thread-project-assignments"]!["missing-kind"]);
        Assert.Null(root["thread-project-membership-host-ids"]!["cloud"]);
        Assert.Null(parked["thread-project-membership-host-ids"]!["remote"]);
        Assert.Null(parked["thread-project-membership-host-ids"]!["unknown"]);
    }

    [Fact]
    public void ExistingNonlocalEntriesWinOverALocalEntryWithTheSameId()
    {
        File.WriteAllText(StatePath, """
            {"local-projects":{"p":{"codex.chatGptBacking":{}}},
             "thread-project-assignments":{"t":{"projectKind":"remote"}},
             "thread-project-membership-host-ids":{"t":"remote"},
             "electron-account-renderer-state-v1":{"owner":"scope-a","inactive":{
               "scope-b":{"atoms":{},"globals":{"local-projects":{"p":{"name":"local"}},
                 "thread-project-assignments":{"t":{"projectKind":"local","projectId":"p"}},
                 "thread-project-membership-host-ids":{"t":"local"}}},
               "scope-c":{"atoms":{},"globals":{}}
             }}}
            """);

        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out _));

        var root = Read();
        Assert.NotNull(root["local-projects"]!["p"]!["codex.chatGptBacking"]);
        Assert.Equal("remote", root["thread-project-assignments"]!["t"]!["projectKind"]!.GetValue<string>());
        Assert.Equal("remote", root["thread-project-membership-host-ids"]!["t"]!.GetValue<string>());
        Assert.Equal("local", Globals(root, "scope-c")["local-projects"]!["p"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void PendingDeletionKeysAndProjectIdValuesFromAnyHostAreNeverCopied()
    {
        File.WriteAllText(StatePath, """
            {
              "local-projects":{"deleted-by-key":{},"kept":{}}, "project-order":["deleted-by-key","kept"],
              "thread-project-assignments":{"t1":{"projectKind":"local","projectId":"deleted-by-key"}},
              "app-server-project-id-by-legacy-project-id-by-host":{"local:test":{"alias":"deleted-by-value"}},
              "electron-account-renderer-state-v1":{"owner":"scope-a","inactive":{
                "scope-b":{"atoms":{},"globals":{
                  "app-server-pending-project-deletions-by-host":{"remote:any":{"deleted-by-key":{},"request-id":{"projectId":"deleted-by-value"}}},
                  "local-projects":{"deleted-by-value":{}}, "project-order":["deleted-by-value"],
                  "pinned-project-ids":["deleted-by-value"], "project-appearances":{"deleted-by-value":{"color":"red"}}
                }},
                "scope-c":{"atoms":{},"globals":{}}
              }}
            }
            """);
        var before = Read();

        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out _));

        var root = Read();
        var b = Globals(root, "scope-b");
        var c = Globals(root, "scope-c");
        Assert.NotNull(root["local-projects"]!["deleted-by-key"]);
        Assert.NotNull(b["local-projects"]!["deleted-by-value"]);
        Assert.Null(root["local-projects"]!["deleted-by-value"]);
        Assert.Null(b["local-projects"]!["deleted-by-key"]);
        Assert.Null(c["local-projects"]!["deleted-by-key"]);
        Assert.Null(c["local-projects"]!["deleted-by-value"]);
        Assert.NotNull(c["local-projects"]!["kept"]);
        Assert.Equal("""["kept"]""", c["project-order"]!.ToJsonString());
        Assert.Empty(c["thread-project-assignments"]!.AsObject());
        Assert.Empty(c["pinned-project-ids"]!.AsArray());
        Assert.Empty(c["project-appearances"]!.AsObject());
        Assert.Empty(c["app-server-project-id-by-legacy-project-id-by-host"]!["local:test"]!.AsObject());
        Assert.Null(root["app-server-pending-project-deletions-by-host"]);
        Assert.Equal(Globals(before, "scope-b")["app-server-pending-project-deletions-by-host"]!.ToJsonString(), b["app-server-pending-project-deletions-by-host"]!.ToJsonString());
    }

    [Theory]
    [InlineData("{ \"local-projects\": {\"p\":{}} }\n")]
    [InlineData("{ \"electron-account-renderer-state-v1\": null }")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"inactive\":{}}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":5,\"inactive\":{}}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":false,\"inactive\":{}}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":null,\"inactive\":[]}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":null,\"inactive\":{\"b\":null}}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":null,\"inactive\":{\"b\":{\"globals\":{}}}}}")]
    [InlineData("{\"electron-account-renderer-state-v1\":{\"owner\":null,\"inactive\":{\"b\":{\"atoms\":{},\"globals\":[]}}}}")]
    [InlineData("[]")]
    [InlineData("invalid json")]
    public void MissingScopesOrInvalidStructureLeavesEveryByteUntouched(string before)
    {
        File.WriteAllText(StatePath, before);

        Assert.False(CodexProjectState.ShareLocalProjects(StatePath, out var note));

        Assert.Null(note);
        Assert.Equal(before, File.ReadAllText(StatePath));
        Assert.False(File.Exists(BackupPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ASecondRunDoesNotWriteOrReplaceTheBackupAndLaterChangesBackUpThePreviousFile()
    {
        File.WriteAllText(StatePath, """
            {"local-projects":{"p":{}},"electron-account-renderer-state-v1":{"owner":null,"inactive":{"b":{"atoms":{},"globals":{}}}}}
            """);
        var original = File.ReadAllBytes(StatePath);
        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out _));
        var shared = File.ReadAllBytes(StatePath);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(StatePath, stamp);
        File.SetLastWriteTimeUtc(BackupPath, stamp);

        Assert.False(CodexProjectState.ShareLocalProjects(StatePath, out var note));

        Assert.Null(note);
        Assert.Equal(shared, File.ReadAllBytes(StatePath));
        Assert.Equal(original, File.ReadAllBytes(BackupPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StatePath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(BackupPath));
        var next = Read();
        Globals(next, "b")["local-projects"]!["p2"] = new JsonObject();
        File.WriteAllText(StatePath, next.ToJsonString());
        var previous = File.ReadAllBytes(StatePath);
        Assert.True(CodexProjectState.ShareLocalProjects(StatePath, out _));
        Assert.Equal(previous, File.ReadAllBytes(BackupPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void MissingFileIsNotCreated()
    {
        Assert.False(CodexProjectState.ShareLocalProjects(StatePath, out var note));
        Assert.Null(note);
        Assert.Empty(Directory.GetFiles(_directory));
    }
}
