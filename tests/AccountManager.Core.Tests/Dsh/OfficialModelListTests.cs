using System.Net;
using AccountManager.Core.Dsh;
using Xunit;

namespace AccountManager.Core.Tests;

public class OfficialModelListTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(send(request));
    }

    [Theory]
    [InlineData("https://API.OPENAI.COM/v1/", "https://api.openai.com/v1/models")]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", "https://ark.cn-beijing.volces.com/api/v3/models")]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", "https://ark.cn-beijing.volces.com/api/coding/v3/models")]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3/", "https://ark.cn-beijing.volces.com/api/plan/v3/models")]
    public async Task QueriesOnlyTheSelectedCatalogAndPreservesExactIds(string url, string expected)
    {
        using var client = new HttpClient(new Handler(r =>
        {
            Assert.Equal(expected, r.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("Bearer test-key", r.Headers.Authorization!.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"new"},{"id":"new-fast"},{"id":"new"}]}""") };
        }));
        Assert.Equal(new[] { "new", "new-fast" }, (await new OfficialModelList(client).QueryAsync(url, "test-key", false, default)).Models);
    }

    [Theory]
    [InlineData("https://api.openai.com.evil.test/v1")]
    [InlineData("https://api.openai.com@evil.test/v1")]
    [InlineData("https://api.openai.com/v1?x=1")]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3#x")]
    [InlineData("https://ark.cn-beijing.volces.com:444/api/v3")]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/responses")]
    public async Task RejectsUnknownAddressBeforeSendingKey(string url)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("unexpected request")));
        await Assert.ThrowsAsync<DshException>(() => new OfficialModelList(client).QueryAsync(url, "test-key", false, default));
    }

    [Fact]
    public async Task CodingFallbackOnlyReadsAnonymousSupportedModelsTable()
    {
        using var client = new HttpClient(new Handler(r =>
        {
            if (r.RequestUri!.Host == "ark.cn-beijing.volces.com") { return new(HttpStatusCode.NotFound); }
            Assert.Null(r.Headers.Authorization);
            Assert.Equal("https://ark.volcengine.com/region:cn-beijing/docs/coding-plan-personal-plan-overview.md", r.RequestUri.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                <span id="supported-models"></span>
                ### 支持的模型
                |模型 |说明 |
                |---|---|
                |Auto |控制台 |
                |Kimi-K3 `即将下线` |旧款 |
                |Kimi-K3.1-Pro |新款 |
                | |引用 `not-a-model` |

                ### 支持的工具
                |not-a-model |其他 |
                """) };
        }));
        var result = await new OfficialModelList(client).QueryAsync("https://ark.cn-beijing.volces.com/api/coding/v3", "test-key", true, default);
        Assert.Equal(new[] { "kimi-k3.1-pro" }, result.Models);
        Assert.Contains("未验证当前账号", result.Source);
    }

    [Fact]
    public async Task GuideCanIntroduceANewFamilyButCannotInventAccountAccess()
    {
        using var client = new HttpClient(new Handler(r =>
        {
            if (r.RequestUri!.Host == "api.openai.com")
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"id":"gpt-7-nova"},{"id":"tts-1"}]}""") };
            Assert.Null(r.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new StringContent("1. **GPT-7 Nova · Low**\n2. **GPT-7 Terra · Medium**") };
        }));
        var result = await new OfficialModelList(client).QueryAsync("https://api.openai.com/v1", "test-key", true, default);
        Assert.Equal(new[] { "gpt-7-nova" }, result.Models);
    }

    [Theory]
    [InlineData(200, "{\"data\":[]}")]
    [InlineData(200, "{\"data\":[{\"id\":\"a,b\"}]}")]
    [InlineData(200, "<html>test-key</html>")]
    [InlineData(401, "test-key")]
    [InlineData(302, "test-key")]
    public async Task BadResponseFailsWithoutReturningEmptyListOrExposingResponse(int status, string body)
    {
        using var client = new HttpClient(new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent(body) }));
        var ex = await Assert.ThrowsAsync<DshException>(() => new OfficialModelList(client).QueryAsync("https://api.openai.com/v1", "test-key", false, default));
        Assert.DoesNotContain("test-key", ex.Message);
    }
}
