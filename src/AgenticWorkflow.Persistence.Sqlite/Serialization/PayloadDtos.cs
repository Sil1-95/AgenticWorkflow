using System.Text.Json.Serialization;

namespace AgenticWorkflow.Persistence.Sqlite.Serialization;

// Version-1 payload shapes. These are the persistence contract, independent of the Core types. Ids are GUID
// strings, instants use Timestamps, enums use explicit stable names. `required` members must be present;
// optional ones are nullable. Unknown members are rejected when reading (see EventCodec).
// To change a shape, add a new version of the DTO and a new decoder; never edit a published version in place.

internal sealed class SemanticRevisionDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("ordinal")]
    public required int Ordinal { get; init; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }
}

internal sealed class ImplementationRevisionDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }
}

internal sealed class RoleDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("required_capabilities")]
    public required IReadOnlyList<string> RequiredCapabilities { get; init; }
}

internal sealed class ProfileDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

internal sealed class RoleBindingDto
{
    [JsonPropertyName("role")]
    public required RoleDto Role { get; init; }

    [JsonPropertyName("profile")]
    public required ProfileDto Profile { get; init; }

    [JsonPropertyName("bound_at")]
    public required string BoundAt { get; init; }

    /// <summary>How the profile is launched. Omitted when the binding has none, which is how every binding recorded before execution configuration existed reads.</summary>
    [JsonPropertyName("execution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionConfigurationDto? Execution { get; init; }
}

internal sealed class ExecutionConfigurationDto
{
    [JsonPropertyName("harness")]
    public required string Harness { get; init; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reasoning { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }
}

internal sealed class AuthorityEnvelopeDto
{
    [JsonPropertyName("allowed_without_human")]
    public required IReadOnlyList<string> AllowedWithoutHuman { get; init; }

    [JsonPropertyName("reserved_for_human")]
    public required IReadOnlyList<string> ReservedForHuman { get; init; }
}

internal sealed class PolicyTriggerDto
{
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("reference")]
    public string? Reference { get; init; }
}

internal sealed class PolicyDecisionDto
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("effect")]
    public required string Effect { get; init; }

    [JsonPropertyName("trigger")]
    public required PolicyTriggerDto Trigger { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }

    [JsonPropertyName("decided_at")]
    public required string DecidedAt { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public string? SemanticRevisionId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public string? ImplementationRevisionId { get; init; }
}

internal sealed class TaskCreatedV1
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }
}

/// <summary>
/// <c>task_created</c> at payload version 2: version 1 plus the task's acceptance requirement. Version 1 rows have no such member and are read as
/// the human-required semantics every task had before it existed.
/// </summary>
internal sealed class TaskCreatedV2
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }

    [JsonPropertyName("acceptance")]
    public required string Acceptance { get; init; }
}

/// <summary>
/// <c>task_created</c> at payload version 3: version 2 plus the human's per-run requirement that the WHAT be approved before implementation. It is written
/// only for a task that carries that requirement; every other task is still written as version 2, byte for byte as before.
/// </summary>
internal sealed class TaskCreatedV3
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }

    [JsonPropertyName("acceptance")]
    public required string Acceptance { get; init; }

    [JsonPropertyName("what_required")]
    public required bool WhatRequired { get; init; }
}

/// <summary>
/// <c>task_created</c> at payload version 4: version 3 plus the human's per-run requirement that the HOW (implementation strategy) be approved before
/// implementation. It is written only for a task that carries that requirement; every other task keeps its version-2 or version-3 payload byte for byte.
/// </summary>
internal sealed class TaskCreatedV4
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }

    [JsonPropertyName("acceptance")]
    public required string Acceptance { get; init; }

    [JsonPropertyName("what_required")]
    public required bool WhatRequired { get; init; }

    [JsonPropertyName("how_required")]
    public required bool HowRequired { get; init; }
}

internal sealed class WorkflowStateChangedV1
{
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

internal sealed class SemanticRevisionCreatedV1
{
    [JsonPropertyName("revision")]
    public required SemanticRevisionDto Revision { get; init; }
}

internal sealed class ImplementationRevisionRecordedV1
{
    [JsonPropertyName("revision")]
    public required ImplementationRevisionDto Revision { get; init; }
}

internal sealed class RoleBoundV1
{
    [JsonPropertyName("binding")]
    public required RoleBindingDto Binding { get; init; }
}

internal sealed class AuthorityEnvelopeChangedV1
{
    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("envelope")]
    public required AuthorityEnvelopeDto Envelope { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

internal sealed class PolicyDecisionRecordedV1
{
    [JsonPropertyName("decision")]
    public required PolicyDecisionDto Decision { get; init; }
}

internal sealed class VerificationCheckResultDto
{
    [JsonPropertyName("check_id")]
    public required string CheckId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }
}

internal sealed class VerificationRunDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public required string ImplementationRevisionId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<VerificationCheckResultDto> Checks { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }
}

internal sealed class VerificationRecordedV1
{
    [JsonPropertyName("run")]
    public required VerificationRunDto Run { get; init; }
}

// ---- verification_recorded, payload version 2 ------------------------------------------------------------------
// Adds per-check provenance. Version 1 (above) is untouched and still decodes: every v1 check is ControllerExecuted.

internal sealed class AgentObservationDto
{
    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("command_identity")]
    public required string CommandIdentity { get; init; }

    [JsonPropertyName("duration_ms")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("state_fingerprint")]
    public required string StateFingerprint { get; init; }
}

internal sealed class VerificationCheckResultV2Dto
{
    [JsonPropertyName("check_id")]
    public required string CheckId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    /// <summary><c>controller_executed</c> or <c>agent_observed</c>; required so that provenance is never implicit.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Present exactly when <see cref="Source"/> is <c>agent_observed</c>.</summary>
    [JsonPropertyName("observation")]
    public AgentObservationDto? Observation { get; init; }
}

internal sealed class VerificationRunV2Dto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public required string ImplementationRevisionId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<VerificationCheckResultV2Dto> Checks { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }
}

internal sealed class VerificationRecordedV2
{
    [JsonPropertyName("run")]
    public required VerificationRunV2Dto Run { get; init; }
}

// ---- verification_recorded, payload version 3 (verification observability) ---------------------------------------------
// Adds the bounded reason the controller reused the runtime's record of a check or ran it itself. Versions 1 and 2 are untouched and still decode.

internal sealed class VerificationCheckResultV3Dto
{
    [JsonPropertyName("check_id")]
    public required string CheckId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    /// <summary><c>controller_executed</c> or <c>agent_observed</c>; required so that provenance is never implicit.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Present exactly when <see cref="Source"/> is <c>agent_observed</c>.</summary>
    [JsonPropertyName("observation")]
    public AgentObservationDto? Observation { get; init; }

    /// <summary>Why the check was reused or executed (a stable code with bounded detail); absent for checks that are not subject to reuse.</summary>
    [JsonPropertyName("evidence_reason")]
    public string? EvidenceReason { get; init; }
}

internal sealed class VerificationRunV3Dto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public required string ImplementationRevisionId { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("checks")]
    public required IReadOnlyList<VerificationCheckResultV3Dto> Checks { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }
}

internal sealed class VerificationRecordedV3
{
    [JsonPropertyName("run")]
    public required VerificationRunV3Dto Run { get; init; }
}

// ---- gate_opened / gate_decided, payload version 1 --------------------------------------------------------------

internal sealed class GateDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    /// <summary>Stable name of the gate type (<c>accept</c>, <c>clarify</c>, <c>what</c> or <c>how</c>).</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    /// <summary>Present for an <c>accept</c> gate; absent (null) for a <c>clarify</c> gate, which binds no implementation.</summary>
    [JsonPropertyName("implementation_revision_id")]
    public string? ImplementationRevisionId { get; init; }

    /// <summary>Present for an <c>accept</c> gate; absent (null) for a <c>clarify</c> gate.</summary>
    [JsonPropertyName("verification_run_id")]
    public string? VerificationRunId { get; init; }

    [JsonPropertyName("opened_at")]
    public required string OpenedAt { get; init; }

    /// <summary>Present exactly for a <c>clarify</c> gate.</summary>
    [JsonPropertyName("clarification")]
    public ClarificationDto? Clarification { get; init; }

    /// <summary>Present exactly for a <c>what</c> gate.</summary>
    [JsonPropertyName("what")]
    public WhatDto? What { get; init; }

    /// <summary>Present exactly for a <c>how</c> gate.</summary>
    [JsonPropertyName("how")]
    public HowDto? How { get; init; }
}

internal sealed class HowDto
{
    [JsonPropertyName("strategy")]
    public required string Strategy { get; init; }

    [JsonPropertyName("components")]
    public required string Components { get; init; }

    [JsonPropertyName("data_and_control_flow")]
    public string? DataAndControlFlow { get; init; }

    [JsonPropertyName("compatibility")]
    public string? Compatibility { get; init; }

    [JsonPropertyName("risks_and_tradeoffs")]
    public required string RisksAndTradeoffs { get; init; }

    [JsonPropertyName("verification_approach")]
    public string? VerificationApproach { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }
}

internal sealed class WhatDto
{
    [JsonPropertyName("objective")]
    public required string Objective { get; init; }

    [JsonPropertyName("required_behavior")]
    public required string RequiredBehavior { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("constraints")]
    public string? Constraints { get; init; }

    [JsonPropertyName("non_goals")]
    public string? NonGoals { get; init; }

    [JsonPropertyName("acceptance_conditions")]
    public required string AcceptanceConditions { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }
}

internal sealed class ClarificationDto
{
    [JsonPropertyName("question")]
    public required string Question { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }
}

internal sealed class GateOpenedV1
{
    [JsonPropertyName("gate")]
    public required GateDto Gate { get; init; }
}

internal sealed class GateDecisionDto
{
    [JsonPropertyName("gate_id")]
    public required string GateId { get; init; }

    /// <summary>Stable name of the action: <c>accept</c>, <c>request_changes</c>, <c>cancel</c>, <c>answer</c> or <c>approve</c>.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>Present exactly for <c>request_changes</c>; bounded by Core.</summary>
    [JsonPropertyName("feedback")]
    public string? Feedback { get; init; }

    /// <summary>Present exactly for <c>answer</c>; bounded by Core.</summary>
    [JsonPropertyName("answer")]
    public string? Answer { get; init; }

    [JsonPropertyName("decided_at")]
    public required string DecidedAt { get; init; }
}

internal sealed class GateDecidedV1
{
    [JsonPropertyName("decision")]
    public required GateDecisionDto Decision { get; init; }
}

// ---- reclassification_recorded, payload version 1 -------------------------------------------------------------------

internal sealed class ReclassificationDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }

    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("prior_semantic_revision_id")]
    public required string PriorSemanticRevisionId { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("workspace")]
    public required string Workspace { get; init; }

    [JsonPropertyName("provisional_work")]
    public required bool ProvisionalWork { get; init; }

    [JsonPropertyName("invalidated_gates")]
    public required List<string> InvalidatedGates { get; init; }
}

internal sealed class ReclassificationRecordedV1
{
    [JsonPropertyName("reclassification")]
    public required ReclassificationDto Reclassification { get; init; }

    [JsonPropertyName("revision")]
    public required SemanticRevisionDto Revision { get; init; }
}

/// <summary>
/// <c>task_created</c> at payload version 5: version 4 plus the human's per-run requirement that the verified implementation be independently reviewed. It is
/// written only for a task that carries that requirement; every other task keeps its earlier payload byte for byte.
/// </summary>
internal sealed class TaskCreatedV5
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }

    [JsonPropertyName("acceptance")]
    public required string Acceptance { get; init; }

    [JsonPropertyName("what_required")]
    public required bool WhatRequired { get; init; }

    [JsonPropertyName("how_required")]
    public required bool HowRequired { get; init; }

    [JsonPropertyName("review_required")]
    public required bool ReviewRequired { get; init; }
}

// ---- review_requested / review_recorded, payload version 1 ------------------------------------------------------------

internal sealed class ReviewRequestDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }

    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }
}

internal sealed class ReviewRequestedV1
{
    [JsonPropertyName("request")]
    public required ReviewRequestDto Request { get; init; }
}

internal sealed class ReviewFindingDto
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("evidence")]
    public required string Evidence { get; init; }

    [JsonPropertyName("impact")]
    public required string Impact { get; init; }

    [JsonPropertyName("recommendation")]
    public required string Recommendation { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }
}

internal sealed class ReviewDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("round")]
    public required int Round { get; init; }

    /// <summary>"pass" or "changes_required".</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("findings")]
    public required List<ReviewFindingDto> Findings { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("report_hash")]
    public required string ReportHash { get; init; }

    [JsonPropertyName("reviewer_profile")]
    public required string ReviewerProfile { get; init; }

    [JsonPropertyName("reviewer_session")]
    public required string ReviewerSession { get; init; }

    [JsonPropertyName("implementer_session")]
    public required string ImplementerSession { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public required string ImplementationRevisionId { get; init; }

    [JsonPropertyName("verification_run_id")]
    public required string VerificationRunId { get; init; }

    [JsonPropertyName("recorded_at")]
    public required string RecordedAt { get; init; }

    [JsonPropertyName("reviewer_milliseconds")]
    public required long ReviewerMilliseconds { get; init; }

    [JsonPropertyName("reviewer_command_count")]
    public required int ReviewerCommandCount { get; init; }

    [JsonPropertyName("reviewer_commands")]
    public required List<string> ReviewerCommands { get; init; }

    [JsonPropertyName("configured_check_rerun")]
    public required bool ConfiguredCheckRerun { get; init; }
}

internal sealed class ReviewRecordedV1
{
    [JsonPropertyName("review")]
    public required ReviewDto Review { get; init; }
}

/// <summary>
/// <c>task_created</c> at payload version 6: version 5 plus the human's per-run requirement that the work be formally proved. It is written only for a task that
/// carries that requirement; every other task keeps its earlier payload byte for byte.
/// </summary>
internal sealed class TaskCreatedV6
{
    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("completion_boundary")]
    public required string CompletionBoundary { get; init; }

    [JsonPropertyName("initial_semantic_revision")]
    public required SemanticRevisionDto InitialSemanticRevision { get; init; }

    [JsonPropertyName("acceptance")]
    public required string Acceptance { get; init; }

    [JsonPropertyName("what_required")]
    public required bool WhatRequired { get; init; }

    [JsonPropertyName("how_required")]
    public required bool HowRequired { get; init; }

    [JsonPropertyName("review_required")]
    public required bool ReviewRequired { get; init; }

    [JsonPropertyName("prove_required")]
    public required bool ProveRequired { get; init; }
}

// ---- proof_contract_recorded / proof_recorded, payload version 1 ------------------------------------------------------

internal sealed class ProofEvidenceRequirementDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }
}

internal sealed class ProofClaimDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    [JsonPropertyName("evidence")]
    public required List<ProofEvidenceRequirementDto> Evidence { get; init; }

    [JsonPropertyName("acceptance_criterion")]
    public required string AcceptanceCriterion { get; init; }
}

internal sealed class ProofContractDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("turn")]
    public required int Turn { get; init; }

    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("rationale")]
    public required string Rationale { get; init; }

    [JsonPropertyName("claims")]
    public required List<ProofClaimDto> Claims { get; init; }

    [JsonPropertyName("content_hash")]
    public required string ContentHash { get; init; }
}

internal sealed class ProofContractRecordedV1
{
    [JsonPropertyName("contract")]
    public required ProofContractDto Contract { get; init; }
}

internal sealed class ProofClaimResultDto
{
    [JsonPropertyName("claim_id")]
    public required string ClaimId { get; init; }

    /// <summary>"pass", "fail" or "inconclusive".</summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("evidence")]
    public required List<string> Evidence { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }
}

internal sealed class ProofDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("round")]
    public required int Round { get; init; }

    [JsonPropertyName("contract_id")]
    public required string ContractId { get; init; }

    [JsonPropertyName("contract_hash")]
    public required string ContractHash { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("claims")]
    public required List<ProofClaimResultDto> Claims { get; init; }

    [JsonPropertyName("report_hash")]
    public required string ReportHash { get; init; }

    [JsonPropertyName("prover_profile")]
    public required string ProverProfile { get; init; }

    [JsonPropertyName("prover_session")]
    public required string ProverSession { get; init; }

    [JsonPropertyName("implementer_session")]
    public required string ImplementerSession { get; init; }

    [JsonPropertyName("semantic_revision_id")]
    public required string SemanticRevisionId { get; init; }

    [JsonPropertyName("implementation_revision_id")]
    public required string ImplementationRevisionId { get; init; }

    [JsonPropertyName("verification_run_id")]
    public required string VerificationRunId { get; init; }

    [JsonPropertyName("review_id")]
    public string? ReviewId { get; init; }

    [JsonPropertyName("recorded_at")]
    public required string RecordedAt { get; init; }

    [JsonPropertyName("prover_milliseconds")]
    public required long ProverMilliseconds { get; init; }

    [JsonPropertyName("prover_command_count")]
    public required int ProverCommandCount { get; init; }

    [JsonPropertyName("prover_commands")]
    public required List<string> ProverCommands { get; init; }

    [JsonPropertyName("configured_check_rerun")]
    public required bool ConfiguredCheckRerun { get; init; }

    [JsonPropertyName("duplicated_verification_milliseconds")]
    public required long DuplicatedVerificationMilliseconds { get; init; }
}

internal sealed class ProofRecordedV1
{
    [JsonPropertyName("proof")]
    public required ProofDto Proof { get; init; }
}
