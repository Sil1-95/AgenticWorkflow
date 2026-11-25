using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

public sealed class SemanticRevisionTests
{
    [Fact]
    public void Initial_revision_has_ordinal_one_no_parent_and_initial_kind()
    {
        var revision = NewTask().State.CurrentSemanticRevision;

        Assert.Equal(1, revision.Ordinal);
        Assert.Null(revision.ParentId);
        Assert.Equal(SemanticChangeKind.Initial, revision.Kind);
        Assert.Equal(TaskOne, revision.TaskId);
        Assert.Equal(T0, revision.CreatedAt);
    }

    [Fact]
    public void Child_revision_descends_from_the_current_one_with_the_next_ordinal()
    {
        var stream = NewTask();
        var initial = stream.State.CurrentSemanticRevision;

        var child = stream.CreateSemanticRevision(SemanticChangeKind.StrategyChange, "Use polling instead of webhooks.", At(1));
        var grandchild = stream.CreateSemanticRevision(SemanticChangeKind.HumanRequestChanges, "Reviewer wants a queue.", At(2));

        Assert.Equal(initial.Id, child.ParentId);
        Assert.Equal(2, child.Ordinal);
        Assert.Equal(child.Id, grandchild.ParentId);
        Assert.Equal(3, grandchild.Ordinal);
        Assert.Equal(grandchild.Id, stream.State.CurrentSemanticRevisionId);
        Assert.Equal([1, 2, 3], stream.State.SemanticRevisions.Select(r => r.Ordinal));
    }

    [Fact]
    public void Historical_revisions_are_not_mutated_by_later_revisions()
    {
        var stream = NewTask();
        var initialBefore = stream.State.SemanticRevisions[0];

        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope grew", At(1));

        Assert.Same(initialBefore, stream.State.SemanticRevisions[0]);
        Assert.Equal(SemanticChangeKind.Initial, stream.State.SemanticRevisions[0].Kind);
    }

    [Fact]
    public void Custom_change_kinds_are_allowed_but_initial_is_reserved()
    {
        var stream = NewTask();

        var revision = stream.CreateSemanticRevision(new SemanticChangeKind("compliance_ruling"), "legal changed scope", At(1));
        Assert.Equal("compliance_ruling", revision.Kind.Value);

        Assert.Throws<InvalidRevisionLineageException>(
            () => stream.CreateSemanticRevision(SemanticChangeKind.Initial, "sneaky", At(2)));
    }

    [Fact]
    public void Revision_requires_an_explanation()
        => Assert.Throws<ArgumentException>(
            () => NewTask().CreateSemanticRevision(SemanticChangeKind.RequirementChange, " ", At(1)));

    [Fact]
    public void Record_shape_rules_reject_incoherent_lineage()
    {
        var id = new SemanticRevisionId(Guid.NewGuid());
        var other = new SemanticRevisionId(Guid.NewGuid());

        Assert.Throws<InvalidRevisionLineageException>(
            () => new SemanticRevision(id, TaskOne, 0, null, SemanticChangeKind.Initial, "x", T0));
        Assert.Throws<InvalidRevisionLineageException>(
            () => new SemanticRevision(id, TaskOne, 1, other, SemanticChangeKind.Initial, "x", T0));
        Assert.Throws<InvalidRevisionLineageException>(
            () => new SemanticRevision(id, TaskOne, 2, null, SemanticChangeKind.RequirementChange, "x", T0));
        Assert.Throws<InvalidRevisionLineageException>(
            () => new SemanticRevision(id, TaskOne, 2, id, SemanticChangeKind.RequirementChange, "x", T0));
    }

    [Fact]
    public void Replay_rejects_a_revision_whose_parent_is_not_the_current_revision()
    {
        var stream = NewTask();
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "r2", At(1));
        var history = stream.Events.ToList();

        // A third revision that (wrongly) descends from revision 1 instead of revision 2: branching lineage.
        var branch = new SemanticRevision(
            new SemanticRevisionId(Guid.NewGuid()), TaskOne, 2, stream.State.SemanticRevisions[0].Id,
            SemanticChangeKind.StrategyChange, "branch", At(2));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 3, At(2), new SemanticRevisionCreated(branch)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_an_ordinal_gap()
    {
        var stream = NewTask();
        var history = stream.Events.ToList();
        var skipped = new SemanticRevision(
            new SemanticRevisionId(Guid.NewGuid()), TaskOne, 3, stream.State.CurrentSemanticRevisionId,
            SemanticChangeKind.RequirementChange, "skipped 2", At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new SemanticRevisionCreated(skipped)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_revision_that_belongs_to_another_task()
    {
        var stream = NewTask();
        var history = stream.Events.ToList();
        var foreign = new SemanticRevision(
            new SemanticRevisionId(Guid.NewGuid()), TaskTwo, 2, stream.State.CurrentSemanticRevisionId,
            SemanticChangeKind.RequirementChange, "foreign", At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new SemanticRevisionCreated(foreign)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_revision_stamped_differently_from_its_event()
    {
        var stream = NewTask();
        var history = stream.Events.ToList();
        var skewed = new SemanticRevision(
            new SemanticRevisionId(Guid.NewGuid()), TaskOne, 2, stream.State.CurrentSemanticRevisionId,
            SemanticChangeKind.RequirementChange, "skewed", At(5));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new SemanticRevisionCreated(skewed)));

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }
}

public sealed class ImplementationRevisionTests
{
    [Fact]
    public void Fingerprint_is_preserved_verbatim_and_never_interpreted()
    {
        var stream = NewTask();
        const string odd = "  Weird::Fingerprint/with CASE and spaces  ";

        var revision = stream.RecordImplementationRevision(new ImplementationFingerprint(odd), At(1));

        Assert.Equal(odd, revision.Fingerprint.Value);
        Assert.Equal(odd, stream.State.CurrentImplementationRevision!.Fingerprint.Value);
    }

    [Fact]
    public void Blank_fingerprint_is_rejected()
        => Assert.Throws<ArgumentException>(() => new ImplementationFingerprint(" "));

    [Fact]
    public void First_revision_has_no_parent_and_later_ones_chain_to_the_previous_current()
    {
        var stream = NewTask();

        var first = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var second = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(2));
        var revert = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(3));

        Assert.Null(first.ParentId);
        Assert.Equal(first.Id, second.ParentId);
        Assert.Equal(second.Id, revert.ParentId);
        Assert.NotEqual(first.Id, revert.Id);
        Assert.Equal(revert.Id, stream.State.CurrentImplementationRevisionId);
        Assert.Equal(3, stream.State.ImplementationRevisions.Length);
    }

    [Fact]
    public void Implementation_and_semantic_revisions_are_independent_axes()
    {
        var stream = NewTask();
        var semanticBefore = stream.State.CurrentSemanticRevisionId;

        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(2));

        Assert.Equal(semanticBefore, stream.State.CurrentSemanticRevisionId);
        Assert.Single(stream.State.SemanticRevisions);

        var implementationBefore = stream.State.CurrentImplementationRevisionId;
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "new requirement", At(3));

        Assert.Equal(implementationBefore, stream.State.CurrentImplementationRevisionId);
    }

    [Fact]
    public void First_implementation_revision_binds_to_the_current_semantic_revision()
    {
        var stream = NewTask();

        var revision = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));

        Assert.Equal(stream.State.CurrentSemanticRevisionId, revision.SemanticRevisionId);
    }

    [Fact]
    public void Repair_under_unchanged_semantics_stays_bound_to_the_same_semantic_revision()
    {
        var stream = NewTask();

        var first = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var repair = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(2));

        Assert.Equal(first.SemanticRevisionId, repair.SemanticRevisionId);
        Assert.Equal(stream.State.CurrentSemanticRevisionId, repair.SemanticRevisionId);
    }

    [Fact]
    public void Semantic_change_does_not_mutate_or_replace_the_existing_implementation_revision()
    {
        var stream = NewTask();
        var original = stream.State.CurrentSemanticRevisionId;
        var implementation = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));

        var changed = stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope grew", At(2));

        Assert.Same(implementation, stream.State.CurrentImplementationRevision);
        Assert.Single(stream.State.ImplementationRevisions);
        Assert.Equal(original, stream.State.CurrentImplementationRevision!.SemanticRevisionId);
        Assert.NotEqual(changed.Id, stream.State.CurrentImplementationRevision.SemanticRevisionId);
        Assert.Equal(changed.Id, stream.State.CurrentSemanticRevisionId);
    }

    [Fact]
    public void Next_implementation_revision_after_a_semantic_change_binds_to_the_new_semantic_revision()
    {
        var stream = NewTask();
        var s1 = stream.State.CurrentSemanticRevisionId;
        var i1 = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var s2 = stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope grew", At(2));

        var i2 = stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));

        Assert.Equal(s1, i1.SemanticRevisionId);
        Assert.Equal(s2.Id, i2.SemanticRevisionId);
        Assert.Equal(i1.Id, i2.ParentId);
    }

    [Fact]
    public void Replay_preserves_the_implementation_to_semantic_binding()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "scope grew", At(2));
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:2"), At(3));

        var replayed = TaskStream.Replay(stream.Events, new SequentialIdSource());

        Assert.Equal(stream.State, replayed.State);
        Assert.Equal(
            stream.State.ImplementationRevisions.Select(r => r.SemanticRevisionId),
            replayed.State.ImplementationRevisions.Select(r => r.SemanticRevisionId));
        Assert.NotEqual(
            replayed.State.ImplementationRevisions[0].SemanticRevisionId,
            replayed.State.ImplementationRevisions[1].SemanticRevisionId);
    }

    [Fact]
    public void Replay_rejects_an_implementation_revision_that_references_an_unknown_semantic_revision()
    {
        var history = NewTask().Events.ToList();
        var forged = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()), TaskOne, new SemanticRevisionId(Guid.NewGuid()),
            new ImplementationFingerprint("t"), null, At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new ImplementationRevisionRecorded(forged)));

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_an_implementation_revision_that_references_a_semantic_revision_not_yet_created()
    {
        var future = NewTask();
        var later = future.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "later", At(1));

        var history = NewTask().Events.ToList();
        var early = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()), TaskOne, later.Id,
            new ImplementationFingerprint("t"), null, At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new ImplementationRevisionRecorded(early)));

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_revision_from_another_task()
    {
        var history = NewTask().Events.ToList();
        var foreign = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()), TaskTwo, NewTask().State.CurrentSemanticRevisionId, new ImplementationFingerprint("t"), null, At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new ImplementationRevisionRecorded(foreign)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_wrong_parent()
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var history = stream.Events.ToList();

        var orphan = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()), TaskOne, stream.State.CurrentSemanticRevisionId, new ImplementationFingerprint("tree:2"), null, At(2));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 3, At(2), new ImplementationRevisionRecorded(orphan)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Replay_rejects_a_parent_that_does_not_exist()
    {
        var history = NewTask().Events.ToList();
        var dangling = new ImplementationRevision(
            new ImplementationRevisionId(Guid.NewGuid()), TaskOne, NewTask().State.CurrentSemanticRevisionId, new ImplementationFingerprint("t"),
            new ImplementationRevisionId(Guid.NewGuid()), At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new ImplementationRevisionRecorded(dangling)));

        Assert.Throws<InvalidRevisionLineageException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }
}
