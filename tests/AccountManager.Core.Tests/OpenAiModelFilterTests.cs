using AccountManager.Core.Actions;
using Xunit;

namespace AccountManager.Core.Tests;

public class OpenAiModelFilterTests
{
    [Fact]
    public void UsesNewestNumericFamilyOfStableGeneralAliases()
    {
        var ids = new[] { "gpt-6.2-astra", "gpt-6.10-sol", "gpt-6.10-astra", "gpt-7-mini-2030-01-01",
            "gpt-8-search-api", "gpt-4o-transcribe", "gpt-image-9", "o3", "tts-1", "gpt-6.10-sol-fast",
            "gpt-5.6-luna", "gpt-5.6-terra", "gpt-6-luna", "gpt-6.1-terra", "gpt-5-mini" };
        var expected = new[] { "gpt-6.10-astra", "gpt-6.10-sol", "gpt-6.1-terra", "gpt-6-luna" };
        Assert.Equal(expected, OpenAiModelFilter.Latest(ids));
        Assert.Equal(expected.Prepend("gpt-7"), OpenAiModelFilter.Latest(ids.Append("gpt-7").ToArray()));
        Assert.Empty(OpenAiModelFilter.Latest(new[] { "gpt-realtime-2.1", "text-embedding-3-small" }));
    }
}
