namespace AccountManager.Core.Dsh;

/// <summary>
/// Locates a dsh installation's data: <c>DSH_HOME</c> (environment variable, else
/// <c>&lt;user home&gt;/.dsh</c>), its shared <c>.credentials.yaml</c> and the per-profile directory.
/// The profile is auto-detected as <c>desktop</c>, then <c>tauri</c>, then the first other existing
/// directory under <c>profiles/</c> (ordinal order) unless explicitly overridden.
/// </summary>
public sealed class DshHome
{
    public const string EnvironmentVariable = "DSH_HOME";
    public const string CredentialsFileName = ".credentials.yaml";
    public const string PatchFileName = "cordis.patch.yml";
    public static readonly string[] PreferredProfiles = { "desktop", "tauri" };

    private DshHome(string root, string profile, bool profileExists)
    {
        Root = root;
        Profile = profile;
        ProfileExists = profileExists;
    }

    /// <summary>Absolute DSH_HOME directory (it may not exist yet).</summary>
    public string Root { get; }

    /// <summary>Profile name in use (an explicit override, a detected profile, or the platform default).</summary>
    public string Profile { get; }

    /// <summary>Whether <see cref="ProfileDirectory"/> exists on disk.</summary>
    public bool ProfileExists { get; }

    public string CredentialsPath => Path.Combine(Root, CredentialsFileName);

    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    public string ProfileDirectory => Path.Combine(ProfilesDirectory, Profile);

    public string PatchPath => Path.Combine(ProfileDirectory, PatchFileName);

    /// <summary>
    /// Resolves the home and profile. <paramref name="homeOverride"/> and <paramref name="profileOverride"/>
    /// come from settings (null or blank means automatic). <paramref name="getEnvironmentVariable"/> and
    /// <paramref name="userHome"/> are test seams.
    /// </summary>
    public static DshHome Resolve(string? homeOverride = null, string? profileOverride = null,
        Func<string, string?>? getEnvironmentVariable = null, string? userHome = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var home = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var chosen = !string.IsNullOrWhiteSpace(homeOverride) ? homeOverride.Trim()
            : !string.IsNullOrWhiteSpace(env(EnvironmentVariable)) ? env(EnvironmentVariable)!.Trim()
            : Path.Combine(home, ".dsh");
        chosen = ExpandHome(chosen, home);
        var root = Path.GetFullPath(chosen);

        if (!string.IsNullOrWhiteSpace(profileOverride))
        {
            var name = profileOverride.Trim();
            ValidateProfileName(name);
            return new DshHome(root, name, Directory.Exists(Path.Combine(root, "profiles", name)));
        }

        var detected = DetectProfile(root);
        if (detected is not null) { return new DshHome(root, detected, true); }
        // Nothing exists yet: default to the profile of the platform's client.
        return new DshHome(root, OperatingSystem.IsWindows() ? "desktop" : "tauri", false);
    }

    /// <summary>Existing profile directory names under <c>profiles/</c> in ordinal order.</summary>
    public static IReadOnlyList<string> ListProfiles(string root)
    {
        var dir = Path.Combine(root, "profiles");
        if (!Directory.Exists(dir)) { return Array.Empty<string>(); }
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static string? DetectProfile(string root)
    {
        var existing = ListProfiles(root);
        foreach (var preferred in PreferredProfiles)
        {
            if (existing.Contains(preferred, StringComparer.Ordinal)) { return preferred; }
        }

        return existing.Count > 0 ? existing[0] : null;
    }

    private static void ValidateProfileName(string name)
    {
        if (name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
        {
            throw new ArgumentException("Invalid dsh profile name.", nameof(name));
        }
    }

    private static string ExpandHome(string path, string home)
    {
        if (path == "~") { return home; }
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, path[2..]);
        }

        return path;
    }
}
