using System.Text.Json;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

namespace AgenticWorkflow.Runtime.Herdr.Tests;

/// <summary>
/// The MCP <c>instructions</c> a session receives are loaded into the agent's context for every call, so for the root agent's signal tools they only orient (each tool's own
/// description says when to use it) and repeat neither the tool descriptions nor the direct-path prompt. The reviewer's and the prover's instructions are their whole role and are kept.
/// </summary>
public sealed class ClaudeSignalInstructionsTests
{
    private const ClaudeClarificationServer.Tools RootTools = ClaudeClarificationServer.Tools.Clarification | ClaudeClarificationServer.Tools.What | ClaudeClarificationServer.Tools.How
        | ClaudeClarificationServer.Tools.Reclassification | ClaudeClarificationServer.Tools.ReviewRequest | ClaudeClarificationServer.Tools.ProveRequest;

    private static (string Instructions, string[] ToolNames) Initialize(ClaudeClarificationServer.Tools tools)
    {
        var raised = false;
        var proposed = false;
        var proposedHow = false;
        var reported = false;

        using var init = JsonDocument.Parse(ClaudeClarificationServer.Handle("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", tools, ref raised, ref proposed, ref proposedHow, ref reported)!.ToJsonString());
        using var list = JsonDocument.Parse(ClaudeClarificationServer.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", tools, ref raised, ref proposed, ref proposedHow, ref reported)!.ToJsonString());

        return (init.RootElement.GetProperty("result").GetProperty("instructions").GetString()!,
            list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public void The_root_agents_instructions_orient_once_and_state_who_owns_the_resume()
    {
        var (instructions, tools) = Initialize(RootTools);

        Assert.Equal(6, tools.Length);   // no workflow capability was removed
        Assert.Contains("signals to the workflow controller", instructions, StringComparison.Ordinal);
        Assert.Contains("Clear, fully specified work needs none", instructions, StringComparison.Ordinal);
        Assert.Contains("end your turn: the controller resumes your conversation once a person has decided", instructions, StringComparison.Ordinal);
        Assert.True(instructions.Length < 600, $"the always-loaded instructions grew to {instructions.Length} characters");
    }

    [Fact]
    public void The_root_agents_instructions_do_not_repeat_the_per_tool_guidance_which_lives_in_the_tool_descriptions()
    {
        var (instructions, _) = Initialize(RootTools);

        foreach (var repeated in new[] { "request_clarification", "propose_what", "propose_how", "report_reclassification", "request_review", "request_prove", "materially change the work", "externally visible", "Proof Contract" })
            Assert.DoesNotContain(repeated, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_signal_tool_still_says_where_it_is_listed_when_to_use_it_and_to_stop_where_it_stops_the_turn()
    {
        var raised = false;
        var proposed = false;
        var proposedHow = false;
        var reported = false;

        using var list = JsonDocument.Parse(ClaudeClarificationServer.Handle("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", RootTools, ref raised, ref proposed, ref proposedHow, ref reported)!.ToJsonString());
        var descriptions = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("description").GetString()!);

        Assert.Contains("BEFORE any WHAT or HOW", descriptions[AgentClarificationSignal.ToolName], StringComparison.Ordinal);
        Assert.Contains("not a question", descriptions[AgentWhatSignal.ToolName], StringComparison.Ordinal);
        Assert.Contains("not a question", descriptions[AgentHowSignal.ToolName], StringComparison.Ordinal);
        Assert.Contains("stop and wait", descriptions[AgentReclassificationSignal.ToolName], StringComparison.Ordinal);
        Assert.Contains("Do not use it for routine work", descriptions[AgentReviewRequestSignal.ToolName], StringComparison.Ordinal);
        Assert.Contains("cannot be withdrawn", descriptions[AgentProveRequestSignal.ToolName], StringComparison.Ordinal);
    }

    [Fact]
    public void The_reviewers_instructions_are_their_whole_role_and_carry_no_signal_orientation()
    {
        var (instructions, _) = Initialize(ClaudeClarificationServer.Tools.Review);

        Assert.StartsWith("You are an independent reviewer", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("signals to the workflow controller", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provers_instructions_are_their_whole_role_and_carry_no_signal_orientation()
    {
        var (instructions, _) = Initialize(ClaudeClarificationServer.Tools.Prove);

        Assert.StartsWith("You are an independent prover", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("signals to the workflow controller", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_given_no_signal_tool_gets_no_signal_orientation()
    {
        var (instructions, _) = Initialize(ClaudeClarificationServer.Tools.None);

        Assert.Empty(instructions);
    }
}
