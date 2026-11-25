using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticWorkflow.Cli.Diagnostics;

/// <summary>One file of a release: its path (relative, forward slashes), size and SHA-256.</summary>
internal sealed record PackageFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// What a release says it contains, written by the packaging step next to <c>aw</c> and read by <c>aw doctor</c> to tell a complete installation from a damaged or partial one. Pure data:
/// it names no machine, no user and no path outside the release.
/// </summary>
internal sealed record PackageManifest(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("files")] IReadOnlyList<PackageFile> Files)
{
    /// <summary>The manifest's name in the installed application folder (and in a release's <c>bin</c> folder).</summary>
    public const string FileName = "aw-package.json";

    public const string ProductName = "AgenticWorkflow";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The manifest in <paramref name="directory"/>, or null when there is none or it cannot be read.</summary>
    public static PackageManifest? TryRead(string directory)
    {
        try
        {
            var path = System.IO.Path.Combine(directory, FileName);

            if (!File.Exists(path))
                return null;

            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), Json);

            return manifest is { Files: not null } && manifest.Product == ProductName ? manifest : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The files of the manifest that are missing from <paramref name="directory"/>, and those that differ from what was packaged.</summary>
    public (IReadOnlyList<string> Missing, IReadOnlyList<string> Changed) Verify(string directory)
    {
        var missing = new List<string>();
        var changed = new List<string>();

        foreach (var file in Files)
        {
            var full = System.IO.Path.Combine(directory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                missing.Add(file.Path);
                continue;
            }

            try
            {
                if (new FileInfo(full).Length != file.Size || !string.Equals(Hash(full), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    changed.Add(file.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                changed.Add(file.Path);
            }
        }

        return (missing, changed);
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
