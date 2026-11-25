using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Application.ReadModel;

/// <summary>
/// Projects a replayed <see cref="WorkflowTask"/> to its <see cref="TaskReadModel"/>. It is a pure function of the task (and, for <see cref="TaskReadModel.Operational"/>,
/// of the facts the host passes in): it reads no store, keeps no state and asks the same Core and Application predicates the controller asks (currentness, what is due,
/// completion eligibility, what a block is), so the model can never disagree with what the controller would do. Where it names something the controller has no single
/// predicate for (which stage is active, which gates are stale for which reason), the derivation is here, in one place, and is tested against those predicates.
/// </summary>
public static partial class TaskReadModelProjector
{
    /// <param name="task">The task as replayed from its durable event stream.</param>
    /// <param name="operational">Facts about the run that are not workflow state, or null when the host has none.</param>
    public static TaskReadModel Project(WorkflowTask task, OperationalView? operational = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var (stage, reason) = ActiveStage(task);

        return new TaskReadModel
        {
            SchemaVersion = TaskReadModel.CurrentSchemaVersion,
            TaskId = task.Id.Value,
            Intent = task.Intent,
            CreatedAt = task.CreatedAt,
            LastEventAt = task.LastEventAt,
            StreamVersion = task.LastSequence,
            State = task.WorkflowState,
            StateReason = task.StateReason,
            CompletionBoundary = task.CompletionBoundary.Value,
            Stage = stage,
            StageReason = reason,
            Requirements = Requirements(task),
            SemanticRevision = SemanticRevision(task, task.CurrentSemanticRevision),
            SemanticRevisions = [.. task.SemanticRevisions.Select(r => SemanticRevision(task, r))],
            Implementation = Implementation(task),
            Verification = Verification(task),
            Gates = [.. task.Gates.Select(g => Gate(task, g))],
            PendingRequest = PendingRequest(task),
            Reclassifications = [.. task.Reclassifications.Select(r => Reclassification(task, r))],
            Review = Review(task),
            Proof = Proof(task),
            Completion = Completion(task),
            Block = Block(task),
            Recovery = Recovery(task),
            Bindings = Bindings(task),
            MissingBindings = MissingBindings(task),
            Operational = operational,
        };
    }

    /// <summary>
    /// Where the workflow stands and why, from the same predicates the runner steps through, in the same order: an open gate; a required stage that is not approved;
    /// the implementation; its verification; the proof contract the implementer owes; the review; the proof; and finally the ACCEPT or the controller's completion.
    /// </summary>
    internal static (WorkflowStage Stage, StageReason Reason) ActiveStage(WorkflowTask task)
    {
        if (task.IsTerminal)
            return (WorkflowStage.None, StageReason.Terminal);

        if (task.PendingGate is { } gate)
        {
            return gate.Type switch
            {
                GateType.Clarify => (WorkflowStage.Clarify, StageReason.AwaitingHumanAnswer),
                GateType.What => (WorkflowStage.What, StageReason.AwaitingHumanApproval),
                GateType.How => (WorkflowStage.How, StageReason.AwaitingHumanApproval),
                _ => (WorkflowStage.Accept, StageReason.AwaitingHumanAcceptance),
            };
        }

        if (!task.MayImplement)
        {
            var isWhat = !task.WhatSatisfied;
            var latest = isWhat ? task.LatestWhatGate : task.LatestHowGate;

            var reason = latest is null
                ? StageReason.ProposalNotYetMade
                : task.StatusOf(latest) switch
                {
                    GateStatus.Invalidated => StageReason.ApprovalInvalidated,
                    GateStatus.ChangesRequested => StageReason.ProposalRevisionOwed,
                    _ => StageReason.ProposalNotYetMade,
                };

            return (isWhat ? WorkflowStage.What : WorkflowStage.How, reason);
        }

        if (!task.IsImplementationCurrent)
        {
            var actedOn = PendingHumanInput.Find(task) is { Kind: HumanInputKind.RequestedChanges or HumanInputKind.Clarification or HumanInputKind.WhatApproved or HumanInputKind.HowApproved };

            return (WorkflowStage.Implement, actedOn
                ? StageReason.HumanInputBeingActedOn
                : task.CurrentImplementationRevision is null ? StageReason.InitialImplementation : StageReason.ImplementationStale);
        }

        if (task.CurrentVerification is not { } verification)
            return (WorkflowStage.Verify, StageReason.AwaitingVerification);

        if (verification.Outcome != VerificationOutcome.Passed)
            return (WorkflowStage.Implement, StageReason.VerificationFailed);

        // The contract comes first so that a review is never spent on work that cannot then be proved (the runner's own order).
        if (task.IsProofContractMissing)
            return (WorkflowStage.Prove, StageReason.ProofContractOwed);

        if (!task.IsReviewSatisfied)
            return task.IsReviewDue ? (WorkflowStage.Review, StageReason.ReviewDue) : (WorkflowStage.Implement, StageReason.ReviewFindingsOwed);

        if (!task.IsProveSatisfied)
            return task.IsProveDue ? (WorkflowStage.Prove, StageReason.ProveDue) : (WorkflowStage.Implement, StageReason.ProofFindingsOwed);

        return task.Acceptance == AcceptanceRequirement.HumanRequired
            ? (WorkflowStage.Accept, StageReason.AcceptGateNotYetOpened)
            : (WorkflowStage.Completion, StageReason.EligibleToComplete);
    }

    private static int Ordinal(WorkflowTask task, SemanticRevisionId id) => task.SemanticRevisions.First(r => r.Id == id).Ordinal;

    private static RequirementsView Requirements(WorkflowTask task) => new()
    {
        Acceptance = task.Acceptance,
        PredatesAcceptanceRequirement = task.PredatesAcceptanceRequirement,
        What = new StageRequirementView
        {
            Origin = task.WhatRequired ? RequirementOrigin.Human : task.WhatEscalated || task.LatestWhatGate is not null ? RequirementOrigin.Agent : RequirementOrigin.None,
            Satisfied = task.WhatSatisfied,
        },
        How = new StageRequirementView
        {
            Origin = task.HowRequired ? RequirementOrigin.Human : task.HowEscalated || task.LatestHowGate is not null ? RequirementOrigin.Agent : RequirementOrigin.None,
            Satisfied = task.HowSatisfied,
        },
        Review = new StageRequirementView
        {
            Origin = task.ReviewRequired ? RequirementOrigin.Human : task.ReviewEscalated ? RequirementOrigin.Agent : RequirementOrigin.None,
            Satisfied = task.IsReviewSatisfied,
        },
        Prove = new StageRequirementView
        {
            Origin = task.ProveRequired ? RequirementOrigin.Human : task.ProveEscalated ? RequirementOrigin.Agent : RequirementOrigin.None,
            Satisfied = task.IsProveSatisfied,
        },
    };

    private static SemanticRevisionView SemanticRevision(WorkflowTask task, SemanticRevision revision) => new()
    {
        Id = revision.Id.Value,
        Ordinal = revision.Ordinal,
        Kind = revision.Kind.Value,
        Explanation = revision.Explanation,
        CreatedAt = revision.CreatedAt,
        IsCurrent = revision.Id == task.CurrentSemanticRevisionId,
    };

    private static ImplementationView Implementation(WorkflowTask task) => new()
    {
        Currency = task.CurrentImplementationRevision is null ? EvidenceCurrency.None : task.IsImplementationCurrent ? EvidenceCurrency.Current : EvidenceCurrency.Stale,
        Latest = task.CurrentImplementationRevision is { } latest ? ImplementationRevision(task, latest) : null,
        HasProvisionalWork = task.HasProvisionalWork,
        Revisions = [.. task.ImplementationRevisions.Select(r => ImplementationRevision(task, r))],
    };

    private static ImplementationRevisionView ImplementationRevision(WorkflowTask task, ImplementationRevision revision) => new()
    {
        Id = revision.Id.Value,
        Fingerprint = revision.Fingerprint.Value,
        SemanticRevisionId = revision.SemanticRevisionId.Value,
        SemanticOrdinal = Ordinal(task, revision.SemanticRevisionId),
        ParentId = revision.ParentId?.Value,
        CreatedAt = revision.CreatedAt,
        IsLatest = revision.Id == task.CurrentImplementationRevisionId,
        TargetsCurrentSemantics = revision.SemanticRevisionId == task.CurrentSemanticRevisionId,
    };

    private static VerificationView Verification(WorkflowTask task)
    {
        var current = task.CurrentVerification;

        return new VerificationView
        {
            Currency = current is null ? EvidenceCurrency.None : task.IsVerificationStale ? EvidenceCurrency.Stale : EvidenceCurrency.Current,
            Outcome = current?.Outcome,
            CurrentRunId = current?.Id.Value,
            Runs = [.. task.Verifications.Select(v => VerificationRun(v, v.Id == current?.Id))],
        };
    }

    private static VerificationRunView VerificationRun(VerificationRun run, bool isCurrent) => new()
    {
        Id = run.Id.Value,
        ImplementationRevisionId = run.ImplementationRevisionId.Value,
        Outcome = run.Outcome,
        Timestamp = run.Timestamp,
        IsCurrent = isCurrent,
        Checks = [.. run.Checks.Select(c => new VerificationCheckView
        {
            CheckId = c.CheckId.Value,
            Outcome = c.Outcome,
            Summary = c.Summary,
            ExitCode = c.ExitCode,
            Source = c.Source == VerificationEvidenceSource.AgentObserved ? "agent_observed" : "controller_executed",
            EvidenceReason = c.EvidenceReason,
            Observation = c.Observation is { } observed
                ? new AgentObservationView
                {
                    Runtime = observed.Runtime,
                    ToolUseId = observed.ToolUseId,
                    CommandIdentity = observed.CommandIdentity,
                    DurationMilliseconds = observed.DurationMilliseconds,
                }
                : null,
        })],
    };

    private static ReclassificationView Reclassification(WorkflowTask task, Reclassification reclassification) => new()
    {
        Id = reclassification.Id.Value,
        Stage = reclassification.Stage,
        Evidence = reclassification.Evidence,
        Reason = reclassification.Reason,
        Turn = reclassification.Turn,
        Session = reclassification.Session,
        PriorSemanticOrdinal = Ordinal(task, reclassification.PriorSemanticRevisionId),
        SemanticOrdinal = Ordinal(task, reclassification.SemanticRevisionId),
        ProvisionalWork = reclassification.ProvisionalWork,
        WorkspaceFingerprint = reclassification.Workspace.Value,
        InvalidatedGateIds = [.. reclassification.InvalidatedGates.Select(g => g.Value)],
        IsActive = task.ActiveReclassification is { } active && active.Id == reclassification.Id,
    };

    private static IReadOnlyList<RoleBindingView> Bindings(WorkflowTask task)
        => [.. task.RoleBindings.Values
            .OrderBy(b => b.Role.Id.Value, StringComparer.Ordinal)
            .Select(b => new RoleBindingView
            {
                Role = b.Role.Id.Value,
                Profile = new ProfileView
                {
                    Id = b.Profile.Id.Value,
                    Description = b.Profile.Description,
                    Capabilities = [.. b.Profile.Capabilities.Select(c => c.Value).Order(StringComparer.Ordinal)],
                },
                BoundAtSemanticOrdinal = Ordinal(task, b.BoundAt),
                Configuration = b.Execution is { } execution
                    ? new ExecutionConfigurationView { Harness = execution.Harness, Model = execution.Model, Reasoning = execution.Reasoning, Fingerprint = execution.Fingerprint }
                    : null,
            })];

    /// <summary>The roles the task needs now without a binding. Derived from the same predicates the runner steps through; a terminal task needs nothing.</summary>
    private static IReadOnlyList<MissingBindingView> MissingBindings(WorkflowTask task)
    {
        if (task.IsTerminal)
            return [];

        var missing = new List<MissingBindingView>();

        void Needs(Role role, string reason)
        {
            if (!task.RoleBindings.ContainsKey(role.Id))
                missing.Add(new MissingBindingView { Role = role.Id.Value, Reason = reason, RequiredCapabilities = [.. role.RequiredCapabilities.Select(c => c.Value).Order(StringComparer.Ordinal)] });
        }

        Needs(DirectPathRoles.Root, "work_requires_root");

        if (task.IsReviewRequired && !task.IsReviewSatisfied)
            Needs(DirectPathRoles.Reviewer, "review_required");

        if (task.IsProveRequired && !task.IsProveSatisfied)
            Needs(DirectPathRoles.Prover, "prove_required");

        return missing;
    }
}
