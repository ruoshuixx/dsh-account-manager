using System.Globalization;
using System.Reflection;

namespace AccountManager.App.Services;

/// <summary>
/// Build time embedded in the assembly, independent of the installer's file timestamps.
/// </summary>
public static class AppBuild
{
    public static string Stamp()
    {
        var utc = typeof(AppBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimestampUtc")?.Value;
        return DateTimeOffset.TryParseExact(utc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? "构建 " + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;
    }
}
