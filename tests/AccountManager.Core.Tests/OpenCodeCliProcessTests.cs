using System.Text.Json.Nodes;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// The OpenCode CLI wrapper against a fake <c>opencode</c> shell script in a temp directory (Linux only). The real
/// OpenCode and its data are never started or read.
/// </summary>
public class OpenCodeCliProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ocF-" + Guid.NewGuid().ToString("N")[..10]);

    public OpenCodeCliProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Script(string body)
    {
        var path = Path.Combine(_dir, "opencode");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private OpenCodeApi Api(string script, SecretRegistry? secrets = null)
    {
        secrets ??= new SecretRegistry(false);
        return new OpenCodeApi(new OpenCodeCli(new OpenCodeCliOptions { CliPath = script }, secrets), secrets);
    }

    [Fact]
    public async Task CredentialCallsUseTheDocumentedCliArguments()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var log = Path.Combine(_dir, "args.log");
        var script = Script($"printf '%s\\n' \"$*\" >> '{log}'\ncase \"$2\" in get) echo '{{\"data\":[{{\"id\":\"c1\",\"integrationID\":\"openai\",\"active\":true}}]}}';; post) echo '{{\"data\":{{\"id\":\"c2\"}}}}';; *) echo '{{}}';; esac");
        var api = Api(script);

        var list = await api.GetCredentialsAsync();
        var id = await api.CreateCredentialAsync("openai", "a@example.test", new JsonObject { ["type"] = "oauth" }, true, default, "c2");
        await api.ActivateCredentialAsync("c1");
        await api.DeleteCredentialAsync("c1");

        Assert.Single(list);
        Assert.Equal("c2", id);
        var lines = File.ReadAllLines(log);
        Assert.Equal("api get /api/credential", lines[0]);
        Assert.StartsWith("api post /api/credential --data ", lines[1]);
        Assert.Contains("\"activate\":true", lines[1]);
        Assert.Contains("\"id\":\"c2\"", lines[1]);
        Assert.Equal("api post /api/credential/c1/activate", lines[2]);
        Assert.Equal("api delete /api/credential/c1", lines[3]);
    }

    [Fact]
    public async Task FailuresNeverEchoTheServerOutput()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var script = Script("echo 'Unauthorized sk-live-should-never-show-up-in-messages-0123456789' >&2\nexit 3");
        var api = Api(script);

        var ex = await Assert.ThrowsAsync<OpenCodeException>(() => api.GetCredentialsAsync());

        Assert.DoesNotContain("sk-live", ex.Message);
        Assert.Contains("退出码 3", ex.Message);
        Assert.Contains("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task ANonJsonResponseIsRejectedWithoutBeingShown()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var api = Api(Script("echo 'not json secret-body'"));

        var ex = await Assert.ThrowsAsync<OpenCodeException>(() => api.GetCredentialsAsync());

        Assert.DoesNotContain("secret-body", ex.Message);
    }

    [Fact]
    public async Task ACredentialListThatIsNotAnArrayIsNeverTreatedAsNoAccounts()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var api = Api(Script("echo '{\"data\":{\"oops\":1}}'"));

        await Assert.ThrowsAsync<OpenCodeException>(() => api.GetCredentialsAsync());
    }

    [Fact]
    public async Task AMissingConfiguredCliFailsClosedWithAChineseMessage()
    {
        var secrets = new SecretRegistry(false);
        var api = new OpenCodeApi(new OpenCodeCli(new OpenCodeCliOptions { CliPath = Path.Combine(_dir, "nope") }, secrets), secrets);

        var ex = await Assert.ThrowsAsync<OpenCodeException>(() => api.GetCredentialsAsync());

        Assert.Contains("不存在或不可执行", ex.Message);
    }

    [Fact]
    public async Task TokensInRequestBodiesAreRegisteredForRedaction()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        var secrets = new SecretRegistry(false);
        var api = Api(Script("echo '{\"data\":{\"id\":\"c9\"}}'"), secrets);

        await api.CreateCredentialAsync("openai", "x", new JsonObject { ["type"] = "oauth", ["access"] = "ACCESS-VALUE-1234", ["refresh"] = "REFRESH-VALUE-5678" }, true);

        Assert.DoesNotContain("REFRESH-VALUE-5678", secrets.Redact("failed with REFRESH-VALUE-5678"));
    }
}
