using System.Net.Http.Headers;
using System.Text.Json;
using AccountManager.Core.Actions;

namespace AccountManager.Core.Dsh;

/// <summary>Read-only discovery. User input selects a fixed destination, never an arbitrary URL.</summary>
public sealed class OfficialModelList
{
    private static readonly Uri[] Bases = Array.ConvertAll(new[]
    {
        "https://api.openai.com/v1",
        "https://ark.cn-beijing.volces.com/api/v3",
        "https://ark.cn-beijing.volces.com/api/coding/v3",
        "https://ark.cn-beijing.volces.com/api/plan/v3",
    }, url => new Uri(url));

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Proxy = Net.SystemProxy.Instance })
    {
        Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };
    private readonly HttpClient _client;
    public OfficialModelList() : this(Client) { }
    internal OfficialModelList(HttpClient client) => _client = client;

    public static Uri? MatchBase(string? url)
    {
        var text = url?.Trim().TrimEnd('/');
        return Bases.FirstOrDefault(b => text is not null &&
            text.StartsWith(b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) &&
            text[b.GetLeftPart(UriPartial.Authority).Length..] == b.AbsolutePath);
    }

    public async Task<ModelQueryResult> QueryAsync(string? url, string? key, bool recommended, CancellationToken ct)
    {
        var endpoint = MatchBase(url) ?? throw new DshException("仅支持 OpenAI 或火山方舟北京区的官方基础地址。");
        key = ApiProviderValidation.Key(key, required: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.AbsoluteUri + "/models");
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            bool coding = endpoint.AbsolutePath == "/api/coding/v3";
            if (coding && response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
                return await CodingGuideAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DshException($"模型查询失败（HTTP {(int)response.StatusCode}）。请检查密钥及该地址是否提供模型列表。");
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            if (body.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new DshException("供应商未返回有效的模型列表。");
            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    id.GetString() is not { Length: > 0 and <= 512 } model || model.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '，' or '#'))
                    throw new DshException("供应商返回了无法写入的模型 ID，已保留原列表。");
                if (seen.Add(model)) { models.Add(model); }
            }
            if (models.Count == 0 && coding) { return await CodingGuideAsync(ct).ConfigureAwait(false); }
            if (models.Count == 0) { throw new DshException("该密钥未查到模型，已保留原列表。"); }
            if (recommended && OpenAiModelFilter.AppliesTo(endpoint))
            {
                var guide = await ReadGuideAsync(PublishedModelGuides.OpenAiUrl, ct).ConfigureAwait(false);
                var picks = PublishedModelGuides.OpenAiPicks(guide, models);
                if (picks.Count > 0) { return new(picks, $"OpenAI 选型指南 ∩ 当前 Key 可见的 {models.Count} 个模型"); }
                picks = OpenAiModelFilter.Latest(models);
                if (picks.Count == 0) { throw new DshException(OpenAiModelFilter.NoMatch); }
                return new(picks, "官方指南未匹配，按当前 Key 各系列最高版本筛选");
            }
            return new(models, "当前 Key 的模型接口");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DshException("模型查询超时。"); }
        catch (HttpRequestException) { throw new DshException("无法读取官方模型列表，请检查网络连接。"); }
        catch (JsonException) { throw new DshException("供应商未返回有效的模型列表。"); }
        catch (FormatException) { throw new DshException("API Key 格式无效。"); }
    }

    private async Task<ModelQueryResult> CodingGuideAsync(CancellationToken ct)
    {
        var page = await ReadGuideAsync(PublishedModelGuides.CodingUrl, ct).ConfigureAwait(false);
        var ids = PublishedModelGuides.CodingModels(page);
        if (ids.Count == 0) { throw new DshException("套餐接口未提供列表，且无法读取官方「支持的模型」表；已保留原列表。"); }
        return new(ids, "火山 Coding Plan 官方文档（套餐支持列表，未验证当前账号）");
    }

    private async Task<string> ReadGuideAsync(string fixedUrl, CancellationToken ct)
    {
        // A fresh, anonymous request; the bearer header is only on the account-list request.
        try { return await _client.GetStringAsync(fixedUrl, ct).ConfigureAwait(false); }
        catch (HttpRequestException) { return string.Empty; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return string.Empty; }
    }
}
