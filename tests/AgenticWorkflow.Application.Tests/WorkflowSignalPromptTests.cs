using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The contract of the always-on workflow-signals section of the direct-path prompt, asserted as semantics rather than as a pinned string: what the agent is told about working
/// normally, about CLARIFY before WHAT/HOW, about the optional stages, about reclassification and about who owns a wait. The section is one compact block, so these tests also
/// guard its size and that it never coaches how the agent sequences its own tool calls or how the controller reuses evidence.
/// </summary>
public sealed class WorkflowSignalPromptTests
{
    private const string Clarify = AgentClarificationSignal.ToolName;
    private const string What = AgentWhatSignal.ToolName;
    private const string How = AgentHowSignal.ToolName;
    private const string Reclassify = AgentReclassificationSignal.ToolName;
    private const string Review = AgentReviewRequestSignal.ToolName;
    private const string Prove = AgentProveRequestSignal.ToolName;

    private static async Task<string> PromptAsync(bool clarify = true, bool what = true, bool how = true, bool whatRequired = false, bool reviewRequired = false, bool proveRequired = false)
    {
        var h = await DirectHarness.CreateAsync(whatRequired: whatRequired, reviewRequired: reviewRequired, reviewerProfile: reviewRequired ? "reviewer-profile" : null, proveRequired: proveRequired);
        var task = (await h.Service.LoadAsync(Fixture.TaskOne)).Task;

        return DirectPathPrompts.Initial(task, h.Definition(), clarify, what, how);
    }

    private static int Line(string prompt, string signal)
        => prompt.IndexOf($"- {signal}:", StringComparison.Ordinal);

    // ---- normal work -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Clear_and_fully_specified_work_is_described_as_normal_work_before_any_signal_is_named()
    {
        var prompt = await PromptAsync();

        var normal = prompt.IndexOf("use none: just do the work, run the checks and finish", StringComparison.Ordinal);

        Assert.True(normal > 0, "the prompt must say that work needing no signal simply proceeds to implementation and verification");
        Assert.All(new[] { Clarify, What, How, Reclassify, Review, Prove }, name => Assert.True(Line(prompt, name) > normal, $"{name} must be offered after the statement that signals are optional"));
        Assert.Contains("optional", prompt[..Line(prompt, Clarify)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_signal_is_offered_as_one_line_of_a_single_section_and_each_line_is_narrow()
    {
        var prompt = await PromptAsync();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(prompt, "Workflow signals are optional"));

        foreach (var name in new[] { What, How, Review, Prove })
        {
            Assert.StartsWith($"- {name}: only ", LineOf(prompt, name), StringComparison.Ordinal);   // "only if/for ...": a narrow trigger, never a default
        }

        Assert.Contains("do not use it", LineOf(prompt, What), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not use it merely because the task is difficult", LineOf(prompt, How), StringComparison.Ordinal);
        Assert.Contains("Not for routine work", LineOf(prompt, Review), StringComparison.Ordinal);
        Assert.Contains("Not for routine work", LineOf(prompt, Prove), StringComparison.Ordinal);
    }

    private static string LineOf(string prompt, string signal)
    {
        var start = Line(prompt, signal);
        Assert.True(start >= 0, $"{signal} is not offered");
        var end = prompt.IndexOf('\n', start);

        return prompt[start..(end < 0 ? prompt.Length : end)];
    }

    // ---- CLARIFY ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unresolved_human_owned_decision_is_clarified_before_any_WHAT_or_HOW_and_before_implementation()
    {
        var clarification = LineOf(await PromptAsync(), Clarify);

        Assert.Contains("only a person can make", clarification, StringComparison.Ordinal);
        Assert.Contains("BEFORE you propose any WHAT or HOW and before you implement", clarification, StringComparison.Ordinal);
        Assert.Contains("stop and wait", clarification, StringComparison.Ordinal);
        Assert.Contains("not a substitute", clarification, StringComparison.Ordinal);   // feedback on a proposal is not the clarification mechanism
        Assert.Contains("details you can reasonably decide yourself", clarification, StringComparison.Ordinal);   // not for what the agent can decide
    }

    [Fact]
    public async Task The_clarification_signal_is_listed_before_the_proposals_it_must_precede()
    {
        var prompt = await PromptAsync();

        Assert.True(Line(prompt, Clarify) < Line(prompt, What));
        Assert.True(Line(prompt, What) < Line(prompt, How));
    }

    [Fact]
    public async Task WHAT_and_HOW_are_contracts_and_never_a_question_mechanism_and_an_open_decision_goes_to_clarification_first()
    {
        var prompt = await PromptAsync();

        foreach (var (stage, tool) in new[] { ("WHAT", What), ("HOW", How) })
        {
            var line = LineOf(prompt, tool);
            Assert.Contains($"A {stage} proposal is a contract a person approves, not a question", line, StringComparison.Ordinal);
            Assert.Contains($"call the {Clarify} tool first, and propose the {stage} once it is answered", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_the_clarification_tool_a_proposal_is_still_not_a_question_and_the_tool_is_not_named()
    {
        var prompt = await PromptAsync(clarify: false);

        Assert.Contains("not a question", LineOf(prompt, What), StringComparison.Ordinal);
        Assert.Contains("not a question", LineOf(prompt, How), StringComparison.Ordinal);
        Assert.DoesNotContain(Clarify, prompt, StringComparison.Ordinal);
    }

    // ---- optional stages --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task REVIEW_and_PROVE_are_conditional_additions_that_do_not_stop_the_turn_and_are_never_routine()
    {
        var prompt = await PromptAsync();

        foreach (var tool in new[] { Review, Prove })
        {
            var line = LineOf(prompt, tool);
            Assert.StartsWith($"- {tool}: only for", line, StringComparison.Ordinal);
            Assert.Contains("does not stop your turn", line, StringComparison.Ordinal);
            Assert.Contains("cannot be withdrawn", line, StringComparison.Ordinal);
            Assert.Contains("Not for routine work", line, StringComparison.Ordinal);
        }

        var prove = LineOf(prompt, Prove);
        Assert.Contains("the configured checks are `tests`", prove, StringComparison.Ordinal);   // the task's own vocabulary is still stated
        Assert.Contains("Do NOT use `review:current-pass`", prove, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_review_is_not_offered_as_the_agents_choice_and_a_required_proof_is_a_directive_not_an_option()
    {
        var reviewRequired = await PromptAsync(reviewRequired: true);
        var proveRequired = await PromptAsync(proveRequired: true);

        Assert.DoesNotContain($"- {Review}:", reviewRequired, StringComparison.Ordinal);
        Assert.Contains($"- {Prove}:", reviewRequired, StringComparison.Ordinal);

        Assert.DoesNotContain($"- {Prove}:", proveRequired, StringComparison.Ordinal);
        Assert.Contains("Formal PROVE is required for this task", proveRequired, StringComparison.Ordinal);
        Assert.Contains($"- {Review}:", proveRequired, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_WHAT_keeps_its_directive_and_is_not_also_offered_as_an_option()
    {
        var prompt = await PromptAsync(whatRequired: true);

        Assert.DoesNotContain($"- {What}:", prompt, StringComparison.Ordinal);
        Assert.Contains("requires a human-approved WHAT before anything is implemented", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not create, edit or delete any file before a person approves the WHAT", prompt, StringComparison.Ordinal);
        Assert.Contains($"- {Clarify}:", prompt, StringComparison.Ordinal);
    }

    // ---- reclassification -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reclassification_remains_available_when_new_evidence_invalidates_a_stage_assumption()
    {
        var line = LineOf(await PromptAsync(), Reclassify);

        Assert.Contains("new evidence", line, StringComparison.Ordinal);
        Assert.Contains("approved WHAT or HOW", line, StringComparison.Ordinal);
        Assert.Contains("earliest stage to reconsider", line, StringComparison.Ordinal);
        Assert.Contains("\"how\"", line, StringComparison.Ordinal);
        Assert.Contains("\"what\"", line, StringComparison.Ordinal);
        Assert.Contains("stop and wait", line, StringComparison.Ordinal);
        Assert.Contains("Not for a failure you can repair", line, StringComparison.Ordinal);
    }

    // ---- who owns the wait; no approval by text ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_controller_owns_the_wait_and_the_resume_and_nothing_the_agent_writes_approves_or_answers()
    {
        var prompt = await PromptAsync();

        Assert.Contains("the controller owns the wait and the resume", prompt, StringComparison.Ordinal);
        Assert.Contains("nothing you write approves or answers anything", prompt, StringComparison.Ordinal);
        Assert.Contains("Your own report does not count; the controller's result does.", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task No_wording_introduces_approving_a_gate_or_answering_a_question_by_natural_language(bool clarify, bool what, bool how)
    {
        var prompt = await PromptAsync(clarify, what, how);

        foreach (var forbidden in new[] { "reply approved", "reply \"approve", "say approved", "say \"approve", "type approve", "respond with approval", "write approved", "answer yourself", "approve it yourself", "assume approval", "treat silence" })
            Assert.DoesNotContain(forbidden, prompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- not offered / compact / no sequencing or reuse coaching ----------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_said_about_workflow_signals_when_the_runtime_offers_none()
    {
        var prompt = await PromptAsync(clarify: false, what: false, how: false);

        Assert.DoesNotContain("Workflow signals", prompt, StringComparison.Ordinal);
        foreach (var name in new[] { Clarify, What, How, Reclassify, Review, Prove })
            Assert.DoesNotContain(name, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_whole_signals_section_stays_compact()
    {
        var all = await PromptAsync();
        var none = await PromptAsync(clarify: false, what: false, how: false);

        // The section was about 4.9 kB as six paragraphs; it must not creep back up.
        Assert.True(all.Length - none.Length < 4000, $"the workflow-signals section grew to {all.Length - none.Length} characters");
    }

    [Fact]
    public async Task The_prompt_does_not_coach_how_to_sequence_tool_calls_or_how_the_controller_reuses_evidence()
    {
        var prompt = await PromptAsync();

        foreach (var coaching in new[] { "combine", "same message", "single message", "in parallel", "batch", "one call", "reuse", "re-run", "rerun", "duplicate" })
            Assert.DoesNotContain(coaching, prompt, StringComparison.OrdinalIgnoreCase);
    }
}
