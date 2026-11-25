using System.Text;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Revisions;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// The ordered, bounded record of everything a human has added to a task since it began: each REQUEST CHANGES feedback, each CLARIFY question with its
/// answer and each WHAT or HOW proposal with the human's decision on it (approved, or sent back with feedback), oldest first, each labelled with where it came from
/// (its kind and the semantic revision it created). It is what a FRESH agent session needs to be told, because it remembers nothing of the conversation that
/// held them.
/// <para>
/// It is a projection of the authoritative history, not a summary: nothing is paraphrased, shortened or dropped, and no model is involved. When the whole of it
/// does not fit <see cref="MaxCharacters"/> the projection is not truncated into something that merely looks complete; <see cref="TryRender"/> reports that it
/// does not fit, and the caller refuses the fresh start (the human can still abort, or continue where a resumable conversation exists).
/// </para>
/// </summary>
internal static class HumanInputHistory
{
    /// <summary>
    /// The most characters the rendered history may take. Bounded input (feedback 2000, answer 2000, question and reason 500 each, a WHAT proposal 6000 at most and
    /// typically far less) makes this a few rounds. A history that does not fit is refused, never cut.
    /// </summary>
    public const int MaxCharacters = 12_000;

    /// <summary>The input in the order it was given. Each decision is paired with the semantic revision created in the same atomic append, in order.</summary>
    public static IReadOnlyList<Entry> Of(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var revisions = task.SemanticRevisions
            .Where(r => r.Kind == SemanticChangeKind.HumanRequestChanges
                        || r.Kind == SemanticChangeKind.HumanClarification
                        || r.Kind == SemanticChangeKind.HumanWhatApproval
                        || r.Kind == SemanticChangeKind.HumanWhatChanges
                        || r.Kind == SemanticChangeKind.HumanHowApproval
                        || r.Kind == SemanticChangeKind.HumanHowChanges)
            .OrderBy(r => r.Ordinal)
            .ToList();

        var decisions = task.GateDecisions.Where(d => d.Action is GateAction.RequestChanges or GateAction.Answer or GateAction.Approve).ToList();

        // One revision per decision, created together. A history that says otherwise is not projected: it is the caller's refusal, not a guess.
        if (revisions.Count != decisions.Count)
            return [];

        var human = decisions
            .Select((decision, index) =>
            {
                var gate = task.Gates.FirstOrDefault(g => g.Id == decision.GateId);

                return new Entry(
                    KindOf(decision, gate),
                    revisions[index].Ordinal,
                    decision.Action switch
                    {
                        GateAction.Answer => decision.Answer!,
                        GateAction.Approve => string.Empty,
                        _ => decision.Feedback!,
                    },
                    gate?.Clarification,
                    gate?.What,
                    gate?.How);
            });

        // what the agent reported is part of the task's history too, in the position its semantic revision gives it. A fresh session needs it: without it the
        // approved HOW that follows would read as if the earlier strategy had simply been declined.
        var reported = task.Reclassifications.Select(r =>
            new Entry(
                HumanInputKind.Reclassification,
                task.SemanticRevisions.First(v => v.Id == r.SemanticRevisionId).Ordinal,
                r.Evidence,
                null,
                Reclassification: r));

        return human.Concat(reported).OrderBy(e => e.Revision).ToList();
    }

    private static HumanInputKind KindOf(GateDecision decision, Gate? gate) => decision.Action switch
    {
        GateAction.Answer => HumanInputKind.Clarification,
        GateAction.Approve => gate?.Type == GateType.How ? HumanInputKind.HowApproved : HumanInputKind.WhatApproved,
        _ => gate?.Type switch
        {
            GateType.What => HumanInputKind.WhatChangesRequested,
            GateType.How => HumanInputKind.HowChangesRequested,
            _ => HumanInputKind.RequestedChanges,
        },
    };

    /// <summary>Renders the history, or returns false when it does not fit <see cref="MaxCharacters"/> (nothing is cut).</summary>
    public static bool TryRender(IReadOnlyList<Entry> entries, out string text)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (entry.Kind == HumanInputKind.Reclassification)
            {
                var reclassification = entry.Reclassification!;
                builder.AppendLine($"{i + 1}. You reported new evidence that invalidated the {reclassification.StageName} (semantic revision {entry.Revision}). Evidence, verbatim:");
                builder.AppendLine("<<<");
                builder.AppendLine(reclassification.Evidence.Trim());
                builder.AppendLine(">>>");
                builder.AppendLine($"   Your reason: {reclassification.Reason.Trim()}");
                continue;
            }

            if (entry.Kind == HumanInputKind.WhatApproved)
            {
                builder.AppendLine($"{i + 1}. Human approved the WHAT you proposed (semantic revision {entry.Revision}). The approved WHAT:");
                builder.AppendLine("<<<");
                builder.AppendLine(entry.What is null ? string.Empty : WhatText.Render(entry.What));
                builder.AppendLine(">>>");
                continue;
            }

            if (entry.Kind == HumanInputKind.HowApproved)
            {
                builder.AppendLine($"{i + 1}. Human approved the HOW you proposed (semantic revision {entry.Revision}). The approved HOW:");
                builder.AppendLine("<<<");
                builder.AppendLine(entry.How is null ? string.Empty : HowText.Render(entry.How));
                builder.AppendLine(">>>");
                continue;
            }

            if (entry.Kind == HumanInputKind.HowChangesRequested)
            {
                builder.AppendLine($"{i + 1}. Human sent the HOW you proposed back for changes (semantic revision {entry.Revision}). The proposal that was sent back:");
                builder.AppendLine("<<<");
                builder.AppendLine(entry.How is null ? string.Empty : HowText.Render(entry.How));
                builder.AppendLine(">>>");
                builder.AppendLine("   Feedback, verbatim:");
            }
            else if (entry.Kind == HumanInputKind.WhatChangesRequested)
            {
                builder.AppendLine($"{i + 1}. Human sent the WHAT you proposed back for changes (semantic revision {entry.Revision}). The proposal that was sent back:");
                builder.AppendLine("<<<");
                builder.AppendLine(entry.What is null ? string.Empty : WhatText.Render(entry.What));
                builder.AppendLine(">>>");
                builder.AppendLine("   Feedback, verbatim:");
            }
            else if (entry.Kind == HumanInputKind.Clarification)
            {
                builder.AppendLine($"{i + 1}. Human answer to a question you raised (semantic revision {entry.Revision}):");
                builder.AppendLine($"   Question: {entry.Question?.Question}");
                builder.AppendLine($"   Why it mattered: {entry.Question?.Reason}");
                builder.AppendLine("   Answer, verbatim:");
            }
            else
            {
                builder.AppendLine($"{i + 1}. Human-requested changes to a result that was already verified (semantic revision {entry.Revision}), verbatim:");
            }

            builder.AppendLine("<<<");
            builder.AppendLine(entry.Text.Trim());
            builder.AppendLine(">>>");
        }

        text = builder.ToString().TrimEnd();
        return text.Length <= MaxCharacters;
    }

    internal sealed record Entry(HumanInputKind Kind, int Revision, string Text, ClarificationRequest? Question, WhatProposal? What = null, HowProposal? How = null, Reclassification? Reclassification = null);
}
