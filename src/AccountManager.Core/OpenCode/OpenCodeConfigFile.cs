using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.OpenCode;

/// <summary>OpenCode's global config document plus the raw text it was read from.</summary>
public sealed class OpenCodeConfigDocument
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Raw text at read time; null when the file did not exist yet.</summary>
    public string? Raw { get; init; }

    public JsonObject Config { get; init; } = new();
}

public interface IOpenCodeConfigFile
{
    Task<OpenCodeConfigDocument> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the file; refuses when it changed since <see cref="OpenCodeConfigDocument.Raw"/>.</summary>
    Task WriteAsync(OpenCodeConfigDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Reads/writes the global <c>opencode.json(c)</c> (same rules as the OpenCode account manager).</summary>
public sealed class OpenCodeConfigFile : IOpenCodeConfigFile
{
    private readonly OpenCodeCli _cli;

    public OpenCodeConfigFile(OpenCodeCli cli) => _cli = cli ?? throw new ArgumentNullException(nameof(cli));

    public async Task<OpenCodeConfigDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        var dir = await _cli.GetConfigDirectoryAsync(cancellationToken).ConfigureAwait(false);
        var json = Path.Combine(dir, "opencode.json");
        var jsonc = Path.Combine(dir, "opencode.jsonc");
        if (File.Exists(json) && File.Exists(jsonc))
        {
            throw new OpenCodeException("OpenCode 全局目录同时存在 opencode.json 和 opencode.jsonc，本工具不会猜测改哪个。");
        }

        var path = File.Exists(jsonc) ? jsonc : json;
        string? raw = null;
        JsonObject config;
        if (File.Exists(path))
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            config = Jsonc.Parse(raw) as JsonObject ?? throw new OpenCodeException("OpenCode 全局配置必须是 JSON 对象。");
        }
        else
        {
            config = new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        }

        return new OpenCodeConfigDocument { Path = path, Raw = raw, Config = config };
    }

    public async Task WriteAsync(OpenCodeConfigDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(document.Path)!);
        var now = File.Exists(document.Path) ? await File.ReadAllTextAsync(document.Path, cancellationToken).ConfigureAwait(false) : null;
        if (!string.Equals(now, document.Raw, StringComparison.Ordinal))
        {
            throw new OpenCodeException("OpenCode 全局配置已被其它程序修改，已停止写入。");
        }

        var temp = document.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, document.Config.ToJsonString(CoreJson.Indented), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var beforeReplace = File.Exists(document.Path) ? await File.ReadAllTextAsync(document.Path, cancellationToken).ConfigureAwait(false) : null;
            if (!string.Equals(beforeReplace, document.Raw, StringComparison.Ordinal))
            {
                throw new OpenCodeException("OpenCode 全局配置在保存期间被其它程序修改，已停止替换。");
            }

            if (File.Exists(document.Path)) { File.Replace(temp, document.Path, null); }
            else { File.Move(temp, document.Path); }
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }
}
