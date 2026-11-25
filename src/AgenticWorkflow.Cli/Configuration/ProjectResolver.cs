using System.Security.Cryptography;
using System.Text;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Cli.Configuration;

/// <summary>
/// Turns a workspace directory and a configuration file into a <see cref="ResolvedProject"/>. Pure file-system work: it starts no
/// process, so it is the part of <c>aw show</c> that proves a project resolves without Git, Herdr or a model.
/// </summary>
internal static class ProjectResolver
{
    public const string DefaultConfigFileName = "agenticworkflow.json";

    /// <summary>A path no real file has; asked of the existing scope matcher to learn whether a directory entry is wholly protected.</summary>
    private const string ScopeProbeName = ".aw-scope-probe";

    public static ResolvedProject Resolve(string? workspaceOption, string? configOption, string currentDirectory)
    {
        var workspace = Path.GetFullPath(workspaceOption ?? currentDirectory, currentDirectory).TrimEnd('\\', '/');

        if (!Directory.Exists(workspace))
            throw new ConfigurationException($"The workspace '{workspace}' does not exist or is not a directory.");

        var configPath = configOption is null
            ? Path.Combine(workspace, DefaultConfigFileName)
            : Path.GetFullPath(configOption, currentDirectory);

        if (!File.Exists(configPath))
        {
            throw new ConfigurationException(
                $"No project configuration at '{configPath}'. Create it (see docs/configuration.md) or pass --config <file>. "
                + "aw never creates or guesses a configuration, and nothing was launched.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(configPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"The configuration '{configPath}' could not be read: {e.Message}");
        }

        ProjectConfig config;
        try
        {
            // Decode strictly as UTF-8 (a BOM is tolerated): a file that is not UTF-8 text is an error, not mojibake.
            config = ProjectConfigParser.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'));
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigurationException($"The configuration '{configPath}' is not valid UTF-8 text.");
        }
        catch (ConfigurationException e)
        {
            throw new ConfigurationException([.. e.Problems.Select(p => $"{configPath}: {p}")]);
        }

        var problems = new List<string>();
        var scope = BuildScope(config, problems);
        var checks = BuildChecks(config, problems);

        if (problems.Count > 0)
            throw new ConfigurationException([.. problems.Select(p => $"{configPath}: {p}")]);

        return new ResolvedProject(
            workspace,
            configPath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            config,
            Toolchain.Find(config.Toolchain)!,
            config.Catalog.Find(config.Profile)!,
            scope!,
            checks,
            config.ReviewerProfile is null ? null : config.Catalog.Find(config.ReviewerProfile)!,
            config.ProverProfile is null ? null : config.Catalog.Find(config.ProverProfile)!);
    }

    /// <summary>
    /// <c>dir/**</c> means the directory <c>dir/</c> (the existing scope's directory-prefix form), a path without wildcards is one exact file,
    /// and <c>**</c> alone means "anywhere not protected". Every other wildcard is refused rather than guessed. The path rules themselves
    /// (relative, inside the workspace) are the existing <see cref="WorkspaceScope"/>'s: it is constructed here and is the only matcher.
    /// </summary>
    private static WorkspaceScope? BuildScope(ProjectConfig config, List<string> problems)
    {
        var before = problems.Count;

        var writable = Translate("scope.writable", config.Writable, problems);
        var protectedPaths = Translate("scope.protected", config.Protected, problems);

        if (problems.Count > before)
            return null;

        var anywhere = writable.Contains("**", StringComparer.Ordinal);
        var allowed = anywhere ? [] : writable;

        WorkspaceScope scope;
        try
        {
            scope = new WorkspaceScope(allowed, protectedPaths);
        }
        catch (ArgumentException e)
        {
            problems.Add($"Invalid scope: {e.Message.Split(" (Parameter", 2)[0]}");
            return null;
        }

        foreach (var entry in scope.AllowedPaths)
        {
            var probe = entry.EndsWith('/') ? entry + ScopeProbeName : entry;

            if (scope.Evaluate([probe]).FirstOrDefault() is { Reason: "protected path changed" })
                problems.Add($"'scope.writable' entry '{entry}' is entirely covered by 'scope.protected', so nothing could ever be changed there.");
        }

        return scope;
    }

    private static List<string> Translate(string setting, IReadOnlyList<string> entries, List<string> problems)
    {
        var translated = new List<string>();

        foreach (var entry in entries)
        {
            var path = entry.Trim().Replace('\\', '/');

            if (path == "**")
            {
                if (setting == "scope.protected")
                    problems.Add($"'{setting}' entry '**' would protect the whole repository; list what to protect.");
                else
                    translated.Add("**");

                continue;
            }

            if (path.EndsWith("/**", StringComparison.Ordinal))
                path = path[..^2]; // "lib/**" -> "lib/"

            if (path.IndexOfAny(['*', '?', '[', ']', '{', '}']) >= 0)
            {
                problems.Add($"'{setting}' entry '{entry}' uses a wildcard that is not supported: write an exact file, or 'dir/**' for a directory.");
                continue;
            }

            translated.Add(path);
        }

        return translated;
    }

    private static List<VerificationCheckSpec> BuildChecks(ProjectConfig config, List<string> problems)
    {
        var checks = new List<VerificationCheckSpec>();

        foreach (var check in config.Verification)
        {
            try
            {
                checks.Add(new VerificationCheckSpec(
                    new VerificationCheckId(check.Id),
                    check.Description ?? Describe(check),
                    check.Executable,
                    check.Arguments,
                    TimeSpan.FromSeconds(check.TimeoutSeconds)));
            }
            catch (ArgumentException e)
            {
                problems.Add($"Verification check '{check.Id}' is invalid: {e.Message.Split(" (Parameter", 2)[0]}");
            }
        }

        return checks;
    }

    private static string Describe(CheckConfig check)
        => check.Arguments.Count == 0 ? check.Executable : $"{check.Executable} {string.Join(' ', check.Arguments)}";
}
