using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class LivePlanTests
{
    private const string Advanced = "self_serve_business_prolite";
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    private static JsonObject Credential(long? issuedAt = null) => new()
    {
        ["id"] = "synthetic-credential",
        ["integrationID"] = "openai",
        ["value"] = new JsonObject
        {
            ["type"] = "oauth",
            ["methodID"] = "chatgpt-browser",
            ["access"] = TestAccounts.AccessToken("a@example.test", "acct-1", plan: Advanced, iat: issuedAt),
            ["metadata"] = new JsonObject { ["accountID"] = "acct-1", ["cockpitEmail"] = "a@example.test" },
        },
    };

    [Fact]
    public async Task AQuotaResponsePersistsTheDowngradeAndTheNextSnapshotShowsIt()
    {
        using var f = new DshFixture();
        var credential = Credential(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
        await f.Backend.CreateCredentialAsync("openai", "Synthetic", (JsonObject)credential["value"]!, activate: false);
        var row = Assert.Single((await f.Snapshot.GetSnapshotAsync()).Accounts);
        Assert.Equal(Advanced, row.Plan);
        var quota = new FakeQuota("team");
        var selector = new AccountQuotaSelector(f.Backend, quota, f.Secrets, store: f.Subscriptions);
        var before = DateTimeOffset.UtcNow;

        var credentials = (await f.Backend.GetCredentialsAsync()).OfType<JsonNode>().ToList();
        var result = await selector.GetResultAsync(row, credentials);

        Assert.Equal(QuotaResultState.Success, result.State);
        Assert.Equal("team", result.Usage!.Plan);
        Assert.Equal(1, quota.Calls);
        var stored = Assert.Single(await f.Subscriptions.LoadAsync()).Value;
        Assert.Equal("team", stored.PlanType);
        Assert.Equal(result.CheckedAt, stored.PlanObservedAt);
        Assert.True(stored.PlanObservedAt >= before);
        var rebuilt = Assert.Single((await f.Snapshot.GetSnapshotAsync()).Accounts);
        Assert.Equal("team", rebuilt.Plan);
        Assert.Equal(stored.PlanObservedAt, rebuilt.PlanObservedAt);
    }

    [Theory]
    [InlineData(-1, "team")]
    [InlineData(0, Advanced)]
    [InlineData(1, Advanced)]
    public void APlanObservationOnlyOverridesAnOlderToken(int tokenOffsetSeconds, string expected)
    {
        var record = new SubscriptionRecord { AccountId = "acct-1", Email = "a@example.test", PlanType = "team", PlanObservedAt = Observed };
        var records = SubscriptionStore.Parse(SubscriptionStore.Serialize(new[] { record }));
        var mapper = new AccountMapper(new SecretRegistry(false));
        var row = mapper.ConvertCredential(Credential(Observed.ToUnixTimeSeconds() + tokenOffsetSeconds), livePlans: records);
        Assert.Equal(expected, row.Plan);
        Assert.Equal(expected == "team" ? Observed : (DateTimeOffset?)null, row.PlanObservedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OldStoresLoadWithoutTreatingAnImportedPlanAsLive(int version)
    {
        var records = SubscriptionStore.Parse("{\"version\":" + version + ",\"accounts\":{\"acct-1|a@example.test\":{\"accountID\":\"acct-1\",\"email\":\"a@example.test\",\"planType\":\"team\"}}}");
        var record = Assert.Single(records).Value;
        Assert.Equal("team", record.PlanType);
        Assert.Null(record.PlanObservedAt);
        var mapper = new AccountMapper(new SecretRegistry(false));
        Assert.Equal(Advanced, mapper.ConvertCredential(Credential(), livePlans: records).Plan);
    }

    [Fact]
    public async Task ConcurrentPlanAndExpiryUpdatesPreserveEachOtherAndOtherAccounts()
    {
        using var f = new DshFixture();
        await f.Subscriptions.SaveAsync(new[] { new SubscriptionRecord
        {
            AccountId = "acct-1", Email = "a@example.test", PlanType = Advanced,
            Cockpit = new JsonObject { ["organization_id"] = "synthetic-org" },
            SealedSecrets = SubscriptionStore.SealSecrets(new JsonObject { ["access_token"] = "synthetic-secret" }),
        } });
        await Task.WhenAll(
            f.Subscriptions.UpdatePlanAsync("acct-1", "a@example.test", "team", Observed),
            f.Subscriptions.UpdateActiveUntilAsync("acct-1", "a@example.test", Observed.AddDays(30)),
            f.Subscriptions.UpdatePlanAsync("acct-2", "b@example.test", "plus", Observed));
        await f.Subscriptions.ClearActiveUntilAsync("acct-1", "a@example.test");
        await f.Subscriptions.UpdatePlanAsync("acct-1", "a@example.test", Advanced, Observed.AddMinutes(-1));

        var stored = await f.Subscriptions.LoadAsync();
        Assert.Equal(2, stored.Count);
        var record = stored["acct-1|a@example.test"];
        Assert.Equal("team", record.PlanType);
        Assert.Equal(Observed, record.PlanObservedAt);
        Assert.Null(record.ActiveUntil);
        Assert.True(record.Authoritative);
        Assert.Equal("synthetic-org", record.Cockpit!["organization_id"]!.GetValue<string>());
        Assert.Equal("synthetic-secret", SubscriptionStore.OpenSecrets(record.SealedSecrets)!["access_token"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("team", true)]
    public async Task EmptyPlansAndFailedQuotaRequestsDoNotOverwriteTheCache(string plan, bool fail)
    {
        using var f = new DshFixture();
        await f.Subscriptions.UpdatePlanAsync("acct-1", "a@example.test", "plus", Observed);
        var credential = Credential();
        var row = f.Mapper.ConvertCredential(credential);
        var quota = new FakeQuota(plan) { Fail = fail };
        var selector = new AccountQuotaSelector(f.Backend, quota, f.Secrets, store: f.Subscriptions);
        await selector.GetResultAsync(row, new JsonNode[] { credential });
        var record = Assert.Single(await f.Subscriptions.LoadAsync()).Value;
        Assert.Equal("plus", record.PlanType);
        Assert.Equal(Observed, record.PlanObservedAt);
        Assert.Equal(1, quota.Calls);
    }

    private sealed class FakeQuota(string plan) : QuotaService
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }

        protected override Task<JsonNode> SendAsync(string accessToken, string accountId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) { throw new InvalidOperationException("Synthetic quota failure"); }
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["plan_type"] = plan,
                ["rate_limit"] = new JsonObject { ["unlimited"] = true },
            });
        }
    }
}
