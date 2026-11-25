using AgenticWorkflow.Core.Proofs;
using AgenticWorkflow.Core.Verification;
using static AgenticWorkflow.Persistence.Sqlite.Tests.Fixture;

namespace AgenticWorkflow.Persistence.Sqlite.Tests;

/// <summary>
/// Formal PROVE is stored as additive versioned payloads (<c>proof_contract_recorded</c> v1, <c>proof_recorded</c> v1, and <c>task_created</c> v6 only when the requirement was
/// forced), so every earlier history replays byte for byte. A contract and a proof replay in a fresh store to exactly the live state; no transcript is ever stored; and a damaged or
/// forged record (a contract or report that does not hash, a proof of another implementation, a duplicate) fails loudly instead of satisfying a requirement.
/// </summary>
public sealed class ProofPersistenceTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static ProofClaim Claim(string id = "c1")
        => new(id, "The empty case yields an empty list.", [ProofEvidenceRequirement.Verification("t"), ProofEvidenceRequirement.Observation("run it on an empty file")], "It returns [] and exits 0.");

    private static ProofReport Report(ProofVerdict verdict = ProofVerdict.Pass)
        => new("Judged.", [new ProofClaimResult("c1", verdict, [new ProofEvidenceRef(ProofEvidenceRefKind.Verification, "t"), new ProofEvidenceRef(ProofEvidenceRefKind.Command, "1")], "Because.")]);

    private static void Verify(TaskStream stream, int at)
        => stream.RecordVerification(stream.State.CurrentImplementationRevisionId!.Value, [new VerificationCheckResult(new VerificationCheckId("t"), VerificationOutcome.Passed, "ok", 0)], At(at));

    /// <summary>PROVE forced (or requested by the agent); implemented and verified; FAIL, a repair, then PASS; completed.</summary>
    private static TaskStream Proven(bool forced = true)
    {
        var stream = TaskStream.Create(TaskOne, "Add retry to the importer", CompletionBoundary.WorkingTree, T0, new GuidIdSource(), AcceptanceRequirement.NotRequired, false, false, false, forced);
        stream.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        stream.BindRole(Role("prover", "repository_tools"), Profile("p-prover", null, "repository_tools"), At(1));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2));
        Verify(stream, 3);
        stream.RecordProofContract(new ProofContractId(Guid.NewGuid()), "The edge matters.", [Claim()], 1, "implementer-1", At(4));
        stream.RecordProof(new ProofId(Guid.NewGuid()), Report(ProofVerdict.Fail), new ProfileId("p-prover"), "prover-1", "implementer-1", 1500, 1, ["dotnet run -- empty.csv"], false, 0, At(5));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(6));
        Verify(stream, 7);
        stream.RecordProof(new ProofId(Guid.NewGuid()), Report(), new ProfileId("p-prover"), "prover-2", "implementer-1", 900, 1, ["dotnet run -- empty.csv"], true, 400, At(8));
        stream.CompleteVerified(At(9));
        return stream;
    }

    private async Task<SqliteEventStore> SeedAsync(TaskStream stream)
    {
        var store = await _db.OpenStoreAsync();
        await PersistAsync(store, stream);
        return store;
    }

    private void Damage(string sql)
        => Assert.True(_db.Execute(sql, []) > 0, "The damaging statement must change something, or the test proves nothing.");

    private string LastProofPayload() => _db.Scalar<string>("SELECT payload_json FROM task_events WHERE event_kind = 'proof_recorded' ORDER BY sequence DESC LIMIT 1");

    private long LastProofSequence() => _db.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'proof_recorded' ORDER BY sequence DESC LIMIT 1");

    private long ContractSequence() => _db.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'proof_contract_recorded' ORDER BY sequence DESC LIMIT 1");

    // ---- stable, additive storage --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_events_are_stored_with_stable_kinds_and_versions()
    {
        await SeedAsync(Proven(forced: false));

        Assert.Equal(1L, _db.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind = 'proof_contract_recorded' AND payload_schema_version = 1"));
        Assert.Equal(2L, _db.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind = 'proof_recorded' AND payload_schema_version = 1"));

        var json = LastProofPayload();
        foreach (var name in new[] { "report_hash", "prover_profile", "prover_session", "implementer_session", "semantic_revision_id", "implementation_revision_id", "verification_run_id", "configured_check_rerun", "duplicated_verification" })
            Assert.Contains(name, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_forced_requirement_is_in_the_first_event_at_version_6_and_a_task_without_it_is_written_exactly_as_before()
    {
        await SeedAsync(Proven(forced: true));
        Assert.Equal(6L, _db.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1"));
        Assert.Contains("prove_required", _db.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"));

        using var other = new TempDatabase();
        var plain = NewTask();
        plain.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        plain.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2));
        Verify(plain, 3);
        await PersistAsync(await other.OpenStoreAsync(), plain);

        // no prove member, no new version: an older history's bytes are untouched by M8
        Assert.True(other.Scalar<long>("SELECT payload_schema_version FROM task_events WHERE sequence = 1") < 6);
        Assert.DoesNotContain("prove", other.Scalar<string>("SELECT payload_json FROM task_events WHERE sequence = 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, other.Scalar<long>("SELECT COUNT(*) FROM task_events WHERE event_kind LIKE 'proof%'"));
    }

    // ---- replay ------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_proven_history_replays_in_a_fresh_store_to_exactly_the_live_state(bool forced)
    {
        var original = Proven(forced);
        await SeedAsync(original);

        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        Assert.Equal(original.State, replayed);
        Assert.Equal(2, replayed.Proofs.Length);
        Assert.Equal(ProofStatus.PassedCurrent, replayed.ProofStatus);
        Assert.True(replayed.IsProveSatisfied);
        Assert.Equal(forced, replayed.ProveRequired);
        Assert.Equal(!forced, replayed.ProveEscalated);
        Assert.Equal(replayed.Proofs[0].Report.ContentHash, replayed.Proofs[0].ReportHash);
        Assert.Equal(replayed.CurrentProofContract!.ContentHash, replayed.Proofs[1].ContractHash);
        Assert.False(replayed.IsProofCurrent(replayed.Proofs[0]));   // the first proof is history after the repair
        Assert.True(replayed.IsProofCurrent(replayed.Proofs[1]));
    }

    [Fact]
    public async Task The_contract_claims_requirements_and_the_proof_provenance_and_cost_survive_the_round_trip()
    {
        await SeedAsync(Proven());
        var replayed = (await LoadAndReplayAsync(await _db.OpenStoreAsync(), TaskOne)).State;

        var claim = Assert.Single(replayed.CurrentProofContract!.Claims);
        Assert.Equal("c1", claim.Id);
        Assert.Equal(["verification:t", "observation:run it on an empty file"], claim.Evidence.Select(e => e.ToString()).ToArray());

        var last = replayed.Proofs[^1];
        Assert.Equal(new ProfileId("p-prover"), last.ProverProfile);
        Assert.Equal("prover-2", last.ProverSession);
        Assert.Equal("implementer-1", last.ImplementerSession);
        Assert.Equal(2, last.Round);
        Assert.Equal(900, last.ProverMilliseconds);
        Assert.Equal(1, last.ProverCommandCount);
        Assert.Equal(["dotnet run -- empty.csv"], last.ProverCommands.ToArray());
        Assert.True(last.ConfiguredCheckRerun);
        Assert.Equal(400, last.DuplicatedVerificationMilliseconds);

        var all = string.Concat(_db.Scalar<string>("SELECT group_concat(payload_json, ' ') FROM task_events WHERE event_kind LIKE 'proof%'"));
        foreach (var forbidden in new[] { "transcript", "reasoning", "thinking", "conversation_text", "messages" })
            Assert.DoesNotContain(forbidden, all, StringComparison.OrdinalIgnoreCase);
    }

    // ---- damage and forgery ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_that_no_longer_matches_its_recorded_hash_is_corruption()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.proof.report.summary', 'Everything is fine, trust me.') WHERE sequence = {LastProofSequence()}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_contract_that_no_longer_matches_its_recorded_hash_is_corruption()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.contract.claims[0].statement', 'Something far easier to prove.') WHERE sequence = {ContractSequence()}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_verdict_or_a_missing_member_is_corruption()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.proof.report.claims[0].verdict', 'maybe') WHERE sequence = {LastProofSequence()}");
        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));

        using var second = new TempDatabase();
        await PersistAsync(await second.OpenStoreAsync(), Proven());
        var sequence = second.Scalar<long>("SELECT sequence FROM task_events WHERE event_kind = 'proof_recorded' ORDER BY sequence DESC LIMIT 1");
        Assert.True(second.Execute($"UPDATE task_events SET payload_json = json_remove(payload_json, '$.proof.prover_session') WHERE sequence = {sequence}", []) > 0);
        await Assert.ThrowsAsync<EventStoreCorruptionException>(async () => await (await second.OpenStoreAsync()).LoadAsync(TaskOne));
    }

    [Fact]
    public async Task An_unknown_evidence_kind_in_a_contract_is_corruption()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.contract.claims[0].evidence[0].kind', 'transcript') WHERE sequence = {ContractSequence()}");

        await Assert.ThrowsAsync<EventStoreCorruptionException>(() => store.LoadAsync(TaskOne));
    }

    [Fact]
    public async Task A_forged_proof_of_a_different_implementation_fails_replay()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.proof.implementation_revision_id', '{Guid.NewGuid()}') WHERE sequence = {LastProofSequence()}");

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_forged_proof_that_names_the_implementers_conversation_as_the_prover_fails_replay()
    {
        var store = await SeedAsync(Proven());
        Damage($"UPDATE task_events SET payload_json = json_set(payload_json, '$.proof.prover_session', 'implementer-1') WHERE sequence = {LastProofSequence()}");

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_duplicated_proof_event_fails_replay()
    {
        var stream = Proven();
        var store = await SeedAsync(stream);
        var duplicate = (ProofRecorded)stream.Events.Last(e => e.Payload is ProofRecorded).Payload;

        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(20), duplicate)]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_second_contract_for_the_same_semantic_revision_fails_replay()
    {
        var stream = Proven();
        var store = await SeedAsync(stream);
        var original = ((ProofContractRecorded)stream.Events.Single(e => e.Payload is ProofContractRecorded).Payload).Contract;
        var again = new ProofContract(new ProofContractId(Guid.NewGuid()), original.SemanticRevisionId, 1, "implementer-1", "again", [Claim("c2")]);

        await store.AppendAsync(TaskOne, stream.Events.Count, [new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, stream.Events.Count + 1, At(20), new ProofContractRecorded(again))]);

        await Assert.ThrowsAnyAsync<DomainException>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_completion_without_a_current_PASS_fails_replay()
    {
        var stream = Proven();
        var store = await SeedAsync(stream);

        // drop the passing proof, keeping the completion that depended on it
        Damage($"DELETE FROM task_events WHERE sequence = {LastProofSequence()}");

        await Assert.ThrowsAnyAsync<Exception>(() => LoadAndReplayAsync(store, TaskOne));
    }

    [Fact]
    public async Task A_proof_recorded_in_a_history_that_never_required_one_fails_replay()
    {
        var plain = NewTask();
        plain.BindRole(Role("root", "code_edit"), Profile("p-impl", null, "code_edit"), At(1));
        plain.BindRole(Role("prover", "repository_tools"), Profile("p-prover", null, "repository_tools"), At(1));
        plain.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(2));
        Verify(plain, 3);

        var forged = Proven().State.Proofs[^1];
        var history = plain.Events.Append(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, plain.Events.Count + 1, At(9), new ProofRecorded(forged))).ToList();

        Assert.ThrowsAny<DomainException>(() => TaskStream.Replay(history, new GuidIdSource()));
    }
}
