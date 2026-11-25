using static AgenticWorkflow.Core.Tests.Fixture;

namespace AgenticWorkflow.Core.Tests;

public sealed class RoleProfileTests
{
    [Fact]
    public void Binding_is_explicit_and_snapshots_role_profile_and_semantic_revision()
    {
        var stream = NewTask();
        var role = Role("implementer", "code_edit");
        var profile = Profile("p-impl", "code_edit", "repository_tools");

        stream.BindRole(role, profile, At(1));

        var binding = stream.State.GetBinding(new RoleId("implementer"));
        Assert.Equal(role, binding.Role);
        Assert.Equal(profile, binding.Profile);
        Assert.Equal(stream.State.CurrentSemanticRevisionId, binding.BoundAt);
        Assert.IsType<RoleBound>(stream.Events[^1].Payload);
    }

    [Fact]
    public void Unbound_role_has_no_fallback()
    {
        var stream = NewTask();
        stream.BindRole(Role("implementer"), Profile("p-impl"), At(1));

        var ex = Assert.Throws<UnboundRoleException>(() => stream.State.GetBinding(new RoleId("reviewer")));

        Assert.Equal(new RoleId("reviewer"), ex.Role);
        Assert.False(stream.State.RoleBindings.ContainsKey(new RoleId("reviewer")));
    }

    [Fact]
    public void Profile_lacking_a_required_capability_cannot_fill_the_role()
    {
        var stream = NewTask();

        var ex = Assert.Throws<RoleBindingException>(
            () => stream.BindRole(Role("implementer", "code_edit", "fresh_context"), Profile("p-ro", "code_edit", "read_only"), At(1)));

        Assert.Contains("fresh_context", ex.Message);
        Assert.Empty(stream.State.RoleBindings);
        Assert.Single(stream.Events);
    }

    [Fact]
    public void Rebinding_replaces_the_binding_and_keeps_the_earlier_one_in_history()
    {
        var stream = NewTask();
        stream.BindRole(Role("reviewer", "fresh_context"), Profile("p-a", "fresh_context"), At(1));
        stream.CreateSemanticRevision(SemanticChangeKind.StrategyChange, "stronger review", At(2));

        stream.BindRole(Role("reviewer", "fresh_context"), Profile("p-b", "fresh_context", "semantic_reasoning"), At(3));

        Assert.Equal(new ProfileId("p-b"), stream.State.GetBinding(new RoleId("reviewer")).Profile.Id);
        Assert.Equal(
            [new ProfileId("p-a"), new ProfileId("p-b")],
            stream.Events.Select(e => e.Payload).OfType<RoleBound>().Select(b => b.Binding.Profile.Id));
    }

    [Fact]
    public void Replay_rejects_a_binding_made_under_a_stale_semantic_revision()
    {
        var stream = NewTask();
        var stale = new RoleBinding(Role("r"), Profile("p"), stream.State.CurrentSemanticRevisionId);
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "moved on", At(1));
        var history = stream.Events.ToList();
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 3, At(2), new RoleBound(stale)));

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Capabilities_are_open_ended_identifiers_with_set_semantics()
    {
        var capabilities = IdentifierSet<ProfileCapability>.Of(Cap("code_edit"), Cap("vendor.custom-capability"), Cap("code_edit"));

        Assert.Equal(2, capabilities.Count);
        Assert.True(capabilities.Contains(Cap("vendor.custom-capability")));
        Assert.Equal(capabilities, IdentifierSet<ProfileCapability>.Of(Cap("vendor.custom-capability"), Cap("code_edit")));
        Assert.Equal(["code_edit", "vendor.custom-capability"], capabilities.Select(c => c.Value));
    }

    [Fact]
    public void A_role_with_no_requirements_accepts_any_profile()
    {
        var stream = NewTask();

        stream.BindRole(Role("observer"), Profile("p-any"), At(1));

        Assert.Single(stream.State.RoleBindings);
    }
}

public sealed class AuthorityEnvelopeTests
{
    [Fact]
    public void Classifies_allowed_reserved_and_unclassified_actions_distinctly()
    {
        var envelope = Envelope(["dependency_change"], ["deployment"]);

        Assert.Equal(AuthorityClassification.AllowedWithoutHuman, envelope.Classify(new ActionId("dependency_change")));
        Assert.Equal(AuthorityClassification.ReservedForHuman, envelope.Classify(new ActionId("deployment")));
        Assert.Equal(AuthorityClassification.Unclassified, envelope.Classify(new ActionId("public_api_change")));
    }

    [Fact]
    public void Unclassified_is_the_default_for_an_empty_envelope_and_is_not_permission()
        => Assert.Equal(AuthorityClassification.Unclassified, AuthorityEnvelope.Empty.Classify(new ActionId("destructive_action")));

    [Fact]
    public void An_action_cannot_be_both_allowed_and_reserved()
        => Assert.Throws<ArgumentException>(() => Envelope(["deployment"], ["deployment"]));

    [Fact]
    public void Categories_are_extensible_project_identifiers()
    {
        var envelope = Envelope(["acme.schema_migration"], []);

        Assert.Equal(AuthorityClassification.AllowedWithoutHuman, envelope.Classify(new ActionId("acme.schema_migration")));
    }

    [Fact]
    public void Authority_change_is_an_explicit_event_under_the_current_semantic_revision()
    {
        var stream = NewTask();
        var envelope = Envelope(["dependency_change"], ["deployment"]);

        stream.ChangeAuthority(envelope, "Human delegated dependency changes.", At(1));

        var change = Assert.IsType<AuthorityEnvelopeChanged>(stream.Events[^1].Payload);
        Assert.Equal(stream.State.CurrentSemanticRevisionId, change.SemanticRevisionId);
        Assert.Equal("Human delegated dependency changes.", change.Reason);
        Assert.Equal(envelope, stream.State.Authority);
    }

    [Fact]
    public void Successive_changes_replace_the_envelope_and_all_remain_in_history()
    {
        var stream = NewTask();

        stream.ChangeAuthority(Envelope(["dependency_change"], []), "first", At(1));
        stream.ChangeAuthority(Envelope([], ["dependency_change"]), "tightened", At(2));

        Assert.Equal(
            AuthorityClassification.ReservedForHuman,
            stream.State.Authority.Classify(new ActionId("dependency_change")));
        Assert.Equal(2, stream.Events.Count(e => e.Payload is AuthorityEnvelopeChanged));
    }

    [Fact]
    public void Authority_change_requires_a_reason()
    {
        var stream = NewTask();

        Assert.Throws<ArgumentException>(() => stream.ChangeAuthority(AuthorityEnvelope.Empty, " ", At(1)));
        Assert.Single(stream.Events);
    }

    [Fact]
    public void Replay_rejects_an_authority_change_made_under_a_stale_semantic_revision()
    {
        var stream = NewTask();
        var staleRevision = stream.State.CurrentSemanticRevisionId;
        stream.CreateSemanticRevision(SemanticChangeKind.AuthorityChange, "authority moved", At(1));
        var history = stream.Events.ToList();
        history.Add(new EventEnvelope(
            new EventId(Guid.NewGuid()), TaskOne, 3, At(2),
            new AuthorityEnvelopeChanged(staleRevision, AuthorityEnvelope.Empty, "stale")));

        Assert.Throws<InvalidRevisionReferenceException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }

    [Fact]
    public void Authority_is_independent_of_policy_decisions()
    {
        var stream = NewTask();
        stream.ChangeAuthority(Envelope([], ["deployment"]), "reserve deployment", At(1));
        var authority = stream.State.Authority;

        stream.RecordPolicyDecision(new PolicyRuleId("deploy.guard"), PolicyEffect.Prevent, Trigger("deploy attempted"), "blocked", At(2));

        Assert.Same(authority, stream.State.Authority);
        Assert.Single(stream.State.PolicyDecisions);
    }
}

public sealed class PolicyDecisionTests
{
    [Theory]
    [InlineData(PolicyEffect.Prevent)]
    [InlineData(PolicyEffect.Require)]
    [InlineData(PolicyEffect.Detect)]
    [InlineData(PolicyEffect.Notify)]
    public void Every_effect_is_recorded_with_source_trigger_explanation_and_time(PolicyEffect effect)
    {
        var stream = NewTask();
        stream.RecordImplementationRevision(new ImplementationFingerprint("tree:1"), At(1));
        var trigger = new PolicyTrigger("agent ran 'npm install left-pad'", new ActionId("dependency_change"), "toolcall:42");

        var decision = stream.RecordPolicyDecision(
            new PolicyRuleId("deps.new-package"),
            effect,
            trigger,
            "A new dependency was introduced.",
            At(2),
            stream.State.CurrentSemanticRevisionId,
            stream.State.CurrentImplementationRevisionId);

        var recorded = Assert.Single(stream.State.PolicyDecisions);
        Assert.Same(decision, recorded);
        Assert.Equal(effect, recorded.Effect);
        Assert.Equal(new PolicyRuleId("deps.new-package"), recorded.Rule);
        Assert.Equal(trigger, recorded.Trigger);
        Assert.Equal("agent ran 'npm install left-pad'", recorded.Trigger.Description);
        Assert.Equal(new ActionId("dependency_change"), recorded.Trigger.Action);
        Assert.Equal("toolcall:42", recorded.Trigger.Reference);
        Assert.Equal("A new dependency was introduced.", recorded.Explanation);
        Assert.Equal(At(2), recorded.DecidedAt);
        Assert.Equal(TaskOne, recorded.TaskId);
        Assert.Equal(stream.State.CurrentSemanticRevisionId, recorded.SemanticRevisionId);
        Assert.Equal(stream.State.CurrentImplementationRevisionId, recorded.ImplementationRevisionId);
        Assert.Equal(EventKinds.PolicyDecisionRecorded, stream.Events[^1].Kind);
    }

    [Fact]
    public void Revisions_are_optional_and_nothing_is_attached_implicitly()
    {
        var stream = NewTask();

        var decision = stream.RecordPolicyDecision(new PolicyRuleId("r"), PolicyEffect.Notify, Trigger(), "fyi", At(1));

        Assert.Null(decision.SemanticRevisionId);
        Assert.Null(decision.ImplementationRevisionId);
    }

    [Fact]
    public void Decision_referencing_an_unknown_revision_is_rejected()
    {
        var stream = NewTask();

        Assert.Throws<InvalidRevisionReferenceException>(() => stream.RecordPolicyDecision(
            new PolicyRuleId("r"), PolicyEffect.Detect, Trigger(), "x", At(1),
            semanticRevisionId: new SemanticRevisionId(Guid.NewGuid())));
        Assert.Throws<InvalidRevisionReferenceException>(() => stream.RecordPolicyDecision(
            new PolicyRuleId("r"), PolicyEffect.Detect, Trigger(), "x", At(1),
            implementationRevisionId: new ImplementationRevisionId(Guid.NewGuid())));
        Assert.Empty(stream.State.PolicyDecisions);
    }

    [Fact]
    public void Decision_may_reference_a_superseded_semantic_revision()
    {
        var stream = NewTask();
        var original = stream.State.CurrentSemanticRevisionId;
        stream.CreateSemanticRevision(SemanticChangeKind.RequirementChange, "changed", At(1));

        var decision = stream.RecordPolicyDecision(
            new PolicyRuleId("r"), PolicyEffect.Detect, Trigger(), "observed under the old meaning", At(2), original);

        Assert.Equal(original, decision.SemanticRevisionId);
    }

    [Fact]
    public void Decision_requires_a_source_a_trigger_and_an_explanation()
    {
        Assert.Throws<ArgumentException>(() => new PolicyTrigger(" "));
        Assert.Throws<ArgumentException>(() => new PolicyRuleId("Not Valid"));
        Assert.Throws<ArgumentException>(
            () => new PolicyDecision(TaskOne, new PolicyRuleId("r"), PolicyEffect.Notify, Trigger(), "", T0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PolicyDecision(TaskOne, new PolicyRuleId("r"), (PolicyEffect)42, Trigger(), "x", T0));
    }

    [Fact]
    public void Replay_rejects_a_decision_for_another_task()
    {
        var history = NewTask().Events.ToList();
        var foreign = new PolicyDecision(TaskTwo, new PolicyRuleId("r"), PolicyEffect.Notify, Trigger(), "x", At(1));
        history.Add(new EventEnvelope(new EventId(Guid.NewGuid()), TaskOne, 2, At(1), new PolicyDecisionRecorded(foreign)));

        Assert.Throws<InvalidEventHistoryException>(() => TaskStream.Replay(history, new SequentialIdSource()));
    }
}
