using Microsoft.Win32;

namespace AgenticWorkflow.Packaging.Tests;

/// <summary>
/// One isolated "user": an install folder, a Herdr home, a state folder and a user-environment registry key, all under a temporary folder whose name contains a space. Nothing here can reach the
/// real user's PATH, Herdr home or run history: every script is told these locations explicitly, and the registry key is a private one that is deleted afterwards.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    private const string KeyParent = @"Software\AgenticWorkflowTests";

    private readonly string _keyName = Guid.NewGuid().ToString("N");

    public Sandbox(ReleaseFixture release, bool copyPackage = false)
    {
        Release = release;
        Root = Directory.CreateTempSubdirectory("aw sandbox ").FullName;
        InstallDir = Path.Combine(Root, "Programs", "Agentic Workflow");
        HerdrHome = Path.Combine(Root, "herdr home");
        StateRoot = Path.Combine(Root, "state");
        Package = copyPackage ? release.CopyPackage(Path.Combine(Root, "release")) : release.PackageRoot;

        Registry.CurrentUser.CreateSubKey($@"{KeyParent}\{_keyName}").Dispose();
    }

    public ReleaseFixture Release { get; }

    public string Root { get; }

    public string InstallDir { get; }

    public string BinDir => Path.Combine(InstallDir, "bin");

    public string AwExe => Path.Combine(BinDir, "aw.exe");

    public string HerdrHome { get; }

    public string ModDir => Path.Combine(HerdrHome, "mods", "agenticworkflow");

    public string SettingsDir => Path.Combine(HerdrHome, "agenticworkflow");

    public string StateRoot { get; }

    /// <summary>The extracted release this sandbox installs from (a private copy when the test changes it).</summary>
    public string Package { get; }

    public string EnvironmentKey => $@"HKCU:\{KeyParent}\{_keyName}";

    public IReadOnlyDictionary<string, string?> Environment => new Dictionary<string, string?>
    {
        ["AGENTICWORKFLOW_HOME"] = StateRoot,
        ["HERDR_APP_HOME"] = HerdrHome,
    };

    public Outcome Install(params string[] extra) => Script(Path.Combine(Package, "install.ps1"), extra);

    public Outcome Uninstall(params string[] extra) => Script(Path.Combine(InstallDir, "uninstall.ps1"), extra);

    /// <summary>The uninstaller of the release (what a person runs when they still have the ZIP, or when the installed copy is gone).</summary>
    public Outcome UninstallFromPackage(params string[] extra) => Script(Path.Combine(Package, "uninstall.ps1"), extra);

    public Outcome Aw(params string[] arguments) => ReleaseFixture.Run(AwExe, arguments, Environment);

    /// <summary>The user's PATH entries in the sandbox's registry key, as stored (unexpanded).</summary>
    public IReadOnlyList<string> PathEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyParent}\{_keyName}");
        var value = key?.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;

        return value.Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    public void SetPath(string value, RegistryValueKind kind = RegistryValueKind.ExpandString)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{KeyParent}\{_keyName}");
        key.SetValue("Path", value, kind);
    }

    public RegistryValueKind PathKind()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyParent}\{_keyName}")!;

        return key.GetValueKind("Path");
    }

    public void Dispose()
    {
        // A controller a test started must not outlive it (it would also lock the files being deleted).
        if (File.Exists(AwExe))
            ReleaseFixture.Run(AwExe, ["ctl", "stop", "--force"], Environment);

        Registry.CurrentUser.DeleteSubKeyTree($@"{KeyParent}\{_keyName}", throwOnMissingSubKey: false);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private Outcome Script(string script, string[] extra)
    {
        var arguments = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };

        if (!extra.Contains("-InstallDir"))
            arguments.AddRange(["-InstallDir", InstallDir]);

        arguments.AddRange(["-HerdrHome", HerdrHome, "-StateRoot", StateRoot, "-UserEnvironmentKey", EnvironmentKey]);
        arguments.AddRange(extra);

        return ReleaseFixture.Run("powershell.exe", arguments, Environment);
    }
}
