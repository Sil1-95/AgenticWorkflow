using System.Runtime.InteropServices;

namespace AgenticWorkflow.Cli.Controller;

/// <summary>
/// The termination signals the controller honours: SIGTERM (and SIGQUIT) stop it cleanly. A controller started in the background also ignores SIGHUP, the hang-up of the
/// terminal that happened to launch it, so closing that terminal does not kill the controller other clients are using. Ctrl+C reaches the host through the program's own
/// handler. Windows has no such signals (the registration is then a no-op).
/// </summary>
internal sealed class ControllerSignals : IDisposable
{
    private readonly CancellationTokenSource _terminated = new();
    private readonly List<PosixSignalRegistration> _registrations = [];

    private ControllerSignals()
    {
    }

    public CancellationToken Token => _terminated.Token;

    public static ControllerSignals Attach(bool background)
    {
        var signals = new ControllerSignals();

        if (OperatingSystem.IsWindows())
            return signals;

        signals._registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            signals._terminated.Cancel();
        }));

        if (background)
            signals._registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true));

        return signals;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();

        _terminated.Dispose();
    }
}
