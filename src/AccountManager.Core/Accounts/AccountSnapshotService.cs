using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.Dsh;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Read-only snapshot of the local dsh state: the stored credentials and (when possible) the
/// provider configuration. Config failures keep the Codex rows and report a fixed warning
/// instead of pretending there are zero providers.
/// </summary>
public sealed class AccountSnapshotService
{
    private readonly IAccountBackend _api;
    private readonly AccountMapper _mapper;
    private readonly SecretRegistry _secrets;
    private readonly SubscriptionStore? _subscriptions;

    public AccountSnapshotService(IAccountBackend api, AccountMapper mapper, SecretRegistry secrets,
        SubscriptionStore? subscriptions = null)
    {
        _api = api;
        _mapper = mapper;
        _secrets = secrets;
        _subscriptions = subscriptions;
    }

    public async Task<AccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        JsonArray credentials;
        try
        {
            credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DshException)
        {
            throw;
        }
        catch
        {
            throw new DshException("无法读取 dsh 本地凭据列表；未生成账号快照。");
        }

        _secrets.Register(credentials);

        Dictionary<string, string> subscriptionFallback = new(StringComparer.Ordinal);
        if (_subscriptions is not null)
        {
            try
            {
                subscriptionFallback = await _subscriptions.LoadActiveUntilAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                subscriptionFallback = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        var accounts = _mapper.GetManagedAccounts(credentials, subscriptionFallback);

        var warning = string.Empty;
        var definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        try
        {
            var entries = await _api.GetConfigEntriesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry is null) { continue; }
                _secrets.Register(entry);
                _secrets.RegisterProviderSecrets(JsonMerge.Get(entry, "info"));
            }

            definitions = AccountMapper.GetProviderDefinitions(entries);
        }
        catch
        {
            warning = "供应商配置读取失败，已显示可读取的 Codex 账号。";
            definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        }

        if (string.IsNullOrEmpty(warning))
        {
            foreach (var (providerId, definition) in definitions)
            {
                var package = JsonMerge.GetString(definition, "package") ?? string.Empty;
                if (!OpenAiApiProtocol.IsManagedPackage(package)) { continue; }
                var keyCredentials = credentials.Where(c =>
                {
                    if (c is null) { return false; }
                    if (JsonMerge.GetString(c, "integrationID") != providerId) { return false; }
                    var value = JsonMerge.Get(c, "value");
                    return value is not null && JsonMerge.GetString(value, "type") == "key";
                }).ToList();

                if (keyCredentials.Count > 0)
                {
                    foreach (var credential in keyCredentials) { accounts.Add(_mapper.ConvertProviderOverview(providerId, definition, credential)); }
                }
                else
                {
                    accounts.Add(_mapper.ConvertProviderOverview(providerId, definition, null));
                }
            }
        }

        return new AccountSnapshot
        {
            Accounts = accounts,
            ProviderCount = definitions.Count,
            Warning = warning,
        };
    }
}
