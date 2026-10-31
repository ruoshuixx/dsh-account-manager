namespace AccountManager.Core.Accounts;

/// <summary>Short display names for ChatGPT plan types; unknown plans are shown as they are.</summary>
public static class PlanNames
{
    private const string SelfServeBusiness = "self_serve_business";

    public static string Display(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) { return "—"; }
        var p = plan.Trim();
        // Team (Business) workspaces bought in the app report "self_serve_business[_<seat>]"; a Pro seat is the upgraded one.
        if (p.StartsWith(SelfServeBusiness, StringComparison.OrdinalIgnoreCase))
        {
            var seat = p[SelfServeBusiness.Length..].TrimStart('_');
            return seat.Contains("pro", StringComparison.OrdinalIgnoreCase) ? "team 高级" : "team";
        }
        return p.Equals("business", StringComparison.OrdinalIgnoreCase) ? "team" : p;
    }
}
