using System.Text.Json;
using Microsoft.Win32;

namespace AgenticWorkflow.Packaging.Tests;

/// <summary>
/// install.ps1 and uninstall.ps1 from the real release, run against an isolated user (see <see cref="Sandbox"/>): a path with a space in it, a private PATH registry key, a private Herdr home
/// and a private state folder. The real PATH, Herdr home and run history are never involved.
/// </summary>
[Collection(ReleaseCollection.Name)]
public sealed class InstallerTests(ReleaseFixture release)
{
    [Fact]
    public void A_fresh_install_puts_a_working_aw_where_it_says_in_a_path_with_spaces()
    {
        using var sandbox = new Sandbox(release);

        var install = sandbox.Install();

        Assert.True(install.ExitCode == 0, install.ToString());
        Assert.Contains(sandbox.BinDir, install.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.AwExe));
        Assert.True(File.Exists(Path.Combine(sandbox.BinDir, "AgenticWorkflow.ClaudeEvidenceHook.exe")));
        Assert.Equal($"aw {release.Version}", sandbox.Aw("--version").Output.Trim());
        Assert.Contains(" ", sandbox.InstallDir, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_never_creates_or_writes_the_state_folder()
    {
        using var sandbox = new Sandbox(release);

        Assert.Equal(0, sandbox.Install().ExitCode);

        Assert.False(Directory.Exists(sandbox.StateRoot));
        Assert.False(Directory.Exists(Path.Combine(sandbox.InstallDir, "runs")));
    }

    [Fact]
    public void The_cockpit_is_a_real_copy_configured_with_the_installed_aw()
    {
        using var sandbox = new Sandbox(release);

        Assert.Equal(0, sandbox.Install().ExitCode);

        Assert.True(Directory.Exists(sandbox.ModDir));
        Assert.Null(new DirectoryInfo(sandbox.ModDir).LinkTarget);
        Assert.False(new DirectoryInfo(sandbox.ModDir).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(Path.Combine(sandbox.ModDir, "main.js")));
        Assert.True(File.Exists(Path.Combine(sandbox.ModDir, "lib", "version.js")));

        using var mod = JsonDocument.Parse(File.ReadAllText(Path.Combine(sandbox.ModDir, "mod.json")));
        Assert.True(mod.RootElement.GetProperty("palette").GetArrayLength() > 0);

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(sandbox.SettingsDir, "config.json")));
        var configured = settings.RootElement.GetProperty("aw").GetString()!;
        Assert.Equal(sandbox.AwExe.Replace('\\', '/'), configured);
        Assert.True(File.Exists(configured));
        Assert.DoesNotContain(release.RepositoryRoot.Replace('\\', '/'), configured, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_cockpit_carries_no_development_files()
    {
        using var sandbox = new Sandbox(release);

        Assert.Equal(0, sandbox.Install().ExitCode);

        var names = Directory.GetFiles(sandbox.ModDir, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).ToList();
        Assert.DoesNotContain("driver.js", names);
        Assert.DoesNotContain("install.mjs", names);
        Assert.DoesNotContain("package.json", names);
        Assert.DoesNotContain(Directory.GetDirectories(sandbox.ModDir), d => Path.GetFileName(d) == "test");
    }

    [Fact]
    public void The_user_path_gets_the_install_folder_once_and_keeps_everything_else()
    {
        using var sandbox = new Sandbox(release);
        sandbox.SetPath(@"C:\Tools;%USERPROFILE%\bin");

        Assert.Equal(0, sandbox.Install().ExitCode);
        Assert.Equal(0, sandbox.Install().ExitCode);
        Assert.Equal(0, sandbox.Install().ExitCode);

        var entries = sandbox.PathEntries();
        Assert.Equal([@"C:\Tools", @"%USERPROFILE%\bin", sandbox.BinDir], entries);
        Assert.Equal(RegistryValueKind.ExpandString, sandbox.PathKind());
    }

    [Fact]
    public void The_path_entry_is_stable_across_versions_and_never_a_cmd_shim()
    {
        using var sandbox = new Sandbox(release);

        Assert.Equal(0, sandbox.Install().ExitCode);

        Assert.Equal(sandbox.BinDir, Assert.Single(sandbox.PathEntries()));
        Assert.DoesNotContain(release.Version, sandbox.BinDir, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(sandbox.BinDir, "aw.cmd"));
        Assert.Empty(Directory.GetFiles(sandbox.BinDir, "aw.bat"));
    }

    [Fact]
    public void NoPath_and_NoCockpit_leave_those_alone()
    {
        using var sandbox = new Sandbox(release);

        var install = sandbox.Install("-NoPath", "-NoCockpit");

        Assert.True(install.ExitCode == 0, install.ToString());
        Assert.Empty(sandbox.PathEntries());
        Assert.False(Directory.Exists(sandbox.ModDir));
        Assert.False(Directory.Exists(sandbox.SettingsDir));
        Assert.True(File.Exists(sandbox.AwExe));
    }

    [Fact]
    public void An_update_replaces_the_application_and_the_cockpit_and_leaves_no_stale_file()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        File.WriteAllText(Path.Combine(sandbox.BinDir, "left-by-an-older-version.dll"), "old");
        File.WriteAllText(Path.Combine(sandbox.ModDir, "left-by-an-older-version.js"), "old");
        Directory.CreateDirectory(Path.Combine(sandbox.ModDir, "lib", "gone"));
        File.WriteAllText(Path.Combine(sandbox.ModDir, "lib", "gone", "x.js"), "old");
        File.WriteAllText(Path.Combine(sandbox.ModDir, "main.js"), "damaged");

        var update = sandbox.Install();

        Assert.True(update.ExitCode == 0, update.ToString());
        Assert.Contains("Updated", update.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sandbox.BinDir, "left-by-an-older-version.dll")));
        Assert.False(File.Exists(Path.Combine(sandbox.ModDir, "left-by-an-older-version.js")));
        Assert.False(Directory.Exists(Path.Combine(sandbox.ModDir, "lib", "gone")));
        Assert.NotEqual("damaged", File.ReadAllText(Path.Combine(sandbox.ModDir, "main.js")));
        Assert.DoesNotContain(Directory.GetDirectories(sandbox.InstallDir), d => Path.GetFileName(d).StartsWith("bin.", StringComparison.Ordinal));
        Assert.Equal($"aw {release.Version}", sandbox.Aw("--version").Output.Trim());
    }

    [Fact]
    public void Install_update_and_uninstall_preserve_the_run_history()
    {
        using var sandbox = new Sandbox(release);
        var run = Directory.CreateDirectory(Path.Combine(sandbox.StateRoot, "runs", "0197d2e1-5c3a-7b8e-9f10-2a3b4c5d6e7f")).FullName;
        var sentinel = Path.Combine(run, "events.db");
        File.WriteAllText(sentinel, "history");

        Assert.Equal(0, sandbox.Install().ExitCode);
        Assert.Equal("history", File.ReadAllText(sentinel));

        Assert.Equal(0, sandbox.Install().ExitCode);
        Assert.Equal("history", File.ReadAllText(sentinel));

        var uninstall = sandbox.Uninstall();
        Assert.True(uninstall.ExitCode == 0, uninstall.ToString());
        Assert.Equal("history", File.ReadAllText(sentinel));
        Assert.Contains("Kept your run history", uninstall.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_idle_controller_is_stopped_for_an_update_and_the_update_succeeds()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        var started = sandbox.Aw("ctl", "health");
        Assert.True(started.ExitCode == 0 || started.ExitCode == 31, started.ToString());
        var doctor = sandbox.Aw("doctor");   // doctor starts the controller, as every client does
        Assert.Contains("controller", doctor.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sandbox.StateRoot, "controller", "controller.lock")), "a controller should have been started");

        var update = sandbox.Install();

        Assert.True(update.ExitCode == 0, update.ToString());
        Assert.Contains("Stopped the idle AgenticWorkflow controller", update.Output, StringComparison.Ordinal);
        Assert.Equal($"aw {release.Version}", sandbox.Aw("--version").Output.Trim());
    }

    [Fact]
    public void An_update_is_refused_without_changing_anything_while_work_is_being_supervised()
    {
        using var sandbox = new Sandbox(release);
        Directory.CreateDirectory(sandbox.BinDir);
        File.Copy(release.FakeAw, sandbox.AwExe);
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "install.json"), """{ "product": "AgenticWorkflow", "version": "0.0.1" }""");
        sandbox.SetPath(@"C:\Tools");
        var before = File.ReadAllBytes(sandbox.AwExe);

        var update = sandbox.Install();

        Assert.Equal(1, update.ExitCode);
        Assert.Contains("supervising 2 running task", update.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(sandbox.AwExe));
        Assert.False(Directory.Exists(sandbox.ModDir));
        Assert.Equal([@"C:\Tools"], sandbox.PathEntries());
        Assert.DoesNotContain(Directory.GetDirectories(sandbox.InstallDir), d => Path.GetFileName(d).StartsWith("bin.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_uninstall_is_also_refused_while_work_is_being_supervised()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);
        File.Copy(release.FakeAw, sandbox.AwExe, overwrite: true);

        var uninstall = sandbox.Uninstall();

        Assert.Equal(1, uninstall.ExitCode);
        Assert.Contains("supervising 2 running task", uninstall.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.AwExe));
        Assert.True(Directory.Exists(sandbox.ModDir));
        Assert.Single(sandbox.PathEntries());
    }

    [Fact]
    public void Installing_an_older_release_over_a_newer_one_is_refused_unless_allowed()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);
        File.WriteAllText(Path.Combine(sandbox.InstallDir, "install.json"), """{ "product": "AgenticWorkflow", "version": "99.0.0" }""");

        var refused = sandbox.Install();

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("older", refused.Output, StringComparison.Ordinal);
        Assert.Contains("-AllowDowngrade", refused.Output, StringComparison.Ordinal);

        var allowed = sandbox.Install("-AllowDowngrade");
        Assert.True(allowed.ExitCode == 0, allowed.ToString());
    }

    [Fact]
    public void A_damaged_release_installs_nothing()
    {
        using var sandbox = new Sandbox(release, copyPackage: true);
        File.AppendAllText(Path.Combine(sandbox.Package, "bin", "aw.dll"), "x");

        var install = sandbox.Install();

        Assert.Equal(1, install.ExitCode);
        Assert.Contains("damaged or incomplete", install.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(sandbox.InstallDir));
        Assert.False(Directory.Exists(sandbox.HerdrHome));
        Assert.Empty(sandbox.PathEntries());
    }

    [Fact]
    public void A_release_with_a_stray_file_is_refused()
    {
        using var sandbox = new Sandbox(release, copyPackage: true);
        File.WriteAllText(Path.Combine(sandbox.Package, "bin", "not-in-the-manifest.dll"), "x");

        var install = sandbox.Install();

        Assert.Equal(1, install.ExitCode);
        Assert.Contains("unexpected file", install.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_in_the_mods_directory_that_is_not_ours_is_never_replaced()
    {
        using var sandbox = new Sandbox(release);
        Directory.CreateDirectory(sandbox.ModDir);
        File.WriteAllText(Path.Combine(sandbox.ModDir, "mod.json"), """{ "title": "Somebody else's mod" }""");
        File.WriteAllText(Path.Combine(sandbox.ModDir, "theirs.txt"), "keep");

        var install = sandbox.Install();

        Assert.Equal(1, install.ExitCode);
        Assert.Contains("is not the AgenticWorkflow cockpit", install.Output, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sandbox.ModDir, "theirs.txt")));
    }

    [Fact]
    public void An_existing_development_link_is_replaced_by_a_real_copy_without_touching_its_target()
    {
        using var sandbox = new Sandbox(release);
        var target = Directory.CreateDirectory(Path.Combine(sandbox.Root, "dev checkout", "agenticworkflow")).FullName;
        File.WriteAllText(Path.Combine(target, "mod.json"), """{ "title": "AgenticWorkflow", "palette": [ { "title": "dev" } ] }""");
        File.WriteAllText(Path.Combine(target, "dev-only.js"), "dev");
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.ModDir)!);
        Directory.CreateSymbolicLink(sandbox.ModDir, target);

        var install = sandbox.Install();

        Assert.True(install.ExitCode == 0, install.ToString());
        Assert.Null(new DirectoryInfo(sandbox.ModDir).LinkTarget);
        Assert.False(File.Exists(Path.Combine(sandbox.ModDir, "dev-only.js")));
        Assert.True(File.Exists(Path.Combine(target, "dev-only.js")), "the development checkout must be untouched");
    }

    [Fact]
    public void Uninstall_removes_only_what_belongs_to_AgenticWorkflow()
    {
        using var sandbox = new Sandbox(release);
        var otherMod = Directory.CreateDirectory(Path.Combine(sandbox.HerdrHome, "mods", "someone-elses")).FullName;
        File.WriteAllText(Path.Combine(otherMod, "mod.json"), "{}");
        var otherSettings = Directory.CreateDirectory(Path.Combine(sandbox.HerdrHome, "other-settings")).FullName;
        File.WriteAllText(Path.Combine(sandbox.HerdrHome, "config.toml"), "theme = 'dark'");
        sandbox.SetPath(@"C:\Tools");

        Assert.Equal(0, sandbox.Install().ExitCode);

        var uninstall = sandbox.Uninstall();

        Assert.True(uninstall.ExitCode == 0, uninstall.ToString());
        Assert.False(Directory.Exists(sandbox.BinDir));
        Assert.False(Directory.Exists(sandbox.ModDir));
        Assert.False(Directory.Exists(sandbox.SettingsDir));
        Assert.False(Directory.Exists(sandbox.InstallDir));
        Assert.Equal([@"C:\Tools"], sandbox.PathEntries());
        Assert.True(File.Exists(Path.Combine(otherMod, "mod.json")));
        Assert.True(Directory.Exists(otherSettings));
        Assert.Equal("theme = 'dark'", File.ReadAllText(Path.Combine(sandbox.HerdrHome, "config.toml")));
    }

    [Fact]
    public void Uninstalling_again_or_when_nothing_was_installed_is_harmless()
    {
        using var sandbox = new Sandbox(release);

        var never = sandbox.UninstallFromPackage();
        Assert.True(never.ExitCode == 0, never.ToString());
        Assert.Contains("not installed", never.Output, StringComparison.Ordinal);

        Assert.Equal(0, sandbox.Install().ExitCode);
        Assert.Equal(0, sandbox.Uninstall().ExitCode);

        var again = sandbox.UninstallFromPackage();
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Contains("not installed", again.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Purging_the_state_needs_the_explicit_switch_and_deletes_only_a_state_folder()
    {
        using var sandbox = new Sandbox(release);
        Directory.CreateDirectory(Path.Combine(sandbox.StateRoot, "runs", "r1"));
        File.WriteAllText(Path.Combine(sandbox.StateRoot, "runs", "r1", "events.db"), "history");
        Assert.Equal(0, sandbox.Install().ExitCode);

        var purge = sandbox.Uninstall("-PurgeState", "-Force");

        Assert.True(purge.ExitCode == 0, purge.ToString());
        Assert.False(Directory.Exists(sandbox.StateRoot));
    }

    [Fact]
    public void Purging_refuses_a_folder_that_does_not_look_like_a_state_folder()
    {
        using var sandbox = new Sandbox(release);
        Directory.CreateDirectory(sandbox.StateRoot);
        File.WriteAllText(Path.Combine(sandbox.StateRoot, "my-thesis.docx"), "precious");
        Assert.Equal(0, sandbox.Install().ExitCode);

        var purge = sandbox.Uninstall("-PurgeState", "-Force");

        Assert.Equal(1, purge.ExitCode);
        Assert.Contains("does not look like an AgenticWorkflow state folder", purge.Output, StringComparison.Ordinal);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(sandbox.StateRoot, "my-thesis.docx")));
        Assert.True(File.Exists(sandbox.AwExe), "a refused purge must change nothing");
    }

    [Fact]
    public void The_installed_uninstaller_works_without_the_release()
    {
        using var sandbox = new Sandbox(release);
        Assert.Equal(0, sandbox.Install().ExitCode);

        Assert.True(File.Exists(Path.Combine(sandbox.InstallDir, "uninstall.ps1")));
        Assert.True(File.Exists(Path.Combine(sandbox.InstallDir, "setup", "AgenticWorkflow.Setup.ps1")));
    }

    [Fact]
    public void The_installer_scripts_make_no_network_call_and_run_nothing_downloaded()
    {
        foreach (var script in new[] { "install.ps1", "uninstall.ps1", Path.Combine("setup", "AgenticWorkflow.Setup.ps1") })
        {
            var text = File.ReadAllText(Path.Combine(release.PackageRoot, script));

            foreach (var forbidden in new[] { "Invoke-WebRequest", "Invoke-RestMethod", "WebClient", "Net.Http", "Start-BitsTransfer", "curl", "iex ", "Invoke-Expression", "http://", "https://" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
