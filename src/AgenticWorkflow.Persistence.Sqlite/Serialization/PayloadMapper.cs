using AgenticWorkflow.Core.Authority;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Policy;
using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Revisions;
using AgenticWorkflow.Core.Roles;
using AgenticWorkflow.Core.Verification;

namespace AgenticWorkflow.Persistence.Sqlite.Serialization;

/// <summary>
/// Explicit, hand-written mapping between Core domain values and the version-1 payload DTOs. Nothing here relies
/// on a serializer discovering Core's constructors or property names. Malformed stored values surface as
/// <see cref="FormatException"/> (text that is not an id/instant/known name) or as Core's own
/// <see cref="ArgumentException"/> (an identifier or text that Core refuses); <see cref="EventCodec"/> turns both
/// into corruption errors. Core <c>DomainException</c>s thrown by record constructors are left alone: the data
/// decoded but describes something the domain does not allow.
/// </summary>
internal static class PayloadMapper
{
    // ---- Core -> DTO ----------------------------------------------------------------------------------------

    public static SemanticRevisionDto ToDto(SemanticRevision revision) => new()
    {
        Id = Text(revision.Id.Value),
        TaskId = Text(revision.TaskId.Value),
        Ordinal = revision.Ordinal,
        ParentId = revision.ParentId is { } parent ? Text(parent.Value) : null,
        Kind = revision.Kind.Value,
        Explanation = revision.Explanation,
        CreatedAt = Timestamps.ToText(revision.CreatedAt),
    };

    public static ReclassificationDto ToDto(Reclassification reclassification) => new()
    {
        Id = Text(reclassification.Id.Value),
        Stage = StageName(reclassification.Stage),
        Evidence = reclassification.Evidence,
        Reason = reclassification.Reason,
        Turn = reclassification.Turn,
        Session = reclassification.Session,
        PriorSemanticRevisionId = Text(reclassification.PriorSemanticRevisionId.Value),
        SemanticRevisionId = Text(reclassification.SemanticRevisionId.Value),
        Workspace = reclassification.Workspace.Value,
        ProvisionalWork = reclassification.ProvisionalWork,
        InvalidatedGates = [.. reclassification.InvalidatedGates.Select(g => Text(g.Value))],
    };

    public static ReviewRequestDto ToDto(ReviewRequest request) => new()
    {
        Id = Text(request.Id.Value),
        Reason = request.Reason,
        Turn = request.Turn,
        Session = request.Session,
        SemanticRevisionId = Text(request.SemanticRevisionId.Value),
    };

    public static ReviewDto ToDto(Review review) => new()
    {
        Id = Text(review.Id.Value),
        Round = review.Round,
        Verdict = review.Verdict == ReviewVerdict.Pass ? "pass" : "changes_required",
        Summary = review.Report.Summary,
        Findings =
        [
            .. review.Report.Findings.Select(f => new ReviewFindingDto
            {
                Title = f.Title,
                Evidence = f.Evidence,
                Impact = f.Impact,
                Recommendation = f.Recommendation,
                Location = f.Location,
            }),
        ],
        Notes = review.Report.Notes,
        ReportHash = review.ReportHash,
        ReviewerProfile = review.ReviewerProfile.Value,
        ReviewerSession = review.ReviewerSession,
        ImplementerSession = review.ImplementerSession,
        SemanticRevisionId = Text(review.SemanticRevisionId.Value),
        ImplementationRevisionId = Text(review.ImplementationRevisionId.Value),
        VerificationRunId = Text(review.VerificationRunId.Value),
        RecordedAt = Timestamps.ToText(review.RecordedAt),
        ReviewerMilliseconds = review.ReviewerMilliseconds,
        ReviewerCommandCount = review.ReviewerCommandCount,
        ReviewerCommands = [.. review.ReviewerCommands],
        ConfiguredCheckRerun = review.ConfiguredCheckRerun,
    };

    public static ProofContractDto ToDto(ProofContract contract) => new()
    {
        Id = Text(contract.Id.Value),
        SemanticRevisionId = Text(contract.SemanticRevisionId.Value),
        Turn = contract.Turn,
        Session = contract.Session,
        Rationale = contract.Rationale,
        Claims =
        [
            .. contract.Claims.Select(c => new ProofClaimDto
            {
                Id = c.Id,
                Statement = c.Statement,
                Evidence = [.. c.Evidence.Select(e => new ProofEvidenceRequirementDto { Kind = ProofEvidenceRequirement.KindName(e.Kind), Subject = e.Subject })],
                AcceptanceCriterion = c.AcceptanceCriterion,
            }),
        ],
        ContentHash = contract.ContentHash,
    };

    public static ProofDto ToDto(Proof proof) => new()
    {
        Id = Text(proof.Id.Value),
        Round = proof.Round,
        ContractId = Text(proof.ContractId.Value),
        ContractHash = proof.ContractHash,
        Summary = proof.Report.Summary,
        Claims =
        [
            .. proof.Report.Claims.Select(c => new ProofClaimResultDto
            {
                ClaimId = c.ClaimId,
                Verdict = ProofReport.VerdictName(c.Verdict),
                Evidence = [.. c.Evidence.Select(e => e.ToString())],
                Summary = c.Summary,
            }),
        ],
        ReportHash = proof.ReportHash,
        ProverProfile = proof.ProverProfile.Value,
        ProverSession = proof.ProverSession,
        ImplementerSession = proof.ImplementerSession,
        SemanticRevisionId = Text(proof.SemanticRevisionId.Value),
        ImplementationRevisionId = Text(proof.ImplementationRevisionId.Value),
        VerificationRunId = Text(proof.VerificationRunId.Value),
        ReviewId = proof.ReviewId is { } review ? Text(review.Value) : null,
        RecordedAt = Timestamps.ToText(proof.RecordedAt),
        ProverMilliseconds = proof.ProverMilliseconds,
        ProverCommandCount = proof.ProverCommandCount,
        ProverCommands = [.. proof.ProverCommands],
        ConfiguredCheckRerun = proof.ConfiguredCheckRerun,
        DuplicatedVerificationMilliseconds = proof.DuplicatedVerificationMilliseconds,
    };

    private static string StageName(ReclassificationStage stage) => stage switch
    {
        ReclassificationStage.What => "what",
        ReclassificationStage.How => "how",
        _ => throw new NotSupportedException($"The persistence codec has no name for reclassification stage {stage}."),
    };

    public static ImplementationRevisionDto ToDto(ImplementationRevision revision) => new()
    {
        Id = Text(revision.Id.Value),
        TaskId = Text(revision.TaskId.Value),
        SemanticRevisionId = Text(revision.SemanticRevisionId.Value),
        Fingerprint = revision.Fingerprint.Value,
        ParentId = revision.ParentId is { } parent ? Text(parent.Value) : null,
        CreatedAt = Timestamps.ToText(revision.CreatedAt),
    };

    public static RoleBindingDto ToDto(RoleBinding binding) => new()
    {
        Role = new RoleDto
        {
            Id = binding.Role.Id.Value,
            RequiredCapabilities = Values(binding.Role.RequiredCapabilities),
        },
        Profile = new ProfileDto
        {
            Id = binding.Profile.Id.Value,
            Capabilities = Values(binding.Profile.Capabilities),
            Description = binding.Profile.Description,
        },
        BoundAt = Text(binding.BoundAt.Value),
        Execution = binding.Execution is { } execution
            ? new ExecutionConfigurationDto { Harness = execution.Harness, Model = execution.Model, Reasoning = execution.Reasoning, Fingerprint = execution.Fingerprint }
            : null,
    };

    public static AuthorityEnvelopeDto ToDto(AuthorityEnvelope envelope) => new()
    {
        AllowedWithoutHuman = Values(envelope.AllowedWithoutHuman),
        ReservedForHuman = Values(envelope.ReservedForHuman),
    };

    public static PolicyDecisionDto ToDto(PolicyDecision decision) => new()
    {
        TaskId = Text(decision.TaskId.Value),
        Rule = decision.Rule.Value,
        Effect = EffectName(decision.Effect),
        Trigger = new PolicyTriggerDto
        {
            Description = decision.Trigger.Description,
            Action = decision.Trigger.Action?.Value,
            Reference = decision.Trigger.Reference,
        },
        Explanation = decision.Explanation,
        DecidedAt = Timestamps.ToText(decision.DecidedAt),
        SemanticRevisionId = decision.SemanticRevisionId is { } semantic ? Text(semantic.Value) : null,
        ImplementationRevisionId = decision.ImplementationRevisionId is { } implementation ? Text(implementation.Value) : null,
    };

    public static VerificationRunV3Dto ToDto(VerificationRun run) => new()
    {
        Id = Text(run.Id.Value),
        TaskId = Text(run.TaskId.Value),
        ImplementationRevisionId = Text(run.ImplementationRevisionId.Value),
        Outcome = OutcomeName(run.Outcome),
        Checks = run.Checks.Select(check => new VerificationCheckResultV3Dto
        {
            CheckId = check.CheckId.Value,
            Outcome = OutcomeName(check.Outcome),
            Summary = check.Summary,
            ExitCode = check.ExitCode,
            EvidenceReason = check.EvidenceReason,
            Source = SourceName(check.Source),
            Observation = check.Observation is { } observation
                ? new AgentObservationDto
                {
                    Runtime = observation.Runtime,
                    ToolUseId = observation.ToolUseId,
                    CommandIdentity = observation.CommandIdentity,
                    DurationMs = observation.DurationMilliseconds,
                    StateFingerprint = observation.StateFingerprint.Value,
                }
                : null,
        }).ToArray(),
        Timestamp = Timestamps.ToText(run.Timestamp),
    };

    public static GateDto ToDto(Gate gate) => new()
    {
        Id = Text(gate.Id.Value),
        TaskId = Text(gate.TaskId.Value),
        Type = GateTypeName(gate.Type),
        SemanticRevisionId = Text(gate.SemanticRevisionId.Value),
        ImplementationRevisionId = gate.ImplementationRevisionId is { } implementation ? Text(implementation.Value) : null,
        VerificationRunId = gate.VerificationRunId is { } verification ? Text(verification.Value) : null,
        OpenedAt = Timestamps.ToText(gate.OpenedAt),
        Clarification = gate.Clarification is { } clarification
            ? new ClarificationDto { Question = clarification.Question, Reason = clarification.Reason, Turn = clarification.Turn }
            : null,
        What = gate.What is { } what
            ? new WhatDto
            {
                Objective = what.Objective,
                RequiredBehavior = what.RequiredBehavior,
                Scope = what.Scope,
                Constraints = what.Constraints,
                NonGoals = what.NonGoals,
                AcceptanceConditions = what.AcceptanceConditions,
                Rationale = what.Rationale,
                Turn = what.Turn,
            }
            : null,
        How = gate.How is { } how
            ? new HowDto
            {
                Strategy = how.Strategy,
                Components = how.Components,
                DataAndControlFlow = how.DataAndControlFlow,
                Compatibility = how.Compatibility,
                RisksAndTradeoffs = how.RisksAndTradeoffs,
                VerificationApproach = how.VerificationApproach,
                Rationale = how.Rationale,
                Turn = how.Turn,
            }
            : null,
    };

    public static GateDecisionDto ToDto(GateDecision decision) => new()
    {
        GateId = Text(decision.GateId.Value),
        Action = GateActionName(decision.Action),
        Feedback = decision.Feedback,
        Answer = decision.Answer,
        DecidedAt = Timestamps.ToText(decision.DecidedAt),
    };

    public static string AcceptanceName(AcceptanceRequirement acceptance) => acceptance switch
    {
        AcceptanceRequirement.NotRequired => "not_required",
        AcceptanceRequirement.HumanRequired => "human_required",
        _ => throw new NotSupportedException($"The persistence codec has no name for acceptance requirement {acceptance}."),
    };

    public static AcceptanceRequirement ParseAcceptance(string name) => name switch
    {
        "not_required" => AcceptanceRequirement.NotRequired,
        "human_required" => AcceptanceRequirement.HumanRequired,
        _ => throw new FormatException($"Unknown acceptance requirement '{name}'."),
    };

    public static string GateTypeName(GateType type) => type switch
    {
        GateType.Accept => "accept",
        GateType.Clarify => "clarify",
        GateType.What => "what",
        GateType.How => "how",
        _ => throw new NotSupportedException($"The persistence codec has no name for gate type {type}."),
    };

    public static string GateActionName(GateAction action) => action switch
    {
        GateAction.Accept => "accept",
        GateAction.RequestChanges => "request_changes",
        GateAction.Cancel => "cancel",
        GateAction.Answer => "answer",
        GateAction.Approve => "approve",
        _ => throw new NotSupportedException($"The persistence codec has no name for gate action {action}."),
    };

    public static string SourceName(VerificationEvidenceSource source) => source switch
    {
        VerificationEvidenceSource.ControllerExecuted => "controller_executed",
        VerificationEvidenceSource.AgentObserved => "agent_observed",
        _ => throw new NotSupportedException($"The persistence codec has no name for verification evidence source {source}."),
    };

    public static string OutcomeName(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "passed",
        VerificationOutcome.Failed => "failed",
        _ => throw new NotSupportedException($"The persistence codec has no name for verification outcome {outcome}."),
    };

    public static string StateName(WorkflowState state) => state switch
    {
        WorkflowState.Running => "running",
        WorkflowState.AwaitingHuman => "awaiting_human",
        WorkflowState.Blocked => "blocked",
        WorkflowState.Completed => "completed",
        WorkflowState.Cancelled => "cancelled",
        WorkflowState.Failed => "failed",
        _ => throw new NotSupportedException($"The persistence codec has no name for workflow state {state}."),
    };

    public static string EffectName(PolicyEffect effect) => effect switch
    {
        PolicyEffect.Prevent => "prevent",
        PolicyEffect.Require => "require",
        PolicyEffect.Detect => "detect",
        PolicyEffect.Notify => "notify",
        _ => throw new NotSupportedException($"The persistence codec has no name for policy effect {effect}."),
    };

    // ---- DTO -> Core ----------------------------------------------------------------------------------------

    public static SemanticRevision ToDomain(SemanticRevisionDto dto) => new(
        new SemanticRevisionId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        dto.Ordinal,
        dto.ParentId is null ? null : new SemanticRevisionId(ParseGuid(dto.ParentId)),
        new SemanticChangeKind(dto.Kind),
        dto.Explanation,
        ParseInstant(dto.CreatedAt));

    public static Reclassification ToDomain(ReclassificationDto dto) => new(
        new ReclassificationId(ParseGuid(dto.Id)),
        dto.Stage switch
        {
            "what" => ReclassificationStage.What,
            "how" => ReclassificationStage.How,
            _ => throw new FormatException($"Unknown reclassification stage '{dto.Stage}'."),
        },
        dto.Evidence,
        dto.Reason,
        dto.Turn,
        dto.Session,
        new SemanticRevisionId(ParseGuid(dto.PriorSemanticRevisionId)),
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        new ImplementationFingerprint(dto.Workspace),
        dto.ProvisionalWork,
        dto.InvalidatedGates.Select(g => new GateId(ParseGuid(g))));

    public static ReviewRequest ToDomain(ReviewRequestDto dto) => new(
        new ReviewRequestId(ParseGuid(dto.Id)),
        dto.Reason,
        dto.Turn,
        dto.Session,
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)));

    public static Review ToDomain(ReviewDto dto) => new(
        new ReviewId(ParseGuid(dto.Id)),
        dto.Round,
        new ReviewReport(
            dto.Verdict switch
            {
                "pass" => ReviewVerdict.Pass,
                "changes_required" => ReviewVerdict.ChangesRequired,
                _ => throw new FormatException($"Unknown review verdict '{dto.Verdict}'."),
            },
            dto.Summary,
            dto.Findings.Select(f => new ReviewFinding(f.Title, f.Evidence, f.Impact, f.Recommendation, f.Location)),
            dto.Notes),
        dto.ReportHash,
        new ProfileId(dto.ReviewerProfile),
        dto.ReviewerSession,
        dto.ImplementerSession,
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        new VerificationRunId(ParseGuid(dto.VerificationRunId)),
        ParseInstant(dto.RecordedAt),
        dto.ReviewerMilliseconds,
        dto.ReviewerCommandCount,
        dto.ReviewerCommands,
        dto.ConfiguredCheckRerun);

    public static ProofContract ToDomain(ProofContractDto dto) => new(
        new ProofContractId(ParseGuid(dto.Id)),
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        dto.Turn,
        dto.Session,
        dto.Rationale,
        dto.Claims.Select(c => new ProofClaim(
            c.Id,
            c.Statement,
            c.Evidence.Select(e => ProofEvidenceRequirement.Parse(e.Kind, e.Subject)),
            c.AcceptanceCriterion)),
        dto.ContentHash);

    public static Proof ToDomain(ProofDto dto) => new(
        new ProofId(ParseGuid(dto.Id)),
        dto.Round,
        new ProofContractId(ParseGuid(dto.ContractId)),
        dto.ContractHash,
        new ProofReport(
            dto.Summary,
            dto.Claims.Select(c => new ProofClaimResult(
                c.ClaimId,
                ProofReport.ParseVerdict(c.Verdict),
                c.Evidence.Select(ProofEvidenceRef.Parse),
                c.Summary))),
        dto.ReportHash,
        new ProfileId(dto.ProverProfile),
        dto.ProverSession,
        dto.ImplementerSession,
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        new VerificationRunId(ParseGuid(dto.VerificationRunId)),
        dto.ReviewId is null ? null : new ReviewId(ParseGuid(dto.ReviewId)),
        ParseInstant(dto.RecordedAt),
        dto.ProverMilliseconds,
        dto.ProverCommandCount,
        dto.ProverCommands,
        dto.ConfiguredCheckRerun,
        dto.DuplicatedVerificationMilliseconds);

    public static ImplementationRevision ToDomain(ImplementationRevisionDto dto) => new(
        new ImplementationRevisionId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        new ImplementationFingerprint(dto.Fingerprint),
        dto.ParentId is null ? null : new ImplementationRevisionId(ParseGuid(dto.ParentId)),
        ParseInstant(dto.CreatedAt));

    public static RoleBinding ToDomain(RoleBindingDto dto) => new(
        new Role(
            new RoleId(dto.Role.Id),
            Set(dto.Role.RequiredCapabilities, value => new ProfileCapability(value))),
        new Profile(
            new ProfileId(dto.Profile.Id),
            Set(dto.Profile.Capabilities, value => new ProfileCapability(value)),
            dto.Profile.Description),
        new SemanticRevisionId(ParseGuid(dto.BoundAt)),
        dto.Execution is { } execution ? new ExecutionConfiguration(execution.Harness, execution.Model, execution.Reasoning, execution.Fingerprint) : null);

    public static AuthorityEnvelope ToDomain(AuthorityEnvelopeDto dto) => new(
        Set(dto.AllowedWithoutHuman, value => new ActionId(value)),
        Set(dto.ReservedForHuman, value => new ActionId(value)));

    public static PolicyDecision ToDomain(PolicyDecisionDto dto) => new(
        new TaskId(ParseGuid(dto.TaskId)),
        new PolicyRuleId(dto.Rule),
        ParseEffect(dto.Effect),
        new PolicyTrigger(
            dto.Trigger.Description,
            dto.Trigger.Action is null ? null : new ActionId(dto.Trigger.Action),
            dto.Trigger.Reference),
        dto.Explanation,
        ParseInstant(dto.DecidedAt),
        dto.SemanticRevisionId is null ? null : new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        dto.ImplementationRevisionId is null ? null : new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)));

    /// <summary>Version 1: no provenance was stored, so every check was executed by the controller.</summary>
    public static VerificationRun ToDomain(VerificationRunDto dto) => new(
        new VerificationRunId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        ParseOutcome(dto.Outcome),
        dto.Checks.Select(check => new VerificationCheckResult(
            new VerificationCheckId(check.CheckId),
            ParseOutcome(check.Outcome),
            check.Summary,
            check.ExitCode)).ToList(),
        ParseInstant(dto.Timestamp));

    public static VerificationRun ToDomain(VerificationRunV2Dto dto) => new(
        new VerificationRunId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        ParseOutcome(dto.Outcome),
        dto.Checks.Select(check => ToDomain(check.CheckId, check.Outcome, check.Summary, check.ExitCode, check.Source, check.Observation, evidenceReason: null)).ToList(),
        ParseInstant(dto.Timestamp));

    /// <summary>Version 3: version 2 plus the recorded reason for reusing or executing each check.</summary>
    public static VerificationRun ToDomain(VerificationRunV3Dto dto) => new(
        new VerificationRunId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        ParseOutcome(dto.Outcome),
        dto.Checks.Select(check => ToDomain(check.CheckId, check.Outcome, check.Summary, check.ExitCode, check.Source, check.Observation, check.EvidenceReason)).ToList(),
        ParseInstant(dto.Timestamp));

    private static VerificationCheckResult ToDomain(
        string checkId,
        string outcome,
        string summary,
        int? exitCode,
        string sourceName,
        AgentObservationDto? observationDto,
        string? evidenceReason)
    {
        var source = sourceName switch
        {
            "controller_executed" => VerificationEvidenceSource.ControllerExecuted,
            "agent_observed" => VerificationEvidenceSource.AgentObserved,
            _ => throw new FormatException($"Unknown verification evidence source '{sourceName}'."),
        };

        // Provenance is never implicit: the declared source and the presence of an observation must agree.
        if ((source == VerificationEvidenceSource.AgentObserved) != (observationDto is not null))
            throw new FormatException($"Check '{checkId}' declares source '{sourceName}' but {(observationDto is null ? "has no" : "carries an")} observation.");

        var observation = observationDto is { } dto
            ? new AgentObservation(dto.Runtime, dto.ToolUseId, dto.CommandIdentity, dto.DurationMs, new ImplementationFingerprint(dto.StateFingerprint))
            : null;

        return new VerificationCheckResult(
            new VerificationCheckId(checkId),
            ParseOutcome(outcome),
            summary,
            exitCode,
            observation,
            evidenceReason);
    }

    public static Gate ToDomain(GateDto dto) => new(
        new GateId(ParseGuid(dto.Id)),
        new TaskId(ParseGuid(dto.TaskId)),
        ParseGateType(dto.Type),
        new SemanticRevisionId(ParseGuid(dto.SemanticRevisionId)),
        dto.ImplementationRevisionId is null ? null : new ImplementationRevisionId(ParseGuid(dto.ImplementationRevisionId)),
        dto.VerificationRunId is null ? null : new VerificationRunId(ParseGuid(dto.VerificationRunId)),
        ParseInstant(dto.OpenedAt),
        dto.Clarification is null ? null : new ClarificationRequest(dto.Clarification.Question, dto.Clarification.Reason, dto.Clarification.Turn),
        dto.What is null
            ? null
            : new WhatProposal(
                dto.What.Objective,
                dto.What.RequiredBehavior,
                dto.What.Scope,
                dto.What.Constraints,
                dto.What.NonGoals,
                dto.What.AcceptanceConditions,
                dto.What.Rationale,
                dto.What.Turn),
        dto.How is null
            ? null
            : new HowProposal(
                dto.How.Strategy,
                dto.How.Components,
                dto.How.DataAndControlFlow,
                dto.How.Compatibility,
                dto.How.RisksAndTradeoffs,
                dto.How.VerificationApproach,
                dto.How.Rationale,
                dto.How.Turn));

    public static GateDecision ToDomain(GateDecisionDto dto) => new(
        new GateId(ParseGuid(dto.GateId)),
        ParseGateAction(dto.Action),
        dto.Feedback,
        ParseInstant(dto.DecidedAt),
        dto.Answer);

    public static GateType ParseGateType(string name) => name switch
    {
        "accept" => GateType.Accept,
        "clarify" => GateType.Clarify,
        "what" => GateType.What,
        "how" => GateType.How,
        _ => throw new FormatException($"Unknown gate type '{name}'."),
    };

    public static GateAction ParseGateAction(string name) => name switch
    {
        "accept" => GateAction.Accept,
        "request_changes" => GateAction.RequestChanges,
        "cancel" => GateAction.Cancel,
        "answer" => GateAction.Answer,
        "approve" => GateAction.Approve,
        _ => throw new FormatException($"Unknown gate action '{name}'."),
    };

    public static VerificationOutcome ParseOutcome(string name) => name switch
    {
        "passed" => VerificationOutcome.Passed,
        "failed" => VerificationOutcome.Failed,
        _ => throw new FormatException($"Unknown verification outcome '{name}'."),
    };

    public static WorkflowState ParseState(string name) => name switch
    {
        "running" => WorkflowState.Running,
        "awaiting_human" => WorkflowState.AwaitingHuman,
        "blocked" => WorkflowState.Blocked,
        "completed" => WorkflowState.Completed,
        "cancelled" => WorkflowState.Cancelled,
        "failed" => WorkflowState.Failed,
        _ => throw new FormatException($"Unknown workflow state '{name}'."),
    };

    public static PolicyEffect ParseEffect(string name) => name switch
    {
        "prevent" => PolicyEffect.Prevent,
        "require" => PolicyEffect.Require,
        "detect" => PolicyEffect.Detect,
        "notify" => PolicyEffect.Notify,
        _ => throw new FormatException($"Unknown policy effect '{name}'."),
    };

    public static Guid ParseGuid(string text)
        => Guid.TryParseExact(text, "D", out var value)
            ? value
            : throw new FormatException($"'{text}' is not a valid identifier.");

    public static DateTimeOffset ParseInstant(string text)
        => Timestamps.TryParse(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a canonical UTC timestamp.");

    // ---- helpers --------------------------------------------------------------------------------------------

    public static string Text(Guid value) => value.ToString("D");

    private static string[] Values<T>(IdentifierSet<T> set)
        where T : StableIdentifier
        => set.Select(item => item.Value).ToArray();

    private static IdentifierSet<T> Set<T>(IReadOnlyList<string> values, Func<string, T> create)
        where T : StableIdentifier
    {
        var items = values.Select(create).ToList();
        var set = IdentifierSet<T>.Of(items);

        // The writer never emits duplicates, so a repeated entry means the payload was not written by us.
        if (set.Count != items.Count)
            throw new FormatException("A stored identifier set contains a duplicate entry.");

        return set;
    }
}
