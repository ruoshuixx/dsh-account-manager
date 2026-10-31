using AccountManager.Core.Accounts;
using Xunit;

namespace AccountManager.Core.Tests;

public class PlanNamesTests
{
    [Theory]
    [InlineData("self_serve_business_prolite", "team 高级")]
    [InlineData("self_serve_business", "team")]
    [InlineData("business", "team")]
    [InlineData("team", "team")]
    [InlineData("plus", "plus")]
    [InlineData(null, "—")]
    [InlineData(" ", "—")]
    public void PlanTypesGetShortNames(string? plan, string expected)
        => Assert.Equal(expected, PlanNames.Display(plan));
}
