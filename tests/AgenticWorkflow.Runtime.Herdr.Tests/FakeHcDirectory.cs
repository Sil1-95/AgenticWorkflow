namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// A throwaway directory (deliberately containing a space) holding a scripted stand-in for <c>hc</c>,
/// so client behaviour can be tested without a Herdr installation. Windows batch file.
/// </summary>
internal sealed class FakeHcDirectory : IDisposable
{
    private readonly string _directory;

    public FakeHcDirectory(string scriptBody)
    {
        _directory = Path.Combine(Path.GetTempPath(), "aw m0 fake " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);

        ExecutablePath = Path.Combine(_directory, "hc.cmd");
        File.WriteAllText(ExecutablePath, "@echo off\r\n" + scriptBody + "\r\n");
    }

    public string ExecutablePath { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
