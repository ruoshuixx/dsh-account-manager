using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Quota;

namespace AccountManager.Core.Dsh;

/// <summary>One model of the list OpenAI returns for a ChatGPT account (only the fields this program uses).</summary>
public sealed record CodexAccountModel(
    string Slug,
    string DisplayName,
    string Visibility,
    int Priority,
    int? ContextWindow,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> ReasoningEfforts,
    bool SupportsFast);

/// <summary>The outcome of asking OpenAI for the account's models; <see cref="Reason"/> says why it failed (never contains a token).</summary>
public sealed record CodexModelListResult(bool Ok, IReadOnlyList<CodexAccountModel> Models, string Reason)
{
    public static CodexModelListResult Failure(string reason) => new(false, Array.Empty<CodexAccountModel>(), reason);
}

/// <summary>The access token (and its ChatGPT account id) a model-list request is made with.</summary>
public sealed record CodexTokenPick(string Access, string AccountId, bool IsCurrentAccount);

/// <summary>
/// Which Codex models dsh should offer. The user's rule (decided 2026-10-26): take every model the account lists, except old ones, i.e.
/// <c>gpt-6-sol</c> and every <c>gpt-5*</c> id. Only models OpenAI marks <c>visibility: "list"</c> that take text input are offered, in
/// OpenAI's own <c>priority</c> order.
/// </summary>
public static class CodexModelPolicy
{
    /// <summary>Exact ids that are never offered.</summary>
    public static readonly IReadOnlyList<string> ExcludedExactIds = new[] { "gpt-6-sol" };

    /// <summary>Id prefixes that are never offered (the whole gpt-5 family).</summary>
    public static readonly IReadOnlyList<string> ExcludedPrefixes = new[] { "gpt-5" };

    public static bool IsExcluded(string slug)
        => ExcludedExactIds.Contains(slug, StringComparer.Ordinal)
           || ExcludedPrefixes.Any(p => slug.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The models to offer, ordered by OpenAI's priority (a stable sort: ties keep the order of the response).</summary>
    public static IReadOnlyList<CodexAccountModel> Select(IEnumerable<CodexAccountModel> models)
        => models
            .Where(m => string.Equals(m.Visibility, "list", StringComparison.OrdinalIgnoreCase)
                        && m.Input.Contains("text", StringComparer.OrdinalIgnoreCase)
                        && !IsExcluded(m.Slug))
            .Select((m, index) => (m, index))
            .OrderBy(x => x.m.Priority).ThenBy(x => x.index)
            .Select(x => x.m)
            .GroupBy(m => m.Slug, StringComparer.Ordinal).Select(g => g.First())
            .ToList();

    /// <summary>"GPT-6-Astra" reads "GPT-6 Astra" (the way dsh's own catalog spells it); other names are kept.</summary>
    public static string NormalizeName(string? displayName, string slug)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name)) { return slug; }
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(GPT-\d+(?:\.\d+)*)-([A-Za-z].*)$");
        return match.Success ? match.Groups[1].Value + " " + match.Groups[2].Value : name;
    }
}

/// <summary>
/// Asks OpenAI which Codex models a ChatGPT account may use: one read-only HTTPS GET of
/// <c>https://chatgpt.com/backend-api/codex/models</c> with the account's own access token (the same call Codex makes), no redirects,
/// a 15 second timeout, a bounded response and fixed safe error texts that never echo the token or the body. It is made through the
/// same kind of <see cref="HttpClient"/> as the quota query, so proxy settings apply the same way. The HTTP handler can be replaced
/// in tests. Tokens are never refreshed here (a refresh would rotate the refresh token and break the same account elsewhere).
/// </summary>
public class CodexModelCatalogClient
{
    public const string Endpoint = "https://chatgpt.com/backend-api/codex/models";

    /// <summary>The Codex client version the endpoint expects in <c>client_version</c> (it gates models by their minimal client version).</summary>
    public const string ClientVersion = "0.159.0";

    /// <summary>The <c>originator</c> header dsh's own Codex traffic uses.</summary>
    public const string Originator = "pi";

    public const int TimeoutSeconds = 15;
    public const int MaxBytes = 1048576;

    /// <summary>A token must be valid for at least this long to be used for the current dsh account.</summary>
    public static readonly TimeSpan CurrentTokenMinimumValidity = TimeSpan.FromMinutes(10);

    /// <summary>Another stored account's token only has to be unexpired for this long.</summary>
    public static readonly TimeSpan OtherTokenMinimumValidity = TimeSpan.FromMinutes(1);

    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler);
    }

    private readonly HttpClient _http;
    private readonly string _userAgent;

    /// <param name="userAgent">Sent as <c>user-agent</c>, e.g. <c>dsh-account-manager/0.1.5</c>.</param>
    /// <param name="http">Test seam: a client with a fake handler.</param>
    public CodexModelCatalogClient(string userAgent = "dsh-account-manager", HttpClient? http = null)
    {
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "dsh-account-manager" : userAgent.Trim();
        _http = http ?? SharedClient;
    }

    /// <summary>Fetches and parses the account's models. Never throws for a network or server problem; the reason is in the result.</summary>
    public virtual async Task<CodexModelListResult> FetchAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { return CodexModelListResult.Failure("缺少访问令牌"); }
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "?client_version=" + ClientVersion);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId)) { request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId); }
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("originator", Originator);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400) { return CodexModelListResult.Failure("请求被重定向，出于安全考虑已中止"); }
            if (status is < 200 or >= 300) { return CodexModelListResult.Failure(Describe(status)); }
            var text = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (text is null) { return CodexModelListResult.Failure("响应过大，已中止解析"); }
            var models = Parse(text);
            return models is null ? CodexModelListResult.Failure("响应无法识别（接口可能已变更）") : new CodexModelListResult(true, models, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return CodexModelListResult.Failure("请求超时"); }
        catch (HttpRequestException) { return CodexModelListResult.Failure("网络不可达"); }
    }

    private static string Describe(int status) => status switch
    {
        401 => "令牌被拒绝（可能已过期）",
        403 => "访问被拒绝",
        429 => "请求过于频繁（HTTP 429）",
        >= 500 and <= 599 => "服务暂时不可用（HTTP " + status + "）",
        _ => "请求失败（HTTP " + status + "）",
    };

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxBytes) { return null; }
            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    // ---- parsing ----

    /// <summary>
    /// Parses <c>{ "models": [ … ] }</c>; null when the text is not that. A model without a slug is skipped. Every other field is optional:
    /// a missing <c>visibility</c> means "not listed", a missing <c>input_modalities</c> means text only.
    /// </summary>
    public static IReadOnlyList<CodexAccountModel>? Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj || obj["models"] is not JsonArray array) { return null; }

        var models = new List<CodexAccountModel>();
        foreach (var node in array)
        {
            if (node is not JsonObject m) { continue; }
            var slug = Str(m, "slug");
            if (string.IsNullOrWhiteSpace(slug)) { continue; }
            var input = Strings(m["input_modalities"]);
            var efforts = new List<string>();
            if (m["supported_reasoning_levels"] is JsonArray levels)
            {
                foreach (var level in levels)
                {
                    var effort = level is JsonObject lo ? Str(lo, "effort") : level is JsonValue lv && lv.TryGetValue<string>(out var s) ? s : null;
                    if (!string.IsNullOrWhiteSpace(effort) && !efforts.Contains(effort)) { efforts.Add(effort); }
                }
            }

            var fast = m["service_tiers"] is JsonArray tiers && tiers.Any(t => t is JsonObject to && string.Equals(Str(to, "id"), "priority", StringComparison.OrdinalIgnoreCase))
                       || Strings(m["additional_speed_tiers"]).Contains("fast", StringComparer.OrdinalIgnoreCase);
            var context = JsonNum.ToNumber(m["context_window"]);
            var priority = JsonNum.ToNumber(m["priority"]);
            models.Add(new CodexAccountModel(
                slug.Trim(),
                Str(m, "display_name") ?? string.Empty,
                Str(m, "visibility") ?? string.Empty,
                priority is { } p && p >= int.MinValue && p <= int.MaxValue ? (int)p : int.MaxValue,
                context is { } c && c > 0 && c <= int.MaxValue ? (int)c : null,
                input.Count == 0 ? new[] { "text" } : input,
                efforts,
                fast));
        }

        return models;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string> Strings(JsonNode? node)
        => node is JsonArray a ? a.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() : new List<string>();

    // ---- which token ----

    /// <summary>
    /// The token to ask with: dsh's current Codex account when its access token is valid for at least ten more minutes, otherwise another
    /// stored account's unexpired access token. Null when there is none. Nothing here refreshes a token or contacts anyone.
    /// </summary>
    public static CodexTokenPick? PickToken(IEnumerable<JsonNode?>? credentials, DateTimeOffset now)
    {
        if (credentials is null) { return null; }
        CodexTokenPick? other = null;
        foreach (var credential in credentials)
        {
            if (credential is null || JsonMerge.GetString(credential, "integrationID") != "openai") { continue; }
            var value = JsonMerge.Get(credential, "value");
            var access = JsonMerge.GetString(value, "access");
            if (string.IsNullOrWhiteSpace(access)) { continue; }
            var expires = ExpiresAt(value, access);
            if (expires is null) { continue; }
            var accountId = CredentialHelpers.GetIdentityInfo(value).AccountID;
            if (string.IsNullOrWhiteSpace(accountId)) { continue; }

            var active = credential is JsonObject co && co["active"] is JsonValue av && av.TryGetValue<bool>(out var isActive) && isActive;
            if (active)
            {
                if (expires.Value - now >= CurrentTokenMinimumValidity) { return new CodexTokenPick(access, accountId, true); }
            }
            else if (other is null && expires.Value - now >= OtherTokenMinimumValidity)
            {
                other = new CodexTokenPick(access, accountId, false);
            }
        }

        return other;
    }

    private static DateTimeOffset? ExpiresAt(JsonNode? value, string access)
    {
        var stored = JsonNum.ToLong(JsonMerge.Get(value, "expires"));
        if (stored is > 0)
        {
            // Milliseconds since the epoch (dsh's grant); tolerate seconds.
            return stored > 100_000_000_000L ? DateTimeOffset.FromUnixTimeMilliseconds(stored.Value) : DateTimeOffset.FromUnixTimeSeconds(stored.Value);
        }

        var exp = JsonNum.ToNumber(JsonMerge.Get(Jwt.ReadPayload(access), "exp"));
        return exp is > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)exp.Value) : null;
    }
}
