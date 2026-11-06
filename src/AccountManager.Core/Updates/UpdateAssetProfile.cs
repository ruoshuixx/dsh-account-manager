namespace AccountManager.Core.Updates;

/// <summary>The supported fixed update targets. Asset names are selected locally, never remotely.</summary>
public enum UpdatePlatform
{
    WindowsX64,
    LinuxX64,
    /// <summary>The .deb package of a system-wide Ubuntu/Debian installation.</summary>
    LinuxDeb,
}

/// <summary>A locally defined executable/checksum pair for one supported update platform.</summary>
internal sealed class UpdateAssetProfile
{
    // Windows and .deb asset names carry the version derived from the release tag.
    private static readonly UpdateAssetProfile Windows = new(UpdatePlatform.WindowsX64, string.Empty, string.Empty);

    private static readonly UpdateAssetProfile Linux = new(
        UpdatePlatform.LinuxX64,
        "DshAccountManager-linux-x64",
        "DshAccountManager-linux-x64.sha256");

    private static readonly UpdateAssetProfile Deb = new(UpdatePlatform.LinuxDeb, string.Empty, string.Empty);

    private UpdateAssetProfile(UpdatePlatform platform, string executableAssetName, string checksumAssetName)
    {
        Platform = platform;
        ExecutableAssetName = executableAssetName;
        ChecksumAssetName = checksumAssetName;
    }

    internal UpdatePlatform Platform { get; }

    internal string ExecutableAssetName { get; }

    internal string ChecksumAssetName { get; }

    internal bool IsLinux => Platform is UpdatePlatform.LinuxX64 or UpdatePlatform.LinuxDeb;

    internal bool IsDeb => Platform == UpdatePlatform.LinuxDeb;

    /// <summary>Executable (or package) asset name for a stable tag such as <c>v1.2.3</c>.</summary>
    internal string ExecutableAssetNameFor(string tag)
        => Platform switch
        {
            UpdatePlatform.WindowsX64 => "AIAccountManager-" + TagVersion(tag) + "-win-x64.exe",
            UpdatePlatform.LinuxDeb => "AIAccountManager-" + TagVersion(tag) + "-amd64.deb",
            _ => ExecutableAssetName,
        };

    /// <summary>The sha256sum file that accompanies <see cref="ExecutableAssetNameFor"/>.</summary>
    internal string ChecksumAssetNameFor(string tag)
        => Platform == UpdatePlatform.LinuxX64 ? ChecksumAssetName : ExecutableAssetNameFor(tag) + ".sha256";

    private static string TagVersion(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

    internal static UpdateAssetProfile For(UpdatePlatform platform)
        => platform switch
        {
            UpdatePlatform.WindowsX64 => Windows,
            UpdatePlatform.LinuxX64 => Linux,
            UpdatePlatform.LinuxDeb => Deb,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported update platform."),
        };
}
