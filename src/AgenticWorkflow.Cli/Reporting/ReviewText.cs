using System.Text;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Reporting;

/// <summary>
/// How the independent REVIEW reads in <c>aw status</c> and in the run report, from the authoritative history alone. It shows the requirement, the reviewer's explicit
/// profile, the current state against the semantics, implementation and verification, every round in order and what happens next. It never shows a transcript: a review
/// holds none, only the structured report and its provenance.
/// </summary>
internal static class ReviewText
{
    private const int ShortSessionLength = 8;
    private const int SummaryLimit = 400;
    private const int FindingLimit = 300;

    /// <summary>True when there is anything to say about reviews for this task.</summary>
    public static bool Applies(WorkflowTask task) => task.IsReviewRequired || !task.Reviews.IsEmpty;

    /// <summary>The one-line state: <c>PASS (current)</c>, <c>PASS (STALE)</c>, <c>CHANGES REQUIRED</c>, <c>none yet</c>.</summary>
    public static string State(WorkflowTask task) => task.ReviewStatus switch
    {
        ReviewStatus.PassedCurrent => "PASS (current)",
        ReviewStatus.PassedStale => "PASS (STALE: the task changed since; historical, it cannot complete the run)",
        ReviewStatus.ChangesRequired => "CHANGES REQUIRED (the implementer has not answered the findings yet)",
        ReviewStatus.ChangesRequiredStale => "CHANGES REQUIRED (STALE: the task changed since; a fresh review is due once the work is verified)",
        _ => task.IsReviewDue ? "none yet (due now: the implementation is verified)" : "none yet (due once the implementation is verified)",
    };

    /// <summary>The header lines of <c>aw status</c> that describe the requirement and the reviewer.</summary>
    public static void AppendRequirement(StringBuilder text, WorkflowTask task)
    {
        if (task.ReviewRequired)
            text.AppendLine("Required: independent REVIEW (--require review; fixed when the run was created)");
        else if (task.ReviewEscalated)
            text.AppendLine($"Required: independent REVIEW (requested by the agent: {Format.Bounded(task.ReviewRequests[0].Reason, 200)}; additive, it cannot be lowered)");
    }

    /// <summary>The full review section: reviewer, state, rounds in order, next step.</summary>
    public static void Append(StringBuilder text, WorkflowTask task)
    {
        if (!Applies(task))
            return;

        text.AppendLine(task.RoleBindings.TryGetValue(WellKnownRoles.Reviewer, out var binding)
            ? $"Reviewer: profile '{binding.Profile.Id}' (explicitly bound; a fresh conversation for every review)"
            : "Reviewer: NOT BOUND (no reviewerProfile; the implementer's profile is never used instead)");

        text.AppendLine($"Review: {State(task)}");

        foreach (var review in task.Reviews)
            AppendRound(text, task, review);

        var next = NextStep(task);

        if (next is not null)
            text.AppendLine($"Review next step: {next}");
    }

    /// <summary>The rounds in the order they were recorded; the final run report keeps them all.</summary>
    public static void AppendRound(StringBuilder text, WorkflowTask task, Review review)
    {
        var currency = task.IsReviewCurrent(review) ? "current" : "historical";
        var verdict = review.Verdict == ReviewVerdict.Pass ? "PASS" : "CHANGES_REQUIRED";
        var cost = $"{Format.Duration(TimeSpan.FromMilliseconds(review.ReviewerMilliseconds))}, {review.ReviewerCommandCount} reviewer command(s){(review.ConfiguredCheckRerun ? ", a configured check was re-run" : ", no configured check re-run")}";

        text.AppendLine($"  Review round {review.Round}: {verdict} ({currency})");
        text.AppendLine($"    reviewer conversation {Short(review.ReviewerSession)}, independent of the implementer's {Short(review.ImplementerSession)}; report {Short(review.ReportHash, 12)}; {cost}");
        text.AppendLine($"    summary: {Format.Bounded(review.Report.Summary, SummaryLimit)}");

        var number = 1;
        foreach (var finding in review.Report.Findings)
        {
            var where = finding.Location is null ? string.Empty : $" ({Format.Bounded(finding.Location, 120)})";
            text.AppendLine($"    finding {number++}: {Format.Bounded(finding.Title, FindingLimit)}{where}");
            text.AppendLine($"      evidence: {Format.Bounded(finding.Evidence, FindingLimit)}");
            text.AppendLine($"      recommendation: {Format.Bounded(finding.Recommendation, FindingLimit)}");
        }

        if (review.Report.Notes is { } notes)
            text.AppendLine($"    notes (non-blocking): {Format.Bounded(notes, FindingLimit)}");
    }

    /// <summary>What the workflow does next about the review, or null when nothing about the review is pending.</summary>
    public static string? NextStep(WorkflowTask task)
    {
        if (!task.IsReviewRequired || task.WorkflowState is WorkflowState.Completed or WorkflowState.Cancelled)
            return null;

        return task.ReviewStatus switch
        {
            ReviewStatus.PassedCurrent => task.Acceptance == AcceptanceRequirement.HumanRequired
                ? "the review passed; the ACCEPT gate is next (REVIEW is never ACCEPT: you still decide)"
                : "the review passed; the controller completes the run",
            ReviewStatus.ChangesRequired => "the ORIGINAL implementer repairs the findings (resumed with the findings only), the repair is verified again, then a NEW fresh reviewer reviews",
            ReviewStatus.PassedStale or ReviewStatus.ChangesRequiredStale => "re-verify the current implementation, then a NEW fresh reviewer reviews it",
            _ => task.IsReviewDue
                ? "a fresh independent reviewer reviews the verified implementation"
                : "implement and verify first, then a fresh independent reviewer reviews",
        };
    }

    private static string Short(string value, int length = ShortSessionLength)
        => value.Length <= length ? value : value[..length];
}
