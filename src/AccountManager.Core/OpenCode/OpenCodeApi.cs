using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Security;

namespace AccountManager.Core.OpenCode;

/// <summary>The credential API boundary of OpenCode; replaceable in tests so no real OpenCode is ever touched.</summary>
public interface IOpenCodeCredentialApi
{
    Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default);

    Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null);

    Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default);

    Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thin typed wrapper over the OpenCode V2 HTTP API exposed through the CLI (<c>opencode api &lt;method&gt; &lt;path&gt;</c>).
/// All responses are registered with the secret registry before use.
/// </summary>
public sealed class OpenCodeApi : IOpenCodeCredentialApi
{
    private static readonly JsonSerializerOptions Compact = CoreJson.Compact;

    private readonly OpenCodeCli _cli;
    private readonly SecretRegistry _secrets;

    public OpenCodeApi(OpenCodeCli cli, SecretRegistry secrets)
    {
        _cli = cli;
        _secrets = secrets;
    }

    public OpenCodeCli Cli => _cli;

    public async Task<JsonNode?> InvokeAsync(string method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "api", method, path };
        if (body is not null)
        {
            _secrets.Register(body);
            args.Add("--data");
            args.Add(body.ToJsonString(Compact));
        }

        var text = await _cli.RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) { return null; }

        JsonNode? result;
        try
        {
            result = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new OpenCodeException("OpenCode 响应不是有效 JSON；请确认使用 OpenCode V2。");
        }

        if (result is not null) { _secrets.Register(result); }
        return result;
    }

    /// <summary>GET /api/credential -&gt; data array.</summary>
    public async Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync("get", "/api/credential", null, cancellationToken).ConfigureAwait(false);
        var data = JsonMerge.Get(response, "data");
        return data as JsonArray ?? throw new OpenCodeException("OpenCode 凭据列表响应格式无效；不会把读取失败当作没有账号。");
    }

    public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => InvokeAsync("post", "/api/credential/" + Uri.EscapeDataString(credentialId) + "/activate", null, cancellationToken);

    public async Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null)
    {
        var body = new JsonObject
        {
            ["integrationID"] = integrationId,
            ["label"] = label,
            ["value"] = value.DeepClone(),
            ["activate"] = activate,
        };
        if (!string.IsNullOrWhiteSpace(requestedId)) { body["id"] = requestedId; }
        var response = await InvokeAsync("post", "/api/credential", body, cancellationToken).ConfigureAwait(false);
        var id = JsonMerge.GetString(JsonMerge.Get(response, "data"), "id");
        return id ?? string.Empty;
    }

    public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => InvokeAsync("delete", "/api/credential/" + Uri.EscapeDataString(credentialId), null, cancellationToken);
}
