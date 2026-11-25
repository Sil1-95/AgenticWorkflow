using System.Text.Encodings.Web;
using System.Text.Json;
using AgenticWorkflow.Core.Events;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Persistence.Sqlite.Serialization;

/// <summary>A serialized event payload together with the two values that make it self-describing on disk.</summary>
internal readonly record struct EncodedPayload(string Kind, int SchemaVersion, string Json);

/// <summary>
/// The persistence contract for Core domain events: <c>(event_kind, payload_schema_version, payload_json)</c> in
/// both directions. Every Core event type is handled explicitly; an event type or stored kind/version this codec
/// does not know fails loudly and is never skipped or approximated.
/// <para>
/// To evolve a payload: leave the existing version's DTO and decoder untouched, add a new DTO + decoder, and
/// write the new version for new events. Old rows keep decoding through the old version.
/// </para>
/// </summary>
internal static class EventCodec
{
    /// <summary>The payload schema version written by this build for every event kind except <c>task_created</c> and <c>verification_recorded</c>.</summary>
    public const int CurrentPayloadVersion = 1;

    /// <summary>
    /// <c>task_created</c> is written at version 2 (the acceptance requirement). Version 1 rows still decode, as
    /// <see cref="Core.Gates.AcceptanceRequirement.HumanRequired"/>: every task created before the requirement existed completed through the human's ACCEPT
    /// (or, before the ACCEPT gate existed, by a plain recorded COMPLETED, which Core still replays).
    /// </summary>
    public const int TaskCreatedPayloadVersion = 2;

    /// <summary>
    /// <c>task_created</c> is written at version 3 only when it must carry something version 2 cannot: the human's requirement that the WHAT be approved
    /// before implementation. Every other new task keeps its version-2 payload exactly.
    /// </summary>
    public const int TaskCreatedWhatPayloadVersion = 3;

    /// <summary>
    /// <c>task_created</c> is written at version 4 only when the human also required the HOW (implementation strategy) approval, which version 3 cannot
    /// carry. A task with no HOW requirement keeps its version-2 or version-3 payload exactly.
    /// </summary>
    public const int TaskCreatedHowPayloadVersion = 4;

    /// <summary>
    /// <c>task_created</c> is written at version 5 only when the human also required an independent REVIEW, which version 4 cannot carry. A task with no
    /// REVIEW requirement keeps its version-2, -3 or -4 payload exactly.
    /// </summary>
    public const int TaskCreatedReviewPayloadVersion = 5;

    /// <summary>
    /// <c>task_created</c> is written at version 6 only when the human also required formal PROVE, which version 5 cannot carry. A task with no PROVE requirement keeps its
    /// earlier payload exactly.
    /// </summary>
    public const int TaskCreatedProvePayloadVersion = 6;

    /// <summary>
    /// <c>verification_recorded</c> is written at version 3 (the recorded reason for reusing or executing each check). Version 2 (per-check
    /// provenance) and version 1 rows still decode, as controller-executed checks without a recorded reason. Every other kind has only version 1.
    /// </summary>
    public const int VerificationPayloadVersion = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyCollection<string> SupportedKinds { get; } =
    [
        EventKinds.TaskCreated,
        EventKinds.WorkflowStateChanged,
        EventKinds.SemanticRevisionCreated,
        EventKinds.ImplementationRevisionRecorded,
        EventKinds.RoleBound,
        EventKinds.AuthorityEnvelopeChanged,
        EventKinds.PolicyDecisionRecorded,
        EventKinds.VerificationRecorded,
        EventKinds.GateOpened,
        EventKinds.GateDecided,
        EventKinds.ReclassificationRecorded,
        EventKinds.ReviewRequested,
        EventKinds.ReviewRecorded,
        EventKinds.ProofContractRecorded,
        EventKinds.ProofRecorded,
    ];

    public static EncodedPayload Encode(DomainEvent payload) => payload switch
    {
        // a legacy task is written back exactly as it was recorded (version 1, no acceptance member), so its marker survives a round trip
        TaskCreated { PredatesAcceptanceRequirement: true } e => Write(EventKinds.TaskCreated, 1, new TaskCreatedV1
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
        }),
        TaskCreated { ProveRequired: true } e => Write(EventKinds.TaskCreated, TaskCreatedProvePayloadVersion, new TaskCreatedV6
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
            Acceptance = PayloadMapper.AcceptanceName(e.Acceptance),
            WhatRequired = e.WhatRequired,
            HowRequired = e.HowRequired,
            ReviewRequired = e.ReviewRequired,
            ProveRequired = true,
        }),
        TaskCreated { ReviewRequired: true } e => Write(EventKinds.TaskCreated, TaskCreatedReviewPayloadVersion, new TaskCreatedV5
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
            Acceptance = PayloadMapper.AcceptanceName(e.Acceptance),
            WhatRequired = e.WhatRequired,
            HowRequired = e.HowRequired,
            ReviewRequired = true,
        }),
        TaskCreated { HowRequired: true } e => Write(EventKinds.TaskCreated, TaskCreatedHowPayloadVersion, new TaskCreatedV4
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
            Acceptance = PayloadMapper.AcceptanceName(e.Acceptance),
            WhatRequired = e.WhatRequired,
            HowRequired = true,
        }),
        TaskCreated { WhatRequired: true } e => Write(EventKinds.TaskCreated, TaskCreatedWhatPayloadVersion, new TaskCreatedV3
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
            Acceptance = PayloadMapper.AcceptanceName(e.Acceptance),
            WhatRequired = true,
        }),
        TaskCreated e => Write(EventKinds.TaskCreated, TaskCreatedPayloadVersion, new TaskCreatedV2
        {
            Intent = e.Intent,
            CompletionBoundary = e.CompletionBoundary.Value,
            InitialSemanticRevision = PayloadMapper.ToDto(e.InitialSemanticRevision),
            Acceptance = PayloadMapper.AcceptanceName(e.Acceptance),
        }),
        WorkflowStateChanged e => Write(EventKinds.WorkflowStateChanged, new WorkflowStateChangedV1
        {
            From = PayloadMapper.StateName(e.From),
            To = PayloadMapper.StateName(e.To),
            Reason = e.Reason,
        }),
        SemanticRevisionCreated e => Write(EventKinds.SemanticRevisionCreated, new SemanticRevisionCreatedV1
        {
            Revision = PayloadMapper.ToDto(e.Revision),
        }),
        ImplementationRevisionRecorded e => Write(EventKinds.ImplementationRevisionRecorded, new ImplementationRevisionRecordedV1
        {
            Revision = PayloadMapper.ToDto(e.Revision),
        }),
        RoleBound e => Write(EventKinds.RoleBound, new RoleBoundV1
        {
            Binding = PayloadMapper.ToDto(e.Binding),
        }),
        AuthorityEnvelopeChanged e => Write(EventKinds.AuthorityEnvelopeChanged, new AuthorityEnvelopeChangedV1
        {
            SemanticRevisionId = PayloadMapper.Text(e.SemanticRevisionId.Value),
            Envelope = PayloadMapper.ToDto(e.Envelope),
            Reason = e.Reason,
        }),
        PolicyDecisionRecorded e => Write(EventKinds.PolicyDecisionRecorded, new PolicyDecisionRecordedV1
        {
            Decision = PayloadMapper.ToDto(e.Decision),
        }),
        VerificationRecorded e => Write(EventKinds.VerificationRecorded, VerificationPayloadVersion, new VerificationRecordedV3
        {
            Run = PayloadMapper.ToDto(e.Run),
        }),
        GateOpened e => Write(EventKinds.GateOpened, new GateOpenedV1
        {
            Gate = PayloadMapper.ToDto(e.Gate),
        }),
        GateDecided e => Write(EventKinds.GateDecided, new GateDecidedV1
        {
            Decision = PayloadMapper.ToDto(e.Decision),
        }),
        ReclassificationRecorded e => Write(EventKinds.ReclassificationRecorded, new ReclassificationRecordedV1
        {
            Reclassification = PayloadMapper.ToDto(e.Reclassification),
            Revision = PayloadMapper.ToDto(e.Revision),
        }),
        ReviewRequested e => Write(EventKinds.ReviewRequested, new ReviewRequestedV1
        {
            Request = PayloadMapper.ToDto(e.Request),
        }),
        ProofContractRecorded e => Write(EventKinds.ProofContractRecorded, new ProofContractRecordedV1
        {
            Contract = PayloadMapper.ToDto(e.Contract),
        }),
        ProofRecorded e => Write(EventKinds.ProofRecorded, new ProofRecordedV1
        {
            Proof = PayloadMapper.ToDto(e.Proof),
        }),
        ReviewRecorded e => Write(EventKinds.ReviewRecorded, new ReviewRecordedV1
        {
            Review = PayloadMapper.ToDto(e.Review),
        }),
        _ => throw new NotSupportedException(
            $"The persistence codec does not support event type '{payload.GetType().Name}'. Add it to EventCodec before persisting it."),
    };

    /// <summary>
    /// Decodes a stored payload. <paramref name="taskId"/> and <paramref name="sequence"/> only label errors.
    /// Anything unreadable raises <see cref="EventStoreCorruptionException"/>.
    /// </summary>
    public static DomainEvent Decode(TaskId taskId, long sequence, string kind, int schemaVersion, string json)
    {
        if (!SupportedKinds.Contains(kind))
            throw new EventStoreCorruptionException($"Unknown event kind '{kind}'.", taskId, sequence);

        var supported = SupportedVersions(kind);
        if (!supported.Contains(schemaVersion))
            throw new EventStoreCorruptionException(
                $"Unsupported payload schema version {schemaVersion} for event kind '{kind}' (this build reads version {string.Join(" and ", supported)}).",
                taskId,
                sequence);

        try
        {
            return kind switch
            {
                EventKinds.TaskCreated when schemaVersion == 1 => Read<TaskCreatedV1>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    AcceptanceRequirement.HumanRequired,
                    PredatesAcceptanceRequirement: true)),
                EventKinds.TaskCreated when schemaVersion == TaskCreatedProvePayloadVersion => Read<TaskCreatedV6>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    PayloadMapper.ParseAcceptance(dto.Acceptance),
                    WhatRequired: dto.WhatRequired,
                    HowRequired: dto.HowRequired,
                    ReviewRequired: dto.ReviewRequired,
                    ProveRequired: dto.ProveRequired)),
                EventKinds.TaskCreated when schemaVersion == TaskCreatedReviewPayloadVersion => Read<TaskCreatedV5>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    PayloadMapper.ParseAcceptance(dto.Acceptance),
                    WhatRequired: dto.WhatRequired,
                    HowRequired: dto.HowRequired,
                    ReviewRequired: dto.ReviewRequired)),
                EventKinds.TaskCreated when schemaVersion == TaskCreatedHowPayloadVersion => Read<TaskCreatedV4>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    PayloadMapper.ParseAcceptance(dto.Acceptance),
                    WhatRequired: dto.WhatRequired,
                    HowRequired: dto.HowRequired)),
                EventKinds.TaskCreated when schemaVersion == TaskCreatedWhatPayloadVersion => Read<TaskCreatedV3>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    PayloadMapper.ParseAcceptance(dto.Acceptance),
                    WhatRequired: dto.WhatRequired)),
                EventKinds.TaskCreated => Read<TaskCreatedV2>(json, dto => new TaskCreated(
                    dto.Intent,
                    new(dto.CompletionBoundary),
                    PayloadMapper.ToDomain(dto.InitialSemanticRevision),
                    PayloadMapper.ParseAcceptance(dto.Acceptance))),
                EventKinds.WorkflowStateChanged => Read<WorkflowStateChangedV1>(json, dto => new WorkflowStateChanged(
                    PayloadMapper.ParseState(dto.From),
                    PayloadMapper.ParseState(dto.To),
                    dto.Reason)),
                EventKinds.SemanticRevisionCreated => Read<SemanticRevisionCreatedV1>(json, dto =>
                    new SemanticRevisionCreated(PayloadMapper.ToDomain(dto.Revision))),
                EventKinds.ImplementationRevisionRecorded => Read<ImplementationRevisionRecordedV1>(json, dto =>
                    new ImplementationRevisionRecorded(PayloadMapper.ToDomain(dto.Revision))),
                EventKinds.RoleBound => Read<RoleBoundV1>(json, dto =>
                    new RoleBound(PayloadMapper.ToDomain(dto.Binding))),
                EventKinds.AuthorityEnvelopeChanged => Read<AuthorityEnvelopeChangedV1>(json, dto =>
                    new AuthorityEnvelopeChanged(
                        new SemanticRevisionId(PayloadMapper.ParseGuid(dto.SemanticRevisionId)),
                        PayloadMapper.ToDomain(dto.Envelope),
                        dto.Reason)),
                EventKinds.PolicyDecisionRecorded => Read<PolicyDecisionRecordedV1>(json, dto =>
                    new PolicyDecisionRecorded(PayloadMapper.ToDomain(dto.Decision))),
                EventKinds.VerificationRecorded when schemaVersion == 1 => Read<VerificationRecordedV1>(json, dto =>
                    new VerificationRecorded(PayloadMapper.ToDomain(dto.Run))),
                EventKinds.VerificationRecorded when schemaVersion == 2 => Read<VerificationRecordedV2>(json, dto =>
                    new VerificationRecorded(PayloadMapper.ToDomain(dto.Run))),
                EventKinds.VerificationRecorded => Read<VerificationRecordedV3>(json, dto =>
                    new VerificationRecorded(PayloadMapper.ToDomain(dto.Run))),
                EventKinds.GateOpened => Read<GateOpenedV1>(json, dto =>
                    new GateOpened(PayloadMapper.ToDomain(dto.Gate))),
                EventKinds.GateDecided => Read<GateDecidedV1>(json, dto =>
                    new GateDecided(PayloadMapper.ToDomain(dto.Decision))),
                EventKinds.ReclassificationRecorded => Read<ReclassificationRecordedV1>(json, dto =>
                    new ReclassificationRecorded(PayloadMapper.ToDomain(dto.Reclassification), PayloadMapper.ToDomain(dto.Revision))),
                EventKinds.ReviewRequested => Read<ReviewRequestedV1>(json, dto =>
                    new ReviewRequested(PayloadMapper.ToDomain(dto.Request))),
                EventKinds.ProofContractRecorded => Read<ProofContractRecordedV1>(json, dto =>
                    new ProofContractRecorded(PayloadMapper.ToDomain(dto.Contract))),
                EventKinds.ProofRecorded => Read<ProofRecordedV1>(json, dto =>
                    new ProofRecorded(PayloadMapper.ToDomain(dto.Proof))),
                EventKinds.ReviewRecorded => Read<ReviewRecordedV1>(json, dto =>
                    new ReviewRecorded(PayloadMapper.ToDomain(dto.Review))),
                _ => throw new EventStoreCorruptionException($"Unknown event kind '{kind}'.", taskId, sequence),
            };
        }
        catch (JsonException e)
        {
            throw new EventStoreCorruptionException($"Malformed '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
        catch (FormatException e)
        {
            throw new EventStoreCorruptionException($"Invalid value in '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
        catch (Core.InvalidProofException e)
        {
            // A stored contract or proof whose own shape Core refuses (a hash that does not match its content).
            throw new EventStoreCorruptionException($"Invalid value in '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
        catch (Core.InvalidReviewException e)
        {
            // A stored review whose own shape Core refuses (a report that does not hash to the recorded hash).
            throw new EventStoreCorruptionException($"Invalid value in '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
        catch (Core.InvalidGateException e)
        {
            // A stored gate or decision whose own shape Core refuses (a clarify gate bound to an implementation, an answer on a request for changes...).
            throw new EventStoreCorruptionException($"Invalid value in '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
        catch (ArgumentException e)
        {
            // Core refused a stored identifier/text/id (invalid characters, blank, empty GUID...).
            throw new EventStoreCorruptionException($"Invalid value in '{kind}' payload: {e.Message}", taskId, sequence, e);
        }
    }

    private static EncodedPayload Write<T>(string kind, T dto)
        => Write(kind, CurrentPayloadVersion, dto);

    private static EncodedPayload Write<T>(string kind, int version, T dto)
        => new(kind, version, JsonSerializer.Serialize(dto, Json));

    private static int[] SupportedVersions(string kind)
        => kind switch
        {
            EventKinds.VerificationRecorded => [1, 2, VerificationPayloadVersion],
            EventKinds.TaskCreated => [1, TaskCreatedPayloadVersion, TaskCreatedWhatPayloadVersion, TaskCreatedHowPayloadVersion, TaskCreatedReviewPayloadVersion, TaskCreatedProvePayloadVersion],
            _ => [CurrentPayloadVersion],
        };

    // Decode dispatches on (kind, version) to the matching DTO type and mapping.
    private static DomainEvent Read<TDto>(string json, Func<TDto, DomainEvent> map)
        where TDto : class
    {
        var dto = JsonSerializer.Deserialize<TDto>(json, Json)
            ?? throw new JsonException("The payload is the JSON literal null.");

        return map(dto);
    }
}
