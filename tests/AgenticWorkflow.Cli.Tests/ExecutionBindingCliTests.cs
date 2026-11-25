using System.Text.Json;
using AgenticWorkflow.Application;
using AgenticWorkflow.Application.Direct;
using AgenticWorkflow.Application.Execution;
using AgenticWorkflow.Cli.Configuration;
using AgenticWorkflow.Cli.Runtime;
using AgenticWorkflow.Cli.State;
using AgenticWorkflow.Core.Gates;
using AgenticWorkflow.Core.Identity;
using AgenticWorkflow.Core.Lifecycle;
using AgenticWorkflow.Core.Reviews;
using AgenticWorkflow.Core.Roles;

namespace AgenticWorkflow.Cli.Tests;

/// <summary>
/// Explicit role -> profile -> harness -> model -> reasoning bindings through the real CLI composition. A role runs under the profile that was named for it, the resolved configuration
/// is recorded with the run and is what is launched (also on resume), a role that needs a profile and has none is refused or waits for an explicit bind (never defaulted), and a profile
/// that cannot be honoured is refused before anything starts. Only the agent runtime is scripted.
/// </summary>
public sealed class ExecutionBindingCliTests
{
    private const string Profiles = """
        "profiles": {
          "claude-main": { "harness": "claude-code", "model": "opus", "reasoning": "high", "description": "main implementer" },
          "claude-review": { "harness": "claude-code", "model": "sonnet", "reasoning": "low" },
          "claude-plain": { "harness": "claude-code" },
          "pi-main": { "harness": "pi", "model": "some-model" },
          "pi-qwen": { "harness": "pi", "model": "llama/Qwen3.8-27B", "reasoning": "low" }
        },
        """;

    private static AgentReviewSubmission Pass() => new("PASS", "Nothing blocks.", [], null);

    /// <summary>
    /// With <paramref name="external"/> the configuration lives outside the workspace (passed with <c>--config</c>), so a test can edit it between a run and its continuation without that edit
    /// itself being a change to the workspace the controller checks.
    /// </summary>
    private static CliHarness NewHarness(string profile = "claude-main", string extra = "", string? acceptance = "none", bool external = false)
    {
        var config = CliHarness.ValidConfig(profile: profile, acceptance: acceptance, extra: Profiles + extra);
        var harness = new CliHarness(external ? null : config);

        if (external)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExternalConfigPath(harness))!);
            File.WriteAllText(ExternalConfigPath(harness), config);
        }

        harness.Factory.Conversations = new ScriptedConversations(harness.Factory.Runtime);
        harness.Factory.Signals = new ScriptedSignals();
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };

        return harness;
    }

    private static async Task<WorkflowTask> LoadAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Task;

    private static async Task<long> VersionAsync(CliHarness harness, TaskId runId)
        => (await (await new RunStore(harness.Home).OpenExistingAsync(runId, CancellationToken.None)).LoadAsync(runId)).Version;

    private static bool NothingWasCreated(CliHarness harness)
        => harness.Factory.Created == 0 && harness.Factory.Runtime.StartRequests.Count == 0 && new RunStore(harness.Home).ListRunIds().Count == 0;

    private static string ExternalConfigPath(CliHarness harness) => Path.Combine(Directory.GetParent(harness.Home)!.FullName, "external-config", ProjectResolver.DefaultConfigFileName);

    private static void Rewrite(CliHarness harness, Func<string, string> change)
        => File.WriteAllText(ExternalConfigPath(harness), change(File.ReadAllText(ExternalConfigPath(harness))));

    // ---- the profile the role uses, recorded with the run ---------------------------------------------------------------------------

    [Fact]
    public async Task The_projects_root_profile_is_resolved_recorded_and_launched_with_its_model_and_reasoning()
    {
        using var harness = NewHarness();

        Assert.Equal(0, await harness.RunAsync("run", "task"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        var binding = task.RoleBindings[DirectPathRoles.RootRoleId];
        Assert.Equal(new ProfileId("claude-main"), binding.Profile.Id);
        Assert.Equal("claude-code", binding.Execution!.Harness);
        Assert.Equal("opus", binding.Execution.Model);
        Assert.Equal("high", binding.Execution.Reasoning);
        Assert.False(string.IsNullOrEmpty(binding.Execution.Fingerprint));

        var launched = Assert.Single(harness.Factory.Launches).Find(DirectPathRoles.RootRoleId)!;
        Assert.Equal(binding.Execution, launched.Execution);
        Assert.False(launched.IsLegacy);
        Assert.Contains("Profile: claude-main (claude-code, model opus, reasoning high)", harness.Out.ToString());
    }

    [Fact]
    public async Task An_explicit_profile_on_the_command_line_wins_over_the_projects_and_is_the_one_recorded()
    {
        using var harness = NewHarness(profile: "claude-main");

        Assert.Equal(0, await harness.RunAsync("run", "task", "--profile", "claude-plain"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        var binding = task.RoleBindings[DirectPathRoles.RootRoleId];
        Assert.Equal(new ProfileId("claude-plain"), binding.Profile.Id);
        Assert.Null(binding.Execution!.Model);          // not chosen: recorded as not chosen, never filled in
        Assert.Null(binding.Execution.Reasoning);
    }

    [Fact]
    public async Task A_role_bound_by_name_on_the_command_line_runs_under_that_profile_and_the_root_keeps_its_own()
    {
        using var harness = NewHarness();
        harness.Factory.Signals!.ReviewByCall[1] = Pass();

        Assert.Equal(0, await harness.RunAsync("run", "task", "--require", "review", "--reviewer-profile", "claude-review"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        var root = task.RoleBindings[DirectPathRoles.RootRoleId].Execution!;
        var reviewer = task.RoleBindings[DirectPathRoles.ReviewerRoleId].Execution!;
        Assert.Equal(("opus", "high"), (root.Model, root.Reasoning));
        Assert.Equal(("sonnet", "low"), (reviewer.Model, reviewer.Reasoning));

        var launched = harness.Factory.Launches.Single();
        Assert.Equal("sonnet", launched.Find(DirectPathRoles.ReviewerRoleId)!.Execution.Model);
        Assert.Equal("opus", launched.Find(DirectPathRoles.RootRoleId)!.Execution.Model);
    }

    [Fact]
    public async Task The_built_in_profile_maps_the_old_single_profile_losslessly_and_chooses_no_model_or_reasoning()
    {
        using var harness = NewHarness(profile: "claude-code-local");

        Assert.Equal(0, await harness.RunAsync("run", "task"));

        var binding = (await LoadAsync(harness, harness.PrintedRunId())).RoleBindings[DirectPathRoles.RootRoleId];
        Assert.Equal(new ProfileId("claude-code-local"), binding.Profile.Id);
        Assert.Equal(("claude-code", null, null), (binding.Execution!.Harness, binding.Execution.Model, binding.Execution.Reasoning));
    }

    // ---- nothing is defaulted, substituted or guessed -------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_named_on_the_command_line_that_the_project_does_not_define_is_refused_and_nothing_is_substituted()
    {
        using var harness = NewHarness();

        var exit = await harness.RunAsync("run", "task", "--profile", "does-not-exist");

        Assert.Equal(2, exit);
        Assert.Contains("unknown_profile", harness.Error.ToString());
        Assert.Contains("does-not-exist", harness.Error.ToString());
        Assert.True(NothingWasCreated(harness));
    }

    [Fact]
    public async Task A_pi_profile_the_installed_pi_cannot_honour_is_refused_by_name_before_anything_starts_and_is_never_run_by_claude()
    {
        using var harness = NewHarness(profile: "pi-main");
        harness.Factory.ExecutionProblems.Add("the installed Pi could not be found or asked what it supports");

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("unsupported_configuration", harness.Error.ToString());
        Assert.Contains("pi-main", harness.Error.ToString());
        Assert.Contains("installed Pi", harness.Error.ToString());
        Assert.True(NothingWasCreated(harness));
    }

    [Fact]
    public async Task A_model_or_reasoning_the_harness_cannot_honour_is_refused_naming_the_profile_and_is_not_dropped()
    {
        using var harness = NewHarness();
        harness.Factory.ExecutionProblems.Add("reasoning level 'high' is not offered by the installed Claude Code (it offers: low, medium)");

        var exit = await harness.RunAsync("run", "task");

        Assert.Equal(2, exit);
        Assert.Contains("unsupported_configuration", harness.Error.ToString());
        Assert.Contains("claude-main", harness.Error.ToString());
        Assert.Contains("'high'", harness.Error.ToString());
        Assert.True(NothingWasCreated(harness));
    }

    [Fact]
    public async Task A_required_review_with_no_reviewer_profile_named_anywhere_is_refused_and_the_root_profile_is_never_used_instead()
    {
        using var harness = NewHarness();

        var exit = await harness.RunAsync("run", "task", "--require", "review");

        Assert.Equal(2, exit);
        Assert.Contains("missing_binding", harness.Error.ToString());
        Assert.Contains("reviewer", harness.Error.ToString());
        Assert.True(NothingWasCreated(harness));
    }

    [Fact]
    public async Task A_role_that_is_not_required_needs_no_binding_and_a_plain_run_binds_only_the_root()
    {
        using var harness = NewHarness();

        Assert.Equal(0, await harness.RunAsync("run", "task"));

        var task = await LoadAsync(harness, harness.PrintedRunId());
        Assert.Equal([DirectPathRoles.RootRoleId], task.RoleBindings.Keys);
    }

    // ---- an agent asks for a review mid-run and nobody chose a reviewer ------------------------------------------------------------

    private static async Task<(CliHarness Harness, TaskId RunId)> WaitingForReviewerAsync()
    {
        var harness = NewHarness();
        harness.Factory.Signals!.ReviewRequestByTurn[1] = new AgentReviewRequestSignal("Worth a second look.");
        harness.Factory.Signals.ReviewByCall[1] = Pass();

        Assert.Equal(24, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        return (harness, runId);
    }

    [Fact]
    public async Task A_review_the_agent_asks_for_with_no_reviewer_bound_waits_and_status_names_the_missing_binding_in_structured_form()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;
        Assert.Single(harness.Factory.Runtime.StartRequests);          // no reviewer was started, with any profile

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString(), "--json"));

        using var json = JsonDocument.Parse(harness.Out.ToString());
        var missing = json.RootElement.GetProperty("missingBindings").EnumerateArray().Single();
        Assert.Equal("reviewer", missing.GetProperty("role").GetString());
        Assert.Equal("review_required", missing.GetProperty("reason").GetString());
        Assert.DoesNotContain(json.RootElement.GetProperty("bindings").EnumerateArray(), b => b.GetProperty("role").GetString() == "reviewer");

        var root = json.RootElement.GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("role").GetString() == "root");
        Assert.Equal("claude-main", root.GetProperty("profile").GetProperty("id").GetString());
        Assert.Equal("claude-code", root.GetProperty("configuration").GetProperty("harness").GetString());
        Assert.Equal("opus", root.GetProperty("configuration").GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("configuration").GetProperty("reasoning").GetString());
    }

    [Fact]
    public async Task Continuing_without_binding_the_missing_reviewer_is_refused_and_launches_nothing()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;
        var launches = harness.Factory.Launches.Count;

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.NotEqual(0, exit);
        Assert.Contains("missing_binding", harness.Error.ToString());
        Assert.Contains("aw bind", harness.Error.ToString());
        Assert.Equal(launches, harness.Factory.Launches.Count);
    }

    [Fact]
    public async Task An_explicit_bind_then_continue_runs_the_reviewer_under_exactly_the_bound_profile()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;

        Assert.Equal(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));
        Assert.Contains("bound to profile 'claude-review'", harness.Out.ToString());
        Assert.Contains("Nothing was started", harness.Out.ToString());
        Assert.Equal(1, harness.Factory.Created);
        Assert.Equal("sonnet", (await LoadAsync(harness, runId)).RoleBindings[DirectPathRoles.ReviewerRoleId].Execution!.Model);

        harness.ResetOutput();
        Assert.Equal(0, await harness.RunAsync("continue", runId.ToString()));

        var done = await LoadAsync(harness, runId);
        Assert.Equal(WorkflowState.Completed, done.WorkflowState);
        Assert.Single(done.Reviews);
        var resumed = harness.Factory.Launches.Last();
        Assert.Equal("sonnet", resumed.Find(DirectPathRoles.ReviewerRoleId)!.Execution.Model);
        Assert.Equal("opus", resumed.Find(DirectPathRoles.RootRoleId)!.Execution.Model);
    }

    [Fact]
    public async Task Binding_the_same_role_to_the_same_profile_again_is_an_idempotent_no_op()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;
        Assert.Equal(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));
        var version = await VersionAsync(harness, runId);
        harness.ResetOutput();

        Assert.Equal(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));

        Assert.Contains("already bound", harness.Out.ToString());
        Assert.Equal(version, await VersionAsync(harness, runId));
    }

    [Fact]
    public async Task Binding_a_role_to_a_different_profile_than_it_has_is_refused_and_the_recorded_binding_stands()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;
        Assert.Equal(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));
        harness.ResetOutput();

        var exit = await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-plain");

        Assert.Equal(18, exit);
        Assert.Contains("already_bound", harness.Error.ToString());
        Assert.Equal(new ProfileId("claude-review"), (await LoadAsync(harness, runId)).RoleBindings[DirectPathRoles.ReviewerRoleId].Profile.Id);

        // the root, bound when the run started, cannot be swapped for another profile either
        Assert.Equal(18, await harness.RunAsync("bind", runId.ToString(), "--role", "root", "--profile", "claude-plain"));
        Assert.Equal(new ProfileId("claude-main"), (await LoadAsync(harness, runId)).RoleBindings[DirectPathRoles.RootRoleId].Profile.Id);
    }

    [Theory]
    [InlineData("claude-unknown", "unknown_profile")]
    [InlineData("pi-main", "unsupported_configuration")]
    public async Task A_bind_to_a_profile_that_is_unknown_or_cannot_be_run_is_refused_and_records_nothing(string profile, string code)
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;

        if (profile == "pi-main")
            harness.Factory.ExecutionProblems.Add("the installed Pi could not be found or asked what it supports");

        var exit = await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", profile);

        Assert.Equal(2, exit);
        Assert.Contains(code, harness.Error.ToString());
        Assert.DoesNotContain(DirectPathRoles.ReviewerRoleId, (await LoadAsync(harness, runId)).RoleBindings.Keys);
    }

    [Fact]
    public async Task A_bind_that_the_harness_cannot_honour_is_refused_and_records_nothing()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;
        harness.Factory.ExecutionProblems.Add("model 'sonnet' is not accepted");

        Assert.Equal(2, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));

        Assert.Contains("unsupported_configuration", harness.Error.ToString());
        Assert.DoesNotContain(DirectPathRoles.ReviewerRoleId, (await LoadAsync(harness, runId)).RoleBindings.Keys);
    }

    [Fact]
    public async Task A_bind_on_a_finished_run_or_for_a_role_that_cannot_be_bound_is_refused()
    {
        using var harness = NewHarness();
        Assert.Equal(0, await harness.RunAsync("run", "task"));
        var runId = harness.PrintedRunId();
        harness.ResetOutput();

        Assert.NotEqual(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));
        Assert.NotEqual(0, await harness.RunAsync("bind", runId.ToString(), "--role", "implementer", "--profile", "claude-review"));
        Assert.DoesNotContain(DirectPathRoles.ReviewerRoleId, (await LoadAsync(harness, runId)).RoleBindings.Keys);
    }

    [Fact]
    public async Task A_bind_is_refused_while_another_process_holds_the_run()
    {
        var (harness, runId) = await WaitingForReviewerAsync();
        using var _ = harness;

        using (new RunStore(harness.Home).AcquireLiveLock(runId))
            Assert.NotEqual(0, await harness.RunAsync("bind", runId.ToString(), "--role", "reviewer", "--profile", "claude-review"));

        Assert.DoesNotContain(DirectPathRoles.ReviewerRoleId, (await LoadAsync(harness, runId)).RoleBindings.Keys);
    }

    // ---- the run keeps what it recorded -------------------------------------------------------------------------------------------

    /// <summary>A run awaiting its ACCEPT gate whose request-changes continuation then failed once: BLOCKED, recoverable, with the root bound to claude-main (opus, high).</summary>
    private static async Task<(CliHarness Harness, TaskId RunId)> BlockedAsync(string profile = "claude-main")
    {
        var harness = NewHarness(profile: profile, acceptance: "human", external: true);
        Assert.Equal(17, await harness.RunAsync("run", "task", "--config", ExternalConfigPath(harness)));
        var runId = harness.PrintedRunId();
        var gateId = harness.PrintedGateId();
        harness.Factory.Conversations!.ResumeFailure = new AgentRuntimeException("the core is unreachable");
        Assert.Equal(14, await harness.RunAsync("request-changes", runId.ToString(), "--gate", gateId.ToString(), "--feedback", "Add a test."));
        harness.Factory.Conversations.ResumeFailure = null;
        harness.Factory.Runtime.Turn = (_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "src", "Feature.txt"), CliHarness.Symbol + "\n// revised after the requested changes\n");
            return Task.FromResult(AgentTurnOutcome.Done);
        };
        harness.ResetOutput();

        return (harness, runId);
    }

    [Fact]
    public async Task A_continuation_launches_the_recorded_binding_even_after_the_project_changed_the_profile()
    {
        var (harness, runId) = await BlockedAsync();
        using var _ = harness;
        Rewrite(harness, c => c.Replace("\"model\": \"opus\"", "\"model\": \"haiku\"").Replace("\"reasoning\": \"high\"", "\"reasoning\": \"low\""));
        var before = harness.Factory.Launches.Count;

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.True(exit == 17, harness.Out.ToString() + harness.Error);
        var resumed = harness.Factory.Launches.Skip(before).Single().Find(DirectPathRoles.RootRoleId)!;
        Assert.Equal(("opus", "high"), (resumed.Execution.Model, resumed.Execution.Reasoning));       // what the run recorded, not what the file says now
        var recorded = (await LoadAsync(harness, runId)).RoleBindings[DirectPathRoles.RootRoleId].Execution!;
        Assert.Equal(("opus", "high"), (recorded.Model, recorded.Reasoning));
    }

    [Fact]
    public async Task A_pi_binding_is_recorded_launched_and_replayed_on_resume_exactly_like_a_claude_one_even_after_the_project_changed_it()
    {
        var (harness, runId) = await BlockedAsync("pi-qwen");
        using var _ = harness;

        var recorded = (await LoadAsync(harness, runId)).RoleBindings[DirectPathRoles.RootRoleId].Execution!;
        Assert.Equal(("pi", "llama/Qwen3.8-27B", "low"), (recorded.Harness, recorded.Model, recorded.Reasoning));
        Assert.All(harness.Factory.Launches, launch => Assert.Equal("pi", launch.Find(DirectPathRoles.RootRoleId)!.Execution.Harness));   // nothing here ran as Claude

        Rewrite(harness, c => c.Replace("\"harness\": \"pi\", \"model\": \"llama/Qwen3.8-27B\", \"reasoning\": \"low\"", "\"harness\": \"claude-code\", \"model\": \"opus\", \"reasoning\": \"high\""));
        var before = harness.Factory.Launches.Count;

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.True(exit == 17, harness.Out.ToString() + harness.Error);
        var resumed = harness.Factory.Launches.Skip(before).Single().Find(DirectPathRoles.RootRoleId)!;
        Assert.Equal(("pi", "llama/Qwen3.8-27B", "low"), (resumed.Execution.Harness, resumed.Execution.Model, resumed.Execution.Reasoning));   // what the run recorded, not what the file says now
    }

    [Fact]
    public async Task A_continuation_still_works_when_the_profile_it_recorded_was_removed_from_the_project()
    {
        var (harness, runId) = await BlockedAsync();
        using var _ = harness;
        Rewrite(harness, c => c.Replace("\"claude-main\": {", "\"claude-renamed\": {").Replace("\"profile\": \"claude-main\"", "\"profile\": \"claude-renamed\""));
        var before = harness.Factory.Launches.Count;

        var exit = await harness.RunAsync("continue", runId.ToString());
        Assert.True(exit == 17, harness.Out.ToString() + harness.Error);

        var resumed = harness.Factory.Launches.Skip(before).Single().Find(DirectPathRoles.RootRoleId)!;
        Assert.Equal(new ProfileId("claude-main"), resumed.Profile);
        Assert.Equal("opus", resumed.Execution.Model);
    }

    [Fact]
    public async Task A_change_to_what_the_work_is_still_refuses_a_continuation_even_though_profiles_may_change()
    {
        var (harness, runId) = await BlockedAsync();
        using var _ = harness;
        Rewrite(harness, c => c.Replace("\"maxTurns\": 3", "\"maxTurns\": 2"));
        var before = harness.Factory.Launches.Count;

        Assert.Equal(2, await harness.RunAsync("continue", runId.ToString()));
        Assert.Contains("changed since this run started", harness.Error.ToString());

        Assert.Equal(before, harness.Factory.Launches.Count);
    }

    [Fact]
    public async Task A_continuation_whose_recorded_configuration_can_no_longer_be_honoured_is_refused_by_name_and_nothing_is_substituted()
    {
        var (harness, runId) = await BlockedAsync();
        using var _ = harness;
        harness.Factory.ExecutionProblems.Add("reasoning level 'high' is not offered by the installed Claude Code");
        var before = harness.Factory.Launches.Count;

        var exit = await harness.RunAsync("continue", runId.ToString());

        Assert.NotEqual(0, exit);
        Assert.Contains("unsupported_configuration", harness.Error.ToString());
        Assert.Contains("claude-main", harness.Error.ToString());
        Assert.Equal(before, harness.Factory.Launches.Count);
        Assert.Equal(WorkflowState.Blocked, (await LoadAsync(harness, runId)).WorkflowState);
    }

    // ---- runs recorded before bindings carried a configuration ----------------------------------------------------------------------

    /// <summary>A run exactly as an older build left it: the root is bound to the project's profile with no execution configuration recorded.</summary>
    private static async Task<TaskId> LegacyRunAsync(CliHarness harness)
    {
        var store = new RunStore(harness.Home);
        var runId = new TaskId(Guid.CreateVersion7());
        var project = ProjectResolver.Resolve(harness.Workspace, null, harness.Workspace);
        store.Create(runId, new RunMetadata(RunMetadata.CurrentVersion, runId.ToString(), harness.Workspace, DateTimeOffset.UtcNow, project.ConfigPath, project.ConfigSha256));
        var tasks = await store.OpenNewAsync(runId, CancellationToken.None);
        var created = await tasks.CreateAsync(runId, "task", CompletionBoundary.WorkingTree, AcceptanceRequirement.HumanRequired, CancellationToken.None);
        await tasks.BindRoleAsync(created.Snapshot, DirectPathRoles.Root, project.ProfileDefinition, CancellationToken.None);

        return runId;
    }

    [Fact]
    public async Task A_run_recorded_before_execution_configuration_existed_replays_with_null_configuration_and_launches_as_the_built_in_profile()
    {
        using var harness = NewHarness(profile: "claude-code-local", acceptance: "human");
        var runId = await LegacyRunAsync(harness);

        Assert.Equal(0, await harness.RunAsync("status", runId.ToString(), "--json"));
        using var json = JsonDocument.Parse(harness.Out.ToString());
        var binding = json.RootElement.GetProperty("bindings").EnumerateArray().Single();
        Assert.Equal("claude-code-local", binding.GetProperty("profile").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("configuration").ValueKind);

        var launch = Assert.Single(RunBindings.From(await LoadAsync(harness, runId)).All);
        Assert.True(launch.IsLegacy);
        Assert.Equal(("claude-code", null, null), (launch.Execution.Harness, launch.Execution.Model, launch.Execution.Reasoning));
    }

    [Fact]
    public async Task A_legacy_binding_of_any_other_profile_is_refused_rather_than_guessed()
    {
        using var harness = NewHarness();                        // the legacy run is bound to claude-main, which an older build never recorded a configuration for
        var runId = await LegacyRunAsync(harness);

        var task = await LoadAsync(harness, runId);

        Assert.Throws<ConfigurationException>(() => RunBindings.From(task));
    }
}
