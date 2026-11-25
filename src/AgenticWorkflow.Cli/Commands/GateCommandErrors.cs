using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Acceptance;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Cli.Reporting;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core;
using AgenticWorkflow.Persistence.Sqlite;

namespace AgenticWorkflow.Cli.Commands;

/// <summary>
/// How the commands that decide a gate or continue its work (<c>accept</c>, <c>request-changes</c>, <c>continue</c>) report the failures they share: a refused
/// decision or continuation, a missing run, and a run whose durable state cannot be trusted. Nothing is repaired or guessed, and every one of these means
/// nothing was recorded.
/// </summary>
internal static class GateCommandErrors
{
    public static bool Handles(Exception e)
        => e is GateRefusedException or ContinuationRefusedException or RunStateException or EventStoreException or DomainException or TaskNotFoundException or Microsoft.Data.Sqlite.SqliteException;

    public static int Report(CliServices services, string action, Exception e)
    {
        switch (e)
        {
            case GateRefusedException refused:
                services.Error.WriteLine($"{action} REFUSED ({Describe(refused.Reason)}): {refused.Message}");
                services.Error.WriteLine("Nothing was recorded. 'aw status <run-id>' shows what the run is waiting for.");
                return ExitCodes.GateDecisionRefused;

            case ContinuationRefusedException refusedContinuation:
                services.Error.WriteLine($"{action} REFUSED ({Describe(refusedContinuation.Reason)}): {refusedContinuation.Message}");
                services.Error.WriteLine("Nothing was recorded. 'aw status <run-id>' shows what the run is waiting for.");
                return ExitCodes.GateDecisionRefused;

            case RunStateException state:
                services.Error.WriteLine($"{(state.Problem == RunStateProblem.NotFound ? "RUN NOT FOUND" : "RUN STATE UNREADABLE")}: {state.Message}");

                if (state.Problem == RunStateProblem.Unreadable)
                    services.Error.WriteLine("Nothing was repaired or guessed; the run's files were left untouched.");

                return state.Problem == RunStateProblem.NotFound ? ExitCodes.RunNotFound : ExitCodes.RunStateUnreadable;

            default:
                services.Error.WriteLine($"RUN STATE UNREADABLE: {e.Message}");
                services.Error.WriteLine("Nothing was repaired or guessed; the run's files were left untouched.");
                return ExitCodes.RunStateUnreadable;
        }
    }

    public static string Describe(ContinuationRefusal reason) => reason switch
    {
        ContinuationRefusal.NotBlocked => "run is not blocked",
        ContinuationRefusal.NotRecoverable => "not a recoverable continuation",
        ContinuationRefusal.BaselineUnavailable => "the request's workspace baseline is unavailable",
        ContinuationRefusal.StateMoved => "run changed meanwhile",
        ContinuationRefusal.SessionNotStopped => "the interrupted attempt's agent session is not confirmed stopped",
        _ => reason.ToString(),
    };

    public static string Describe(GateRefusal reason) => reason switch
    {
        GateRefusal.AcceptanceNotRequired => "no acceptance required",
        GateRefusal.NoPendingGate => "no gate is waiting",
        GateRefusal.UnknownGate => "unknown gate",
        GateRefusal.AlreadyDecided => "gate already decided",
        GateRefusal.NotCurrent => "stale gate",
        GateRefusal.WorkspaceChanged => "working tree changed",
        GateRefusal.WorkspaceUnavailable => "working tree unavailable",
        GateRefusal.WrongGateKind => "the gate is of another kind",
        GateRefusal.StateMoved => "run changed meanwhile",
        GateRefusal.ConversationUnusable => "recorded agent conversation cannot be trusted",
        _ => reason.ToString(),
    };
}
