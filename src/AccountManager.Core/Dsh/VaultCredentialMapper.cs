using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;

namespace AccountManager.Core.Dsh;

/// <summary>
/// Maps between the Cockpit-shaped account records of the <see cref="AccountVault"/> and the credential JSON
/// the upper layers consume, and builds the live grant payload dsh stores for its current Codex account.
/// A record keeps the credential's metadata verbatim in <c>credential_metadata</c> so a credential
/// survives the round trip exactly; the Cockpit top-level fields are derived from it for readability.
/// </summary>
public static class VaultCredentialMapper
{
    public const string DefaultMethodId = "chatgpt-browser";
    public const string IntegrationId = "openai";

    public static string NewCodexId() => "codex_" + Guid.NewGuid().ToString("N");

    public static string? IdOf(JsonObject record) => JsonMerge.GetString(record, "id");

    public static string AccessOf(JsonObject record) => JsonMerge.GetString(JsonMerge.Get(record, "tokens"), "access_token") ?? string.Empty;

    public static string RefreshOf(JsonObject record) => JsonMerge.GetString(JsonMerge.Get(record, "tokens"), "refresh_token") ?? string.Empty;

    public static string AccountIdOf(JsonObject record) => JsonMerge.GetString(record, "account_id") ?? string.Empty;

    public static string EmailOf(JsonObject record) => JsonMerge.GetString(record, "email") ?? string.Empty;

    public static long ExpiresOf(JsonObject record)
    {
        var stored = JsonNum.ToLong(JsonMerge.Get(record, "expires"));
        if (stored is > 0) { return stored.Value; }
        return ExpiryFromJwt(AccessOf(record));
    }

    /// <summary>Access-token expiry in epoch milliseconds from the JWT <c>exp</c> claim; 0 when unreadable.</summary>
    public static long ExpiryFromJwt(string? access)
    {
        var exp = JsonNum.ToNumber(JsonMerge.Get(Jwt.ReadPayload(access), "exp"));
        return exp is > 0 and <= 253402300799 ? (long)(exp.Value * 1000) : 0;
    }

    /// <summary>The grant payload dsh keeps: exactly <c>{ type, access, refresh, expires, accountId }</c>.</summary>
    public static JsonObject BuildGrantPayload(JsonObject record)
    {
        var accountId = AccountIdOf(record);
        if (string.IsNullOrEmpty(accountId))
        {
            accountId = JsonMerge.GetString(JsonMerge.Get(Jwt.ReadPayload(AccessOf(record)), "https://api.openai.com/auth"), "chatgpt_account_id") ?? string.Empty;
        }

        return new JsonObject
        {
            ["type"] = "oauth",
            ["access"] = AccessOf(record),
            ["refresh"] = RefreshOf(record),
            ["expires"] = ExpiresOf(record),
            ["accountId"] = accountId,
        };
    }

    /// <summary>Whether the live grant carries a later access-token expiry than the stored record.</summary>
    public static bool GrantIsNewer(JsonObject grant, JsonObject record)
    {
        var grantExpires = GrantExpires(grant);
        var recordExpires = ExpiresOf(record);
        return grantExpires > recordExpires;
    }

    public static long GrantExpires(JsonObject grant)
    {
        var stored = JsonNum.ToLong(JsonMerge.Get(grant, "expires"));
        return stored is > 0 ? stored.Value : ExpiryFromJwt(JsonMerge.GetString(grant, "access"));
    }

    /// <summary>Copies the grant's token trio into the record (dsh refreshed them) and returns whether anything changed.</summary>
    public static bool ApplyGrantTokens(JsonObject record, JsonObject grant)
    {
        var access = JsonMerge.GetString(grant, "access") ?? string.Empty;
        var refresh = JsonMerge.GetString(grant, "refresh") ?? string.Empty;
        if (access.Length == 0 || refresh.Length == 0) { return false; }
        var changed = false;
        var tokens = record["tokens"] as JsonObject;
        if (tokens is null) { tokens = new JsonObject(); record["tokens"] = tokens; }
        if (AccessOf(record) != access) { tokens["access_token"] = access; changed = true; }
        if (RefreshOf(record) != refresh) { tokens["refresh_token"] = refresh; changed = true; }
        var expires = GrantExpires(grant);
        if (expires > 0 && JsonNum.ToLong(JsonMerge.Get(record, "expires")) != expires) { record["expires"] = expires; changed = true; }
        return changed;
    }

    /// <summary>
    /// Index of the record that is dsh's current Codex account, or -1. Preference: matching refresh token,
    /// matching access token, then the only record with the grant's account id, or among several the one
    /// whose e-mail equals the access token's profile e-mail.
    /// </summary>
    public static int FindActive(IReadOnlyList<JsonObject> records, JsonObject? grant)
    {
        if (grant is null) { return -1; }
        var refresh = JsonMerge.GetString(grant, "refresh") ?? string.Empty;
        var access = JsonMerge.GetString(grant, "access") ?? string.Empty;
        var accountId = JsonMerge.GetString(grant, "accountId") ?? string.Empty;

        if (refresh.Length > 0)
        {
            for (var i = 0; i < records.Count; i++) { if (RefreshOf(records[i]) == refresh) { return i; } }
        }

        if (access.Length > 0)
        {
            for (var i = 0; i < records.Count; i++) { if (AccessOf(records[i]) == access) { return i; } }
        }

        if (accountId.Length == 0) { return -1; }
        var candidates = Enumerable.Range(0, records.Count).Where(i => AccountIdOf(records[i]) == accountId).ToList();
        if (candidates.Count == 1) { return candidates[0]; }
        if (candidates.Count == 0) { return -1; }
        var email = GrantEmail(grant);
        if (email.Length == 0) { return -1; }
        var byEmail = candidates.Where(i => string.Equals(EmailOf(records[i]), email, StringComparison.OrdinalIgnoreCase)).ToList();
        return byEmail.Count == 1 ? byEmail[0] : -1;
    }

    /// <summary>The profile e-mail inside the grant's access token (empty when absent).</summary>
    public static string GrantEmail(JsonObject grant)
    {
        var claims = Jwt.ReadPayload(JsonMerge.GetString(grant, "access"));
        return JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/profile"), "email")
            ?? JsonMerge.GetString(claims, "email") ?? string.Empty;
    }

    /// <summary>The credential JSON of one record; <paramref name="grant"/> overlays fresher tokens of the active account.</summary>
    public static JsonObject ToCredential(JsonObject record, bool active, JsonObject? activeGrant)
    {
        var access = AccessOf(record);
        var refresh = RefreshOf(record);
        var expires = ExpiresOf(record);
        if (active && activeGrant is not null && GrantIsNewer(activeGrant, record))
        {
            access = JsonMerge.GetString(activeGrant, "access") ?? access;
            refresh = JsonMerge.GetString(activeGrant, "refresh") ?? refresh;
            expires = GrantExpires(activeGrant);
        }

        var metadata = JsonMerge.Get(record, "credential_metadata") is JsonObject stored
            ? (JsonObject)stored.DeepClone()
            : DeriveMetadata(record);
        var email = EmailOf(record);
        var workspace = AccountIdOf(record);
        if (workspace.Length > 8) { workspace = workspace[^8..]; }
        var label = JsonMerge.GetString(record, "label");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = email.Length > 0 ? email + " [" + workspace + "]" : "Codex " + AccountIdOf(record);
        }

        return new JsonObject
        {
            ["id"] = IdOf(record),
            ["integrationID"] = IntegrationId,
            ["label"] = label,
            ["active"] = active,
            ["value"] = new JsonObject
            {
                ["type"] = "oauth",
                ["methodID"] = JsonMerge.GetString(record, "method_id") ?? DefaultMethodId,
                ["access"] = access,
                ["refresh"] = refresh,
                ["expires"] = expires,
                ["metadata"] = metadata,
            },
        };
    }

    private static JsonObject DeriveMetadata(JsonObject record)
    {
        var metadata = new JsonObject
        {
            ["accountID"] = AccountIdOf(record),
            ["cockpitEmail"] = EmailOf(record),
        };
        foreach (var name in new[] { "created_at", "last_used" })
        {
            if (JsonMerge.Get(record, name) is { } node) { metadata[name] = node.DeepClone(); }
        }

        var idToken = JsonMerge.GetString(JsonMerge.Get(record, "tokens"), "id_token");
        if (!string.IsNullOrWhiteSpace(idToken)) { metadata["id_token"] = idToken; }
        var plan = JsonMerge.GetString(record, "plan_type");
        if (!string.IsNullOrWhiteSpace(plan)) { metadata["planType"] = plan; }
        if (JsonMerge.Get(record, "subscription_active_until") is { } sub) { metadata["subscriptionActiveUntil"] = sub.DeepClone(); }
        return metadata;
    }

    /// <summary>
    /// The vault record for a credential. <paramref name="existing"/> contributes Cockpit fields the
    /// credential does not carry (so a token update never drops them).
    /// </summary>
    public static JsonObject ToRecord(string id, string label, JsonObject value, JsonObject? existing)
    {
        var record = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
        var metadata = JsonMerge.Get(value, "metadata") as JsonObject ?? new JsonObject();
        var info = CredentialHelpers.GetIdentityInfo(value);
        var access = JsonMerge.GetString(value, "access") ?? string.Empty;

        record["id"] = id;
        record["label"] = label;
        record["auth_mode"] = "oauth";
        record["method_id"] = JsonMerge.GetString(value, "methodID") ?? DefaultMethodId;
        record["email"] = info.Email;
        record["account_id"] = info.AccountID;
        var plan = JsonMerge.GetString(metadata, "planType") ?? JsonMerge.GetString(metadata, "plan");
        if (string.IsNullOrWhiteSpace(plan))
        {
            plan = JsonMerge.GetString(JsonMerge.Get(Jwt.ReadPayload(access), "https://api.openai.com/auth"), "chatgpt_plan_type");
        }
        if (!string.IsNullOrWhiteSpace(plan)) { record["plan_type"] = plan; } else { record.Remove("plan_type"); }

        record["tokens"] = new JsonObject
        {
            ["access_token"] = access,
            ["refresh_token"] = JsonMerge.GetString(value, "refresh") ?? string.Empty,
            ["id_token"] = JsonMerge.GetString(metadata, "id_token") ?? JsonMerge.GetString(value, "id_token") ?? string.Empty,
        };

        var expires = JsonNum.ToLong(JsonMerge.Get(value, "expires"));
        record["expires"] = expires is > 0 ? expires.Value : ExpiryFromJwt(access);

        foreach (var name in new[] { "created_at", "last_used" })
        {
            if (JsonMerge.Get(metadata, name) is { } node) { record[name] = node.DeepClone(); }
        }

        var subscription = JsonMerge.Get(metadata, "subscriptionActiveUntil") ?? JsonMerge.Get(metadata, "subscription_active_until");
        if (subscription is not null) { record["subscription_active_until"] = subscription.DeepClone(); } else { record.Remove("subscription_active_until"); }

        record["credential_metadata"] = metadata.DeepClone();
        return record;
    }
}
