using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AgenticWorkflow.Packaging.Tests;

/// <summary>What the release contains (and does not), and that its identity is one version everywhere. Built by the real <c>scripts/package.ps1</c>.</summary>
[Collection(ReleaseCollection.Name)]
public sealed class PackageTests(ReleaseFixture release)
{
    [Fact]
    public void The_zip_has_the_release_name_and_one_top_level_folder()
    {
        Assert.Equal($"AgenticWorkflow-{release.Version}-win-x64.zip", Path.GetFileName(release.ZipPath));

        using var zip = ZipFile.OpenRead(release.ZipPath);
        var tops = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();

        Assert.Equal([$"AgenticWorkflow-{release.Version}-win-x64"], tops);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains('\\', StringComparison.Ordinal));
    }

    [Fact]
    public void The_release_contains_everything_a_machine_without_a_sdk_needs()
    {
        foreach (var required in new[]
                 {
                     "install.ps1", "uninstall.ps1", "setup/AgenticWorkflow.Setup.ps1", "LICENSE", "README.txt", "manifest.json",
                     "bin/aw.exe", "bin/aw.dll", "bin/AgenticWorkflow.ClaudeEvidenceHook.exe", "bin/AgenticWorkflow.Runtime.Herdr.dll", "bin/e_sqlite3.dll", "bin/aw-package.json",
                     "bin/hostfxr.dll", "bin/coreclr.dll", "bin/System.Private.CoreLib.dll",
                     "cockpit/main.js", "cockpit/ui.js", "cockpit/mod.json", "cockpit/lib/version.js", "cockpit/lib/native.js",
                     "docs/getting-started.md", "examples/agenticworkflow.json",
                 })
            Assert.True(File.Exists(Path.Combine(release.PackageRoot, required.Replace('/', '\\'))), $"missing {required}");
    }

    [Fact]
    public void The_pi_extension_is_embedded_in_the_shipped_runtime_assembly()
    {
        var bytes = File.ReadAllBytes(Path.Combine(release.PackageRoot, "bin", "AgenticWorkflow.Runtime.Herdr.dll"));

        Assert.True(Contains(bytes, Encoding.UTF8.GetBytes("aw-pi-extension.mjs")));
    }

    [Fact]
    public void The_release_contains_no_development_test_or_historical_artifact()
    {
        using var zip = ZipFile.OpenRead(release.ZipPath);
        var names = zip.Entries.Select(e => string.Join('/', e.FullName.Split('/').Skip(1))).Where(n => n.Length > 0).ToList();

        var forbiddenExtensions = new[] { ".pdb", ".log", ".db", ".sqlite", ".sqlite3", ".png", ".jpg", ".jpeg", ".gif", ".pem", ".pfx", ".key", ".tmp", ".d.ts" };
        var forbiddenFragments = new[] { ".git", "node_modules", "/test/", "/driver.js", "/install.mjs", "/package.json", "/.env", "TestResults", "xunit", "Microsoft.NET.Test", "testhost", "coverlet", ".Tests.", "/obj/", "artifacts/", "/runs/", "/controller/" };

        foreach (var name in names)
        {
            Assert.DoesNotContain(forbiddenExtensions, extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(forbiddenFragments, fragment => ("/" + name).Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        // Historical milestone documents live outside the repository and never ship.
        Assert.DoesNotContain(names, n => n.StartsWith("docs/", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(n), @"^(M\d|V\d)"));
    }

    [Fact]
    public void No_file_carries_a_developer_machine_path_or_the_users_name()
    {
        var needles = new List<string>
        {
            release.RepositoryRoot,
            release.RepositoryRoot.Replace('\\', '/'),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/'),
        };

        if (Environment.UserName.Length >= 4)
            needles.Add(Environment.UserName);

        foreach (var file in Directory.GetFiles(release.PackageRoot, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);

            foreach (var needle in needles)
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)) || Contains(bytes, Encoding.Unicode.GetBytes(needle)), $"{Path.GetRelativePath(release.PackageRoot, file)} contains '{needle}'");
            }
        }
    }

    [Fact]
    public void One_version_everywhere()
    {
        using var outer = JsonDocument.Parse(File.ReadAllText(Path.Combine(release.PackageRoot, "manifest.json")));
        using var inner = JsonDocument.Parse(File.ReadAllText(Path.Combine(release.PackageRoot, "bin", "aw-package.json")));
        var stamp = File.ReadAllText(Path.Combine(release.PackageRoot, "cockpit", "lib", "version.js"));

        Assert.Equal(release.Version, outer.RootElement.GetProperty("version").GetString());
        Assert.Equal(release.Version, inner.RootElement.GetProperty("version").GetString());
        Assert.Equal("win-x64", outer.RootElement.GetProperty("platform").GetString());
        Assert.Contains($"COCKPIT_VERSION = \"{release.Version}\";", stamp, StringComparison.Ordinal);

        var reported = ReleaseFixture.Run(Path.Combine(release.PackageRoot, "bin", "aw.exe"), ["--version"]);
        Assert.Equal($"aw {release.Version}", reported.Output.Trim());
    }

    [Fact]
    public void Every_manifest_entry_matches_its_file_and_every_file_is_listed()
    {
        using var outer = JsonDocument.Parse(File.ReadAllText(Path.Combine(release.PackageRoot, "manifest.json")));
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in outer.RootElement.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString()!;
            var path = Path.Combine(release.PackageRoot, relative.Replace('/', '\\'));
            listed.Add(relative);

            Assert.Equal(new FileInfo(path).Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), entry.GetProperty("sha256").GetString());
        }

        var actual = Directory.GetFiles(release.PackageRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(release.PackageRoot, f).Replace('\\', '/')).Where(f => f != "manifest.json");
        Assert.All(actual, f => Assert.Contains(f, listed));
    }

    [Fact]
    public void The_binaries_are_optimized_release_builds()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(release.PackageRoot, "bin"), "AgenticWorkflow.*.dll").Append(Path.Combine(release.PackageRoot, "bin", "aw.dll")))
            Assert.False(IsUnoptimized(file), $"{Path.GetFileName(file)} was built without optimization (a Debug build)");
    }

    [Fact]
    public void Building_twice_gives_the_same_zip_byte_for_byte()
    {
        var second = Path.Combine(Path.GetDirectoryName(release.OutputDirectory)!, "out-again");
        var build = ReleaseFixture.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(release.RepositoryRoot, "scripts", "package.ps1"), "-OutputDirectory", second]);
        Assert.True(build.ExitCode == 0, build.ToString());

        var first = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(release.ZipPath));
        var again = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(second, Path.GetFileName(release.ZipPath))));

        Assert.Equal(first, again);
    }

    [Fact]
    public void The_license_is_the_unmodified_apache_text()
    {
        var packaged = File.ReadAllText(Path.Combine(release.PackageRoot, "LICENSE"));
        var repository = File.ReadAllText(Path.Combine(release.RepositoryRoot, "LICENSE"));

        Assert.Equal(repository, packaged);
        Assert.Contains("Apache License", packaged, StringComparison.Ordinal);
        Assert.Contains("Version 2.0, January 2004", packaged, StringComparison.Ordinal);
    }

    /// <summary>True when the assembly carries <c>DebuggableAttribute</c> with <c>DisableOptimizations</c> set, which is what a Debug build does.</summary>
    private static bool IsUnoptimized(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);

        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);

            if (attribute.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference)
                continue;

            var constructor = reader.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
            var type = reader.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)constructor.Parent);

            if (reader.GetString(type.Name) != "DebuggableAttribute")
                continue;

            var blob = reader.GetBlobBytes(attribute.Value);

            // prolog (2 bytes), then either DebuggingModes (int32) or the two booleans (isJITTrackingEnabled, isJITOptimizerDisabled)
            return blob.Length >= 6 && (blob.Length == 8 ? blob[3] != 0 : (BitConverter.ToInt32(blob, 2) & 0x100) != 0);
        }

        return false;
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
