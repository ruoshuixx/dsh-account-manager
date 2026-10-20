using System.Text.Json.Nodes;

namespace AccountManager.Core.Tests.Dsh;

/// <summary>Short-lived private temp directory; tests never touch the real ~/.dsh.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dshT-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

internal static class Fixtures
{
    /// <summary>Equivalent to the real layout, with fake values only.</summary>
    public const string Credentials = """
        # managed by the test
        version: 1
        refs:
          ARK_CODING_API_KEY: fake-ark-key-123
          CODEX_OAUTH_1: '{"access":"fake.a","refresh":"fake_r","expires":1760000000000}'
          # kept comment between refs
          CODEX_OAUTH_2: "{\"x\":1}"
        records:
          llm-pi-ai/openai-codex:
            kind: grant
            payload:
              type: oauth
              access: fake.access.jwt
              refresh: fake_refresh
              expires: 1760000000000
              accountId: 11111111-2222-3333-4444-555555555555
          client-connection/browser-session:
            kind: grant
            payload:
              cookie: abc
              nested:
                list: [1, 2, true, null]
                flag: false
                ratio: 0.5
          openai/api:
            kind: api-key
            key: fake-key
            env:
              OPENAI_API_KEY: fake-env
        x-foreign: 1

        """;

    public const string Patch = """
        # top comment kept
        - id: other-plugin
          config:
            a: 1   # inline comment
        # between entries
        - id: llm-pi-ai
          name: Pi AI
          config:
            providers:
              openai-codex:
                models: [gpt-5]
            other: keep
        - id: last-plugin
          enabled: true

        """;

    public static JsonObject Grant(string access, long expires, string accountId = "11111111-2222-3333-4444-555555555555")
        => new()
        {
            ["type"] = "oauth",
            ["access"] = access,
            ["refresh"] = "fake_refresh_new",
            ["expires"] = expires,
            ["accountId"] = accountId,
        };
}
